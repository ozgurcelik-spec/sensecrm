import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActionIcon, Button, Group, Stack, Tooltip } from "@mantine/core";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { PageHeader } from "@/components/page-header";
import { SearchInput } from "./search-input";

interface ListPageFrameProps {
  title: string;
  description?: string;
  /** Label of the "New" button; the button is not rendered at all unless `onCreate` is given. */
  createLabel: string;
  onCreate?: () => void;
  extraActions?: ReactNode;
  q: string;
  onSearch: (q: string) => void;
  searchPlaceholder: string;
  /** The board view has no server-side search. */
  hideSearch?: boolean;
  /** Filter controls shown next to the search box. */
  filters?: ReactNode;
  hasActiveFilters: boolean;
  onClearFilters: () => void;
  children: ReactNode;
}

/** Common list page chrome: header with the permission-gated "New" button, search, filters, clear. */
export function ListPageFrame({
  title,
  description,
  createLabel,
  onCreate,
  extraActions,
  q,
  onSearch,
  searchPlaceholder,
  hideSearch = false,
  filters,
  hasActiveFilters,
  onClearFilters,
  children,
}: ListPageFrameProps) {
  const { t } = useTranslation(["common"]);
  return (
    <>
      <PageHeader
        title={title}
        description={description}
        actions={
          <>
            {extraActions}
            {onCreate && (
              <Button leftSection={<Plus size={16} />} onClick={onCreate}>
                {createLabel}
              </Button>
            )}
          </>
        }
      />
      <Stack gap="md">
        <Group gap="sm" align="flex-end" wrap="wrap">
          {!hideSearch && (
            <SearchInput value={q} onSearch={onSearch} placeholder={searchPlaceholder} />
          )}
          {filters}
          {hasActiveFilters && (
            <Button variant="subtle" size="sm" onClick={onClearFilters}>
              {t("common:clearFilters")}
            </Button>
          )}
        </Group>
        {children}
      </Stack>
    </>
  );
}

interface RowActionsProps {
  onEdit?: () => void;
  onDelete?: () => void;
  editLabel: string;
  deleteLabel: string;
}

/** Edit / delete icons of a table row; each one only exists when its handler is provided (permission). */
export function RowActions({ onEdit, onDelete, editLabel, deleteLabel }: RowActionsProps) {
  if (!onEdit && !onDelete) return null;
  return (
    <Group gap={4} wrap="nowrap" justify="flex-end">
      {onEdit && (
        <Tooltip label={editLabel}>
          <ActionIcon variant="subtle" aria-label={editLabel} onClick={onEdit}>
            <Pencil size={16} />
          </ActionIcon>
        </Tooltip>
      )}
      {onDelete && (
        <Tooltip label={deleteLabel}>
          <ActionIcon variant="subtle" color="red" aria-label={deleteLabel} onClick={onDelete}>
            <Trash2 size={16} />
          </ActionIcon>
        </Tooltip>
      )}
    </Group>
  );
}
