import { useTranslation } from "react-i18next";
import { ActionIcon, Group, Progress, Stack, Text, Tooltip } from "@mantine/core";
import { RotateCcw, X } from "lucide-react";
import type { UploadItem } from "@/hooks/use-files";
import { extensionOf, formatBytes } from "@/lib/files";
import { FileIcon } from "./file-icon";

interface UploadQueueProps {
  items: UploadItem[];
  onCancel: (id: string) => void;
  onRetry: (id: string) => void;
  onDismiss: (id: string) => void;
}

/** One row per file in flight: name, size, progress bar (or the error with retry) and cancel / dismiss. */
export function UploadQueue({ items, onCancel, onRetry, onDismiss }: UploadQueueProps) {
  const { t } = useTranslation(["files"]);
  if (items.length === 0) return null;
  return (
    <Stack gap="xs" role="list" aria-label={t("files:queue.title")} data-testid="upload-queue">
      {items.map((item) => (
        <div key={item.id} role="listitem" data-testid="upload-row" data-status={item.status}>
          <Group gap="sm" wrap="nowrap" align="flex-start">
            <FileIcon extension={extensionOf(item.file.name)} />
            <Stack gap={4} flex={1} miw={0}>
              <Group justify="space-between" gap="xs" wrap="nowrap">
                <Text size="sm" fw={500} truncate>
                  {item.file.name}
                </Text>
                <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>
                  {formatBytes(item.file.size)}
                </Text>
              </Group>
              {item.status === "error" ? (
                <Text size="xs" c="red" role="alert">
                  {item.error?.message}
                </Text>
              ) : (
                <>
                  <Progress
                    value={item.progress}
                    size="sm"
                    radius="xl"
                    animated={item.status === "uploading"}
                    aria-label={t("files:queue.progressOf", { name: item.file.name })}
                    aria-valuetext={
                      item.status === "queued"
                        ? t("files:queue.waiting")
                        : t("files:queue.percent", { percent: item.progress })
                    }
                  />
                  <Text size="xs" c="dimmed">
                    {item.status === "queued"
                      ? t("files:queue.waiting")
                      : t("files:queue.percent", { percent: item.progress })}
                  </Text>
                </>
              )}
            </Stack>
            <Group gap={4} wrap="nowrap">
              {item.status === "error" && (
                <Tooltip label={t("files:queue.retry")}>
                  <ActionIcon
                    variant="subtle"
                    aria-label={t("files:queue.retryOf", { name: item.file.name })}
                    onClick={() => onRetry(item.id)}
                  >
                    <RotateCcw size={16} />
                  </ActionIcon>
                </Tooltip>
              )}
              <Tooltip label={item.status === "error" ? t("files:queue.dismiss") : t("files:queue.cancel")}>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  aria-label={
                    item.status === "error"
                      ? t("files:queue.dismissOf", { name: item.file.name })
                      : t("files:queue.cancelOf", { name: item.file.name })
                  }
                  onClick={() => (item.status === "error" ? onDismiss(item.id) : onCancel(item.id))}
                >
                  <X size={16} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Group>
        </div>
      ))}
    </Stack>
  );
}
