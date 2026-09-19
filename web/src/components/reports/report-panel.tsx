import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button, Card, Group, Skeleton, Stack, Table, Text } from "@mantine/core";
import { Download } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { downloadCsv, toCsv, type CsvCell } from "@/lib/csv";

export interface CsvExport {
  /** File name without extension. */
  filename: string;
  headers: string[];
  rows: CsvCell[][];
}

interface ReportPanelProps {
  isLoading: boolean;
  error?: unknown;
  onRetry: () => void;
  isEmpty: boolean;
  /** Extra controls left of the CSV button (pipeline selector, grouping). */
  controls?: ReactNode;
  csv?: CsvExport;
  children: ReactNode;
}

/** Shared frame of a report tab: controls + CSV button, then loading / error / empty / content. */
export function ReportPanel({
  isLoading,
  error,
  onRetry,
  isEmpty,
  controls,
  csv,
  children,
}: ReportPanelProps) {
  const { t } = useTranslation(["reports"]);
  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-end" wrap="wrap">
        <Group gap="sm" align="flex-end">
          {controls}
        </Group>
        <Button
          variant="default"
          leftSection={<Download size={16} />}
          disabled={!csv || isEmpty || isLoading}
          onClick={() => csv && downloadCsv(`${csv.filename}.csv`, toCsv(csv.headers, csv.rows))}
        >
          {t("reports:downloadCsv")}
        </Button>
      </Group>
      {error ? (
        <LoadError error={error} onRetry={onRetry} />
      ) : isLoading ? (
        <Stack gap="sm">
          <Skeleton h={260} data-testid="report-skeleton" />
          <Skeleton h={120} />
        </Stack>
      ) : isEmpty ? (
        <Card withBorder padding="xl">
          <Text size="sm" c="dimmed" ta="center">
            {t("reports:empty")}
          </Text>
        </Card>
      ) : (
        children
      )}
    </Stack>
  );
}

export interface ReportColumn<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
  /** Numeric columns are right aligned. */
  numeric?: boolean;
}

/** Plain report table (no paging: report rows are aggregates, a handful per view). */
export function ReportTable<T>({
  columns,
  rows,
  rowKey,
}: {
  columns: ReportColumn<T>[];
  rows: T[];
  rowKey: (row: T) => string;
}) {
  return (
    <Card withBorder padding={0}>
      <Table.ScrollContainer minWidth={560}>
        <Table verticalSpacing="sm" highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              {columns.map((c) => (
                <Table.Th key={c.key} ta={c.numeric ? "right" : undefined}>
                  {c.header}
                </Table.Th>
              ))}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((row) => (
              <Table.Tr key={rowKey(row)}>
                {columns.map((c) => (
                  <Table.Td key={c.key} ta={c.numeric ? "right" : undefined}>
                    {c.render(row)}
                  </Table.Td>
                ))}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </Card>
  );
}
