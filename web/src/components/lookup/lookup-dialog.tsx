import { useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { useDebouncedValue } from "@mantine/hooks";
import {
  Alert,
  Button,
  Group,
  Loader,
  Modal,
  Radio,
  Stack,
  Table,
  Text,
  TextInput,
  UnstyledButton,
} from "@mantine/core";
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, ChevronsUpDown, Plus, Search } from "lucide-react";
import { usePermission } from "@/hooks/use-permission";
import { parseSort } from "@/hooks/use-list-params";
import { LOOKUP_DEBOUNCE_MS, type LookupDialogProps, type LookupFilters } from "./lookup-types";

/** Rows per page of the window (contract: 10). */
const PAGE_SIZE = 10;
/** Filters as strings for a quick-create dialog (`accountId` ...): undefined and empty values are dropped. */
function presetFrom(filters: LookupFilters | undefined): Record<string, string> {
  const preset: Record<string, string> = {};
  for (const [key, value] of Object.entries(filters ?? {})) {
    if (value !== undefined && value !== "") preset[key] = String(value);
  }
  return preset;
}

/**
 * Search window of the M9C plan ("... Seç"): search box (debounced), paged table (10 rows), sortable
 * headers, radio / row selection that selects and closes, keyboard (up / down, Enter, Esc) and the
 * permission-gated "+ Yeni ..." quick-create that selects the new record automatically. The
 * content mounts only while open, so it starts fresh (and asks the server nothing) every time.
 */
export function LookupDialog<T>(props: LookupDialogProps<T>) {
  return (
    <Modal
      opened={props.opened}
      onClose={props.onClose}
      title={props.title}
      size="xl"
      centered
      trapFocus
    >
      <LookupBody {...props} />
    </Modal>
  );
}

function SortMark({ active, descending }: { active: boolean; descending: boolean }) {
  if (!active) return <ChevronsUpDown size={14} opacity={0.4} aria-hidden="true" />;
  return descending ? <ArrowDown size={14} aria-hidden="true" /> : <ArrowUp size={14} aria-hidden="true" />;
}

