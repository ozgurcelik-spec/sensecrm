/**
 * File attachments (M8C) - `/files`. Uploads are multipart, one file per request (per-file progress
 * and errors); downloads and previews are fetched as blobs through the authenticated client (the
 * token is a header, never a query parameter, and the API never hands out presigned URLs).
 */
import { apiClient } from "@/lib/api-client";
import { UPLOAD_TIMEOUT_MS } from "@/lib/files";
import type {
  FileAttachment,
  FileDisposition,
  FileLimits,
  FileListQuery,
  FilesUsage,
  FileUploadResult,
  ListResult,
} from "@/types";
import { getList, getOne, seg } from "./crm-http";

export const filesKeys = {
  all: ["files"] as const,
  list: (query: FileListQuery) => ["files", "list", query] as const,
  limits: ["files", "limits"] as const,
  usage: ["files", "usage"] as const,
};

export const listFiles = (query: FileListQuery): Promise<ListResult<FileAttachment>> =>
  getList<FileAttachment>("/files", { ...query });

export const getFileLimits = (): Promise<FileLimits> => getOne<FileLimits>("/files/limits");

export const getFilesUsage = (): Promise<FilesUsage> => getOne<FilesUsage>("/files/usage");

export interface UploadOptions {
  /** 0-100 of the request body sent so far. */
  onProgress?: (percent: number) => void;
  signal?: AbortSignal;
}

/**
 * Uploads one file. A 201 / 200 answer is `{ items, failed }`; when nothing was stored the server
 * answers the first error as ProblemDetails (the call rejects). A body that says otherwise (no item
 * and a `failed` entry) is turned into the same rejection shape by the caller (`useUploadQueue`).
 */
export async function uploadFile(
  recordType: string,
  recordId: string,
  file: File,
  { onProgress, signal }: UploadOptions = {}
): Promise<FileUploadResult> {
  const body = new FormData();
  body.append("file", file, file.name);
  const { data } = await apiClient.post<FileUploadResult>("/files", body, {
    params: { recordType, recordId },
    // The client default is JSON; axios turns a FormData into JSON under it. multipart/form-data
    // makes the browser add the boundary itself.
    headers: { "Content-Type": "multipart/form-data" },
    timeout: UPLOAD_TIMEOUT_MS,
    signal,
    onUploadProgress: (event) => {
      if (!onProgress) return;
      const fraction = event.progress ?? (event.total ? event.loaded / event.total : undefined);
      if (fraction !== undefined) onProgress(Math.min(100, Math.round(fraction * 100)));
    },
  });
  return { items: data?.items ?? [], failed: data?.failed ?? [] };
}

/** The file's bytes as a blob (`attachment` for a download, `inline` for a preview of an image / PDF). */
export async function fetchFileBlob(
  id: string,
  disposition: FileDisposition,
  signal?: AbortSignal
): Promise<Blob> {
  const { data } = await apiClient.get<Blob>(`/files/${seg(id)}/content`, {
    params: { disposition },
    responseType: "blob",
    timeout: UPLOAD_TIMEOUT_MS,
    signal,
  });
  return data;
}

export async function renameFile(id: string, name: string): Promise<void> {
  await apiClient.patch(`/files/${seg(id)}`, { name });
}

export async function deleteFile(id: string): Promise<void> {
  await apiClient.delete(`/files/${seg(id)}`);
}
