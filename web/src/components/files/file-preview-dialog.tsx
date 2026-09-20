import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Alert, Button, Group, Loader, Modal, Stack, Text } from "@mantine/core";
import { Download } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { previewableType, type PreviewableType } from "@/lib/files";
import { fetchFileBlob } from "@/services/files.service";
import type { FileAttachment } from "@/types";

interface FilePreviewDialogProps {
  file: FileAttachment;
  onClose: () => void;
  onDownload: (file: FileAttachment) => void;
}

interface PreviewUrl {
  /** Object URL of the blob; undefined when the type is not on the preview allow-list. */
  url?: string;
  type?: PreviewableType;
}

/**
 * Preview of an image or a PDF: the bytes are fetched with the bearer token (`disposition=inline`)
 * and shown from a blob URL. The blob only becomes a URL when its type is on the allow-list (png,
 * jpeg, gif, webp, pdf) - the second lock next to the server's; anything else says "cannot preview"
 * with a download button. The URL is revoked when the dialog closes; Escape closes it.
 */
export function FilePreviewDialog({ file, onClose, onDownload }: FilePreviewDialogProps) {
  const { t } = useTranslation(["files", "common"]);
  const previewable = file.canPreview;

  const { data, isLoading, error, refetch } = useQuery<PreviewUrl>({
    queryKey: ["files", "preview", file.id],
    enabled: previewable,
    // Blobs do not belong in the query cache: keep nothing once the dialog is gone.
    gcTime: 0,
    staleTime: 0,
    retry: false,
    queryFn: async ({ signal }) => {
      const blob = await fetchFileBlob(file.id, "inline", signal);
      const type = previewableType(blob.type);
      if (!type || signal.aborted) return {};
      // A fresh blob carrying only the allow-listed type (no parameters, no sniffable surprises).
      return { url: URL.createObjectURL(new Blob([blob], { type })), type };
    },
  });

  const url = data?.url;
  useEffect(() => {
    return () => {
      if (url) URL.revokeObjectURL(url);
    };
  }, [url]);

  const cannotPreview = !previewable || (!!data && !data.url);

  return (
    <Modal opened onClose={onClose} title={file.name} size="xl" centered>
      <Stack gap="md" data-testid="file-preview">
        {error ? (
          <LoadError error={error} onRetry={() => void refetch()} />
        ) : cannotPreview ? (
          <Alert color="gray" variant="light" title={t("files:preview.unavailable")}>
            <Stack gap="xs" align="flex-start">
              <Text size="sm">{t("files:preview.unavailableHint")}</Text>
              <Button
                size="xs"
                variant="default"
                leftSection={<Download size={14} />}
                onClick={() => onDownload(file)}
              >
                {t("files:actions.download")}
              </Button>
            </Stack>
          </Alert>
        ) : isLoading || !url ? (
          <Group justify="center" py="xl">
            <Loader size="sm" aria-label={t("files:preview.loading")} />
          </Group>
        ) : data?.type === "application/pdf" ? (
          <iframe
            title={file.name}
            src={url}
            style={{ width: "100%", height: "70vh", border: 0 }}
          />
        ) : (
          <img
            src={url}
            alt={file.name}
            style={{ maxWidth: "100%", maxHeight: "70vh", objectFit: "contain", margin: "0 auto" }}
          />
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t("common:close")}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