function LookupBody<T>({
  onClose,
  source,
  filters,
  selectedId,
  onSelect,
  create,
  disabledRow,
  pageSize = PAGE_SIZE,
}: LookupDialogProps<T>) {
  const { t } = useTranslation(["inventory"]);
  const queryClient = useQueryClient();
  const canCreate = usePermission(create?.permission ?? "");
  const [search, setSearch] = useState("");
  const [debounced] = useDebouncedValue(search.trim(), LOOKUP_DEBOUNCE_MS);
  const [page, setPage] = useState(1);
  const [sort, setSort] = useState<string | undefined>(source.defaultSort);
  const [active, setActive] = useState(0);
  const [creating, setCreating] = useState(false);

  const result = source.useSearch({
    q: debounced || undefined,
    page,
    pageSize,
    sort,
    filters,
  });
  const rows = result.items;
  const pageCount = Math.max(1, Math.ceil(result.totalCount / pageSize));

  const disabledReason = (row: T) => disabledRow?.(row);

  function choose(row: T) {
    if (disabledReason(row)) return;
    onSelect(row);
    onClose();
  }

  function toggleSort(field: string) {
    const current = parseSort(sort);
    if (!current || current.field !== field) setSort(field);
    else if (!current.descending) setSort(`-${field}`);
    else setSort(source.defaultSort);
    setPage(1);
    setActive(0);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (creating || rows.length === 0) return;
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setActive((index) => Math.min(index + 1, rows.length - 1));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setActive((index) => Math.max(index - 1, 0));
    } else if (event.key === "Enter") {
      // A focused button (paging, "+ Yeni") keeps its own Enter; the search box and the rows select.
      const target = event.target as HTMLElement;
      if (target.tagName === "BUTTON") return;
      const row = rows[Math.min(active, rows.length - 1)];
      if (row !== undefined) {
        event.preventDefault();
        choose(row);
      }
    }
  }

  function handleCreated(row: T) {
    void queryClient.invalidateQueries({ queryKey: [source.queryKey] });
    setCreating(false);
    onSelect(row);
    onClose();
  }

  const currentSort = parseSort(sort);
  const CreateDialog = create?.Dialog;

  return (
    <Stack gap="sm" onKeyDown={handleKeyDown}>
      <Group gap="sm" align="flex-end" wrap="nowrap">
        <TextInput
          style={{ flex: 1 }}
          aria-label={t("inventory:lookup.search")}
          placeholder={t("inventory:lookup.search")}
          leftSection={<Search size={16} />}
          value={search}
          onChange={(event) => {
            setSearch(event.currentTarget.value);
            setPage(1);
            setActive(0);
          }}
          data-autofocus
          autoFocus
        />
        {create && canCreate && (
          <Button variant="light" leftSection={<Plus size={16} />} onClick={() => setCreating(true)}>
            {create.label}
          </Button>
        )}
      </Group>

      {result.isError ? (
        <Alert color="red" variant="light" role="alert">
          {t("inventory:lookup.error")}
        </Alert>
      ) : (
        <Table.ScrollContainer minWidth={520}>
          <Table verticalSpacing="xs" highlightOnHover aria-label={t("inventory:lookup.results")}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={36} />
                {source.columns.map((column) => (
                  <Table.Th key={column.key}>
                    {column.sortField ? (
                      <UnstyledButton
                        onClick={() => toggleSort(column.sortField as string)}
                        aria-label={t("inventory:lookup.sortBy", { column: column.header })}
                        style={{ font: "inherit", fontWeight: 600 }}
                      >
                        <Group gap={4} wrap="nowrap">
                          {column.header}
                          <SortMark
                            active={currentSort?.field === column.sortField}
                            descending={currentSort?.descending ?? false}
                          />
                        </Group>
                      </UnstyledButton>
                    ) : (
                      column.header
                    )}
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row, index) => {
                const id = source.getId(row);
                const reason = disabledReason(row);
                return (
                  <Table.Tr
                    key={id}
                    data-testid="lookup-row"
                    aria-selected={index === active}
                    aria-disabled={reason ? true : undefined}
                    bg={index === active ? "var(--mantine-color-default-hover)" : undefined}
                    style={{ cursor: reason ? "not-allowed" : "pointer", opacity: reason ? 0.6 : 1 }}
                    onClick={() => choose(row)}
                    onMouseEnter={() => setActive(index)}
                  >
                    <Table.Td>
                      <Radio
                        checked={id === selectedId}
                        readOnly
                        disabled={!!reason}
                        tabIndex={-1}
                        aria-label={source.getLabel(row)}
                      />
                    </Table.Td>
                    {source.columns.map((column, columnIndex) => (
                      <Table.Td key={column.key}>
                        {column.render(row)}
                        {columnIndex === 0 && reason && (
                          <Text size="xs" c="dimmed">
                            {reason}
                          </Text>
                        )}
                      </Table.Td>
                    ))}
                  </Table.Tr>
                );
              })}
              {rows.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={source.columns.length + 1}>
                    <Group justify="center" py="md" gap="xs">
                      {result.isFetching ? (
                        <>
                          <Loader size="xs" />
                          <Text size="sm" c="dimmed">
                            {t("inventory:lookup.loading")}
                          </Text>
                        </>
                      ) : (
                        <Text size="sm" c="dimmed">
                          {t("inventory:lookup.empty")}
                        </Text>
                      )}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      <Group justify="space-between">
        <Text size="xs" c="dimmed">
          {t("inventory:lookup.total", { count: result.totalCount })}
        </Text>
        <Group gap="xs">
          <Button
            variant="default"
            size="compact-sm"
            leftSection={<ChevronLeft size={14} />}
            disabled={page <= 1}
            onClick={() => {
              setPage((current) => Math.max(1, current - 1));
              setActive(0);
            }}
          >
            {t("inventory:lookup.previous")}
          </Button>
          <Text size="sm">{t("inventory:lookup.page", { page, pages: pageCount })}</Text>
          <Button
            variant="default"
            size="compact-sm"
            rightSection={<ChevronRight size={14} />}
            disabled={page >= pageCount}
            onClick={() => {
              setPage((current) => Math.min(pageCount, current + 1));
              setActive(0);
            }}
          >
            {t("inventory:lookup.next")}
          </Button>
        </Group>
      </Group>

      {CreateDialog && creating && (
        <CreateDialog
          opened
          onClose={() => setCreating(false)}
          initialName={search.trim() || undefined}
          presetFilters={presetFrom(filters)}
          onCreated={handleCreated}
        />
      )}
    </Stack>
  );
}
