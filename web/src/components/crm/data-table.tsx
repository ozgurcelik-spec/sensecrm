import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  Card,
  Group,
  Pagination,
  Select,
  Skeleton,
  Table,
  Text,
  UnstyledButton,
} from "@mantine/core";
import { ArrowDown, ArrowUp, ChevronsUpDown } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PAGE_SIZE_OPTIONS, type SortState } from "@/hooks/use-list-params";

export interface Column<T> {
  key: string;
  header: string;
  /** Server-side sort field (`sort=field` / `sort=-field`); omit for non-sortable columns. */
  sortField?: string;
  render: (row: T) => ReactNode;
  width?: number | string;
}

interface DataTableProps<T> {
  columns: readonly Column<T>[];
  rows: readonly T[] | undefined;
  rowKey: (row: T) => string;
  isLoading: boolean;
  isFetching?: boolean;
  error?: unknown;
  onRetry?: () => void;
  sort: SortState | null;
  onSort: (field: string) => void;
  page: number;
  pageSize: number;
  totalCount: number | undefined;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  emptyMessage?: ReactNode;
  minWidth?: number;
}

function SortIcon({ active, descending }: { active: boolean; descending: boolean }) {
  if (!active) return <ChevronsUpDown size={14} opacity={0.4} aria-hidden="true" />;
  return descending ? (
    <ArrowDown size={14} aria-hidden="true" />
  ) : (
    <ArrowUp size={14} aria-hidden="true" />
  );
}

/**
 * Table with server-side paging and sorting: loading skeleton, inline error with retry, empty
 * state and a footer (total count, page size, pagination). The caller owns the query state.
 */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  isLoading,
  isFetching = false,
  error,
  onRetry,
  sort,
  onSort,
  page,
  pageSize,
  totalCount,
  onPageChange,
  onPageSizeChange,
  emptyMessage,
  minWidth = 760,
}: DataTableProps<T>) {
  const { t } = useTranslation(["common"]);
  const totalPages = totalCount ? Math.max(1, Math.ceil(totalCount / pageSize)) : 1;

  return (
    <>
      {!!error && <LoadError error={error} onRetry={onRetry ?? (() => undefined)} />}
      <Card withBorder padding={0} mt={error ? "md" : 0}>
        <Table.ScrollContainer minWidth={minWidth}>
          <Table
            verticalSpacing="sm"
            highlightOnHover
            aria-busy={isFetching}
            style={{ opacity: isFetching && !isLoading ? 0.6 : 1 }}
          >
            <Table.Thead>
              <Table.Tr>
                {columns.map((column) => {
                  const active = !!column.sortField && sort?.field === column.sortField;
                  return (
                    <Table.Th
                      key={column.key}
                      w={column.width}
                      aria-sort={
                        active ? (sort?.descending ? "descending" : "ascending") : undefined
                      }
                    >
                      {column.sortField ? (
                        <UnstyledButton
                          onClick={() => onSort(column.sortField as string)}
                          fw={600}
                          fz="sm"
                          aria-label={t("common:sortBy", { column: column.header })}
                        >
                          <Group gap={4} wrap="nowrap">
                            {column.header}
                            <SortIcon active={active} descending={!!sort?.descending} />
                          </Group>
                        </UnstyledButton>
                      ) : (
                        column.header
                      )}
                    </Table.Th>
                  );
                })}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {isLoading &&
                Array.from({ length: 5 }, (_, i) => (
                  <Table.Tr key={i} data-testid="row-skeleton">
                    <Table.Td colSpan={columns.length}>
                      <Skeleton h={24} />
                    </Table.Td>
                  </Table.Tr>
                ))}
              {rows?.map((row) => (
                <Table.Tr key={rowKey(row)}>
                  {columns.map((column) => (
                    <Table.Td key={column.key}>{column.render(row)}</Table.Td>
                  ))}
                </Table.Tr>
              ))}
              {rows && rows.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={columns.length}>
                    <Text size="sm" c="dimmed" ta="center" py="md">
                      {emptyMessage ?? t("common:noData")}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>
      {!!totalCount && totalCount > 0 && (
        <Group justify="space-between" mt="md" wrap="wrap">
          <Group gap="sm">
            <Text size="sm" c="dimmed">
              {t("common:total", { count: totalCount })}
            </Text>
            <Select
              size="xs"
              w={84}
              aria-label={t("common:pageSize")}
              data={[...PAGE_SIZE_OPTIONS]}
              value={String(pageSize)}
              onChange={(value) => value && onPageSizeChange(Number(value))}
              allowDeselect={false}
            />
          </Group>
          <Pagination
            total={totalPages}
            value={page}
            onChange={onPageChange}
            size="sm"
            getControlProps={(control) => {
              if (control === "next") return { "aria-label": t("common:nextPage") };
              if (control === "previous") return { "aria-label": t("common:previousPage") };
              return {};
            }}
          />
        </Group>
      )}
    </>
  );
}
