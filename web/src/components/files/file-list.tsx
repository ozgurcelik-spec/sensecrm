import { useTranslation } from "react-i18next";
import {
  ActionIcon,
  Badge,
  Group,
  Loader,
  Table,
  Text,
  Tooltip,
  UnstyledButton,
} from "@mantine/core";
import { ArrowDown, ArrowUp, Download, Eye, Pencil, Trash2 } from "lucide-react";
import { formatDateTime } from "@/lib/dates";
import { formatBytes } from "@/lib/files";
import { useAuthStore } from "@/store/auth.store";
import type { FileAttachment } from "@/types";
import { FileIcon } from "./file-icon";

/** Sort fields the list endpoint accepts (`-` prefix = descending). */
export type FileSortField = "name" | "sizeBytes" | "uploadedAt";

interface FileListProps {
  files: FileAttachment[];
  /** Upload / rename / delete are offered (record write permission and a writable tenant). */
  canWrite: boolean;
  sort: string;
  onSort: (sort: string) => void;
  /** Id of the file being downloaded right now (its button spins). */
  downloadingId?: string;
  compact?: boolean;
  onDownload: (file: FileAttachment) => void;
  onPreview: (file: FileAttachment) => void;
  onRename: (file: FileAttachment) => void;
  onDelete: (file: FileAttachment) => void;
}

function SortHeader({
  field,
  label,
  sort,
  onSort,
}: {
  field: FileSortField;
  label: string;
  sort: string;
  onSort: (sort: string) => void;
}) {
  const active = sort === field || sort === `-${field}`;
  const descending = sort === `-${field}`;
  return (
    <Table.Th aria-sort={active ? (descending ? "descending" : "ascending") : "none"}>
      <UnstyledButton
        onClick={() => onSort(active && !descending ? `-${field}` : field)}
        fz="sm"
        fw={700}
      >
        <Group gap={4} wrap="nowrap">
          {label}
          {active && (descending ? <ArrowDown size={12} /> : <ArrowUp size={12} />)}
        </Group>
      </UnstyledButton>
    </Table.Th>
  );
}

/** The attachments table: icon, name, size, uploader, date, state and the row actions the user may use. */
export function FileList({
  files,
  canWrite,
  sort,
  onSort,
  downloadingId,
  compact = false,
  onDownload,
  onPreview,
  onRename,
  onDelete,
}: FileListProps) {
  const { t } = useTranslation(["files"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const action = (key: string, file: FileAttachment) =>
    t("files:actions.of", { action: t(`files:actions.${key}`), name: file.name });

  return (
    <Table.ScrollContainer minWidth={compact ? 420 : 640}>
      <Table verticalSpacing="xs" highlightOnHover data-testid="file-list">
        <Table.Thead>
          <Table.Tr>
            <SortHeader field="name" label={t("files:columns.name")} sort={sort} onSort={onSort} />
            <SortHeader field="sizeBytes" label={t("files:columns.size")} sort={sort} onSort={onSort} />
            {!compact && <Table.Th>{t("files:columns.uploadedBy")}</Table.Th>}
            <SortHeader field="uploadedAt" label={t("files:columns.uploadedAt")} sort={sort} onSort={onSort} />
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {files.map((file) => {
            const downloadable = file.state === "ready";
            return (
              <Table.Tr key={file.id} data-testid="file-row" data-state={file.state}>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    <FileIcon extension={file.extension} />
                    {downloadable ? (
                      <UnstyledButton
                        onClick={() => onDownload(file)}
                        title={t("files:actions.download")}
                        fz="sm"
                        style={{ wordBreak: "break-word", textAlign: "left" }}
                        c="blue"
                      >
                        {file.name}
                      </UnstyledButton>
                    ) : (
                      <Text size="sm" style={{ wordBreak: "break-word" }}>
                        {file.name}
                      </Text>
                    )}
                    {file.state === "quarantined" && (
                      <Tooltip label={t("files:state.quarantinedHint")} multiline w={240}>
                        <Badge color="red" variant="light" data-testid="state-badge">
                          {t("files:state.quarantined")}
                        </Badge>
                      </Tooltip>
                    )}
                    {file.state === "missing" && (
                      <Tooltip label={t("files:state.missingHint")} multiline w={240}>
                        <Badge color="gray" variant="light" data-testid="state-badge">
                          {t("files:state.missing")}
                        </Badge>
                      </Tooltip>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td style={{ whiteSpace: "nowrap" }}>{formatBytes(file.sizeBytes)}</Table.Td>
                {!compact && <Table.Td>{file.uploadedByName ?? "-"}</Table.Td>}
                <Table.Td style={{ whiteSpace: "nowrap" }}>
                  {formatDateTime(file.uploadedAt, timeZone)}
                </Table.Td>
                <Table.Td>
                  <Group gap={4} wrap="nowrap" justify="flex-end">
                    {file.canPreview && (
                      <Tooltip label={t("files:actions.preview")}>
                        <ActionIcon
                          variant="subtle"
                          aria-label={action("preview", file)}
                          onClick={() => onPreview(file)}
                        >
                          <Eye size={16} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                    {downloadable && (
                      <Tooltip label={t("files:actions.download")}>
                        <ActionIcon
                          variant="subtle"
                          aria-label={action("download", file)}
                          disabled={downloadingId === file.id}
                          onClick={() => onDownload(file)}
                        >
                          {downloadingId === file.id ? <Loader size={14} /> : <Download size={16} />}
                        </ActionIcon>
                      </Tooltip>
                    )}
                    {canWrite && (
                      <>
                        <Tooltip label={t("files:actions.rename")}>
                          <ActionIcon
                            variant="subtle"
                            aria-label={action("rename", file)}
                            onClick={() => onRename(file)}
                          >
                            <Pencil size={16} />
                          </ActionIcon>
                        </Tooltip>
                        <Tooltip label={t("files:actions.delete")}>
                          <ActionIcon
                            variant="subtle"
                            color="red"
                            aria-label={action("delete", file)}
                            onClick={() => onDelete(file)}
                          >
                            <Trash2 size={16} />
                          </ActionIcon>
                        </Tooltip>
                      </>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
