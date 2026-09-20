import { useEffect, useRef, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import axios from "axios";
import { describeFileError, fileErrorText, type FileErrorInfo } from "@/lib/file-errors";
import { MAX_CONCURRENT_UPLOADS, validateClientFile } from "@/lib/files";
import {
  deleteFile,
  filesKeys,
  getFileLimits,
  getFilesUsage,
  listFiles,
  renameFile,
  uploadFile,
} from "@/services/files.service";
import { subscriptionKeys } from "@/services/subscription.service";
import type { AttachmentRecordType, FileLimits, FileListQuery } from "@/types";

/** The attachments of one record (`GET /files`); `enabled` false = no request (no read permission). */
export function useFiles(query: FileListQuery, enabled = true) {
  return useQuery({
    queryKey: filesKeys.list(query),
    queryFn: () => listFiles(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

/** Server limits for the client side pre-checks; cached for a long while (configuration, not data). */
export function useFileLimits(enabled = true) {
  return useQuery({
    queryKey: filesKeys.limits,
    queryFn: getFileLimits,
    staleTime: 10 * 60_000,
    enabled,
  });
}

/** Live storage usage (`org.settings.manage`): total, quarantined / missing counts, per record type. */
export function useFilesUsage(enabled = true) {
  return useQuery({ queryKey: filesKeys.usage, queryFn: getFilesUsage, enabled });
}

/** Lists, the usage numbers and the plan page's usage bars all change with an upload or a delete. */
function useRefreshFiles() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ["files", "list"] }),
      queryClient.invalidateQueries({ queryKey: filesKeys.usage }),
      queryClient.invalidateQueries({ queryKey: subscriptionKeys.all }),
    ]);
}

export function useRenameFile() {
  const refresh = useRefreshFiles();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) => renameFile(id, name),
    onSuccess: () => refresh(),
  });
}

export function useDeleteFile() {
  const refresh = useRefreshFiles();
  return useMutation({
    mutationFn: (id: string) => deleteFile(id),
    onSuccess: () => refresh(),
  });
}

// ---- Upload queue -----------------------------------------------------------------------------------

export type UploadStatus = "queued" | "uploading" | "error";

export interface UploadItem {
  id: string;
  file: File;
  recordType: AttachmentRecordType;
  recordId: string;
  status: UploadStatus;
  /** 0-100. */
  progress: number;
  /** Set while `status` is `error`. */
  error?: FileErrorInfo;
}

interface UploadQueueOptions {
  recordType: AttachmentRecordType;
  recordId: string;
  /** Client side pre-checks; a file that fails one becomes an error row without any request. */
  limits: FileLimits | undefined;
  /** Every failure (client pre-check or server), e.g. for the quota toast. */
  onError?: (error: FileErrorInfo, file: File) => void;
}

/**
 * Upload queue of one record: at most `MAX_CONCURRENT_UPLOADS` requests at a time, one file per
 * request (own progress, own error, own cancel), a finished row leaves the queue once the list has
 * been refetched. Leaving the page while uploads run asks for confirmation (`beforeunload`).
 * Cancelling a row aborts its request; an error row can be retried or dismissed.
 */
export function useUploadQueue({ recordType, recordId, limits, onError }: UploadQueueOptions) {
  const refresh = useRefreshFiles();
  const [items, setItems] = useState<UploadItem[]>([]);
  // The authoritative queue (event handlers and request callbacks read and write it synchronously);
  // `items` is its rendered copy.
  const queue = useRef<UploadItem[]>([]);
  const controllers = useRef(new Map<string, AbortController>());
  const counter = useRef(0);
  const latest = useRef({ onError, refresh });
  useEffect(() => {
    latest.current = { onError, refresh };
  });

  function commit(next: UploadItem[]) {
    queue.current = next;
    setItems(next);
  }

  function patch(id: string, changes: Partial<UploadItem>) {
    commit(queue.current.map((item) => (item.id === id ? { ...item, ...changes } : item)));
  }

  function fail(item: UploadItem, error: FileErrorInfo) {
    patch(item.id, { status: "error", error });
    latest.current.onError?.(error, item.file);
  }

  async function run(item: UploadItem) {
    const controller = new AbortController();
    controllers.current.set(item.id, controller);
    try {
      const result = await uploadFile(item.recordType, item.recordId, item.file, {
        signal: controller.signal,
        onProgress: (progress) => {
          if (queue.current.some((entry) => entry.id === item.id)) patch(item.id, { progress });
        },
      });
      const failure = result.items.length === 0 ? result.failed[0] : undefined;
      if (failure) {
        fail(item, {
          code: failure.code,
          args: failure.args,
          message: fileErrorText(failure.code, failure.args),
        });
        return;
      }
      patch(item.id, { progress: 100 });
      await latest.current.refresh();
      commit(queue.current.filter((entry) => entry.id !== item.id));
    } catch (error) {
      // Cancelled by the user: `cancel` already dropped the row.
      if (controller.signal.aborted || axios.isCancel(error)) return;
      fail(item, describeFileError(error));
    } finally {
      controllers.current.delete(item.id);
      pump();
    }
  }

  function pump() {
    let active = queue.current.filter((item) => item.status === "uploading").length;
    const starting: UploadItem[] = [];
    for (const item of queue.current) {
      if (active >= MAX_CONCURRENT_UPLOADS) break;
      if (item.status === "queued") {
        starting.push(item);
        active += 1;
      }
    }
    if (starting.length === 0) return;
    const ids = new Set(starting.map((item) => item.id));
    commit(
      queue.current.map((item) =>
        ids.has(item.id) ? { ...item, status: "uploading", progress: 0, error: undefined } : item
      )
    );
    for (const item of starting) void run(item);
  }

  /** Adds the picked / dropped files; those failing a pre-check become error rows, the rest are queued. */
  function add(files: File[]) {
    const added: UploadItem[] = [];
    const rejected: Array<[UploadItem, FileErrorInfo]> = [];
    for (const file of files) {
      counter.current += 1;
      const item: UploadItem = {
        id: `upload-${counter.current}`,
        file,
        recordType,
        recordId,
        status: "queued",
        progress: 0,
      };
      const rejection = validateClientFile(file, limits);
      if (rejection) {
        const error = {
          ...rejection,
          message: fileErrorText(rejection.code, rejection.args),
        };
        added.push({ ...item, status: "error", error });
        rejected.push([item, error]);
      } else {
        added.push(item);
      }
    }
    if (added.length === 0) return;
    commit([...queue.current, ...added]);
    for (const [item, error] of rejected) latest.current.onError?.(error, item.file);
    pump();
  }

  function cancel(id: string) {
    controllers.current.get(id)?.abort();
    commit(queue.current.filter((item) => item.id !== id));
    pump();
  }

  function retry(id: string) {
    const item = queue.current.find((entry) => entry.id === id);
    if (!item || item.status !== "error") return;
    // The same pre-checks apply again (the limits may have loaded since).
    const rejection = validateClientFile(item.file, limits);
    if (rejection) {
      patch(id, { error: { ...rejection, message: fileErrorText(rejection.code, rejection.args) } });
      return;
    }
    patch(id, { status: "queued", progress: 0, error: undefined });
    pump();
  }

  const busy = items.some((item) => item.status !== "error");
  useEffect(() => {
    if (!busy) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [busy]);

  return { items, busy, add, cancel, retry, dismiss: cancel };
}
