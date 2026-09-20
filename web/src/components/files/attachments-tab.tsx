import { createElement, useState } from "react";
import { useTranslation } from "react-i18next";
import { Divider, Group, Pagination, Skeleton, Stack, Text } from "@mantine/core";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { SearchInput } from "@/components/crm/search-input";
import { LoadError } from "@/components/load-error";
import { PlanLimitMessage } from "@/components/subscription/plan-limit-message";
import { useDeleteFile, useFileLimits, useFiles, useUploadQueue } from "@/hooks/use-files";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { FILE_QUOTA_EXCEEDED } from "@/lib/file-errors";
import { ATTACHMENT_PERMISSIONS, FILES_PAGE_SIZE } from "@/lib/files";
import { downloadBlob } from "@/lib/platform";
import { fetchFileBlob } from "@/services/files.service";
import { PERMISSIONS, type AttachmentRecordType, type FileAttachment } from "@/types";
import { FileDropzone } from "./file-dropzone";
import { FileList } from "./file-list";
import { FilePreviewDialog } from "./file-preview-dialog";
import { RenameFileDialog } from "./rename-file-dialog";
import { UploadQueue } from "./upload-queue";

interface AttachmentsTabProps {
  recordType: AttachmentRecordType;
  recordId: string;
  /** Smaller layout for a dialog (activity form). */
  compact?: boolean;
  /** Section heading above the content (dialogs); the tab itself is named by its tab label. */
  heading?: string;
}

/**
 * "Ekler" of a record: drop area with the upload queue, then the attachments table with download,
 * preview, rename and delete. Everything follows the record's own permission: nothing at all without
 * `read` (no request is sent), the drop area and the row edit actions only with `write` - which
 * `usePermission` already withdraws while the tenant is read-only. The server enforces both anyway.
 */
export function AttachmentsTab({
  recordType,
  recordId,
  compact = false,
  heading,
}: AttachmentsTabProps) {
  const { t } = useTranslation(["files", "common"]);
  const permissions = ATTACHMENT_PERMISSIONS[recordType];
  const canRead = usePermission(permissions.read);
  const canWrite = usePermission(permissions.write);
  const canManagePlan = usePermission(PERMISSIONS.orgSettingsManage);

  const [page, setPage] = useState(1);
  const [q, setQ] = useState("");
  const [sort, setSort] = useState("-uploadedAt");
  const [previewing, setPreviewing] = useState<FileAttachment | null>(null);
  const [renaming, setRenaming] = useState<FileAttachment | null>(null);
  const [deleting, setDeleting] = useState<FileAttachment | null>(null);
  const [downloadingId, setDownloadingId] = useState<string>();

  const limits = useFileLimits(canRead);
  const files = useFiles(
    { recordType, recordId, q: q || undefined, sort, page, pageSize: FILES_PAGE_SIZE },
    canRead
  );
  const remove = useDeleteFile();
  const queue = useUploadQueue({
    recordType,
    recordId,
    limits: limits.data,
    onError: (error) => {
      // A reached storage quota is worth more than a row message: say where the limit lives.
      if (error.code !== FILE_QUOTA_EXCEEDED) return;
      toast({
        variant: "destructive",
        title: t("files:quota.title"),
        description: canManagePlan
          ? createElement(PlanLimitMessage, { message: error.message })
          : error.message,
      });
    },
  });

  if (!canRead) return null;

  async function download(file: FileAttachment) {
    if (downloadingId) return;
    setDownloadingId(file.id);
    try {
      const blob = await fetchFileBlob(file.id, "attachment");
      downloadBlob(file.name, blob);
    } catch (error) {
      // file.quarantined (409), file.content_missing (410), file.storage_unavailable (503), ...
      toastApiError(error);
    } finally {
      setDownloadingId(undefined);
    }
  }

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("files:delete.done") });
      // Deleting the last row of a later page: step back instead of showing an empty page.
      if (files.data?.items.length === 1 && page > 1) setPage(page - 1);
    } catch (error) {
      toastApiError(error);
    } finally {
      setDeleting(null);
    }
  }

  const total = files.data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / FILES_PAGE_SIZE));
  const items = files.data?.items ?? [];

  return (
    <Stack gap="md" data-testid="attachments-tab">
      {heading && <Divider label={heading} labelPosition="left" />}
      {canWrite && (
        <Stack gap="sm">
          <FileDropzone limits={limits.data} onFiles={queue.add} compact={compact} />
          <UploadQueue
            items={queue.items}
            onCancel={queue.cancel}
            onRetry={queue.retry}
            onDismiss={queue.dismiss}
          />
        </Stack>
      )}

      {!compact && (total > 0 || q) && (
        <Group justify="space-between" wrap="wrap">
          <SearchInput
            value={q}
            onSearch={(value) => {
              setQ(value);
              setPage(1);
            }}
            placeholder={t("files:search")}
          />
          <Text size="sm" c="dimmed">
            {t("files:total", { count: total })}
          </Text>
        </Group>
      )}

      {files.error ? (
        <LoadError error={files.error} onRetry={() => void files.refetch()} />
      ) : files.isLoading ? (
        <Stack gap="xs">
          <Skeleton h={40} />
          <Skeleton h={40} />
        </Stack>
      ) : items.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="lg" data-testid="files-empty">
          {q ? t("files:emptySearch") : canWrite ? t("files:emptyWritable") : t("files:empty")}
        </Text>
      ) : (
        <FileList
          files={items}
          canWrite={canWrite}
          sort={sort}
          onSort={(next) => {
            setSort(next);
            setPage(1);
          }}
          compact={compact}
          downloadingId={downloadingId}
          onDownload={(file) => void download(file)}
          onPreview={setPreviewing}
          onRename={setRenaming}
          onDelete={setDeleting}
        />
      )}

      {total > FILES_PAGE_SIZE && (
        <Group justify="flex-end">
          <Pagination total={totalPages} value={page} onChange={setPage} size="sm" />
        </Group>
      )}

      {previewing && (
        <FilePreviewDialog
          file={previewing}
          onClose={() => setPreviewing(null)}
          onDownload={(file) => void download(file)}
        />
      )}
      {renaming && <RenameFileDialog file={renaming} onClose={() => setRenaming(null)} />}
      <ConfirmDialog
        opened={!!deleting}
        title={t("files:delete.title")}
        message={t("files:delete.message", { name: deleting?.name ?? "" })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </Stack>
  );
}
