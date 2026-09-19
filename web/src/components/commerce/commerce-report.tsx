/** "Ticaret" tab of the reports page: quote / order status tables, conversion rate (`crm.reports.read`). */
import { useTranslation } from "react-i18next";
import { Alert, Badge, Card, SimpleGrid, Stack, Text } from "@mantine/core";
import { ReportPanel, ReportTable, type CsvExport } from "@/components/reports/report-panel";
import { useCommerceSummary } from "@/hooks/use-commerce-report";
import { formatMoney, formatNumber } from "@/lib/format";
import { formatPercent } from "@/lib/report-format";
import type { DateRange } from "@/lib/report-range";
import type { CommerceStatusRow } from "@/types";

const QUOTE_COLOR: Record<string, string> = {
  draft: "gray",
  sent: "blue",
  accepted: "green",
  rejected: "red",
  expired: "orange",
};
const ORDER_COLOR: Record<string, string> = {
  draft: "gray",
  confirmed: "blue",
  fulfilled: "green",
  cancelled: "red",
};

export function CommerceReport({ range }: { range: DateRange | null }) {
  const { t } = useTranslation(["commerce", "reports"]);
  const { data, isLoading, error, refetch } = useCommerceSummary(
    { from: range?.from, to: range?.to },
    !!range
  );

  const quoteRows: CommerceStatusRow<string>[] = data?.quotes.byStatus ?? [];
  const orderRows: CommerceStatusRow<string>[] = data?.orders.byStatus ?? [];
  const isEmpty =
    !!data &&
    quoteRows.every((row) => row.count === 0) &&
    orderRows.every((row) => row.count === 0);
  const mixedCurrencies = (data?.currencies.length ?? 0) > 1;
  // Amounts are summed across currencies (a known limitation); the symbol is only exact for one currency.
  const currency = data?.currencies.length === 1 ? data.currencies[0] : "TRY";
  // conversionRate is absent (not 0) when there is no non-draft quote.
  const conversion =
    data?.conversionRate === undefined || data?.conversionRate === null
      ? "-"
      : formatPercent(data.conversionRate);

  const csv: CsvExport = {
    filename: range ? `${t("commerce:reports.file")}_${range.from}_${range.to}` : t("commerce:reports.file"),
    headers: [
      t("commerce:reports.section"),
      t("commerce:fields.status"),
      t("commerce:reports.count"),
      t("commerce:reports.amount"),
    ],
    rows: [
      ...quoteRows.map((r) => [
        t("commerce:quotes.title"),
        t(`commerce:quoteStatuses.${r.status}`),
        r.count,
        r.amount,
      ]),
      ...orderRows.map((r) => [
        t("commerce:orders.title"),
        t(`commerce:orderStatuses.${r.status}`),
        r.count,
        r.amount,
      ]),
    ],
  };

  const columns = (kind: "quote" | "order") => [
    {
      key: "status",
      header: t("commerce:fields.status"),
      render: (row: CommerceStatusRow<string>) => (
        <Badge variant="light" color={(kind === "quote" ? QUOTE_COLOR : ORDER_COLOR)[row.status] ?? "gray"}>
          {t(`commerce:${kind}Statuses.${row.status}`, { defaultValue: row.status })}
        </Badge>
      ),
    },
    {
      key: "count",
      header: t("commerce:reports.count"),
      numeric: true,
      render: (row: CommerceStatusRow<string>) => formatNumber(row.count),
    },
    {
      key: "amount",
      header: t("commerce:reports.amount"),
      numeric: true,
      render: (row: CommerceStatusRow<string>) => formatMoney(row.amount, currency),
    },
  ];

  return (
    <ReportPanel
      isLoading={isLoading && !!range}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={isEmpty}
      csv={csv}
    >
      <Stack gap="md">
        {mixedCurrencies && (
          <Alert color="yellow" variant="light" role="status">
            {t("commerce:reports.mixedCurrency", { currencies: data?.currencies.join(", ") })}
          </Alert>
        )}
        <SimpleGrid cols={{ base: 1, sm: 3 }} spacing="md">
          <Card withBorder padding="md">
            <Text size="xs" c="dimmed">
              {t("commerce:reports.conversionRate")}
            </Text>
            <Text fz={28} fw={700} data-testid="conversion-rate">
              {conversion}
            </Text>
            <Text size="xs" c="dimmed">
              {t("commerce:reports.conversionHint")}
            </Text>
          </Card>
          <Card withBorder padding="md">
            <Text size="xs" c="dimmed">
              {t("commerce:reports.quotesTotal")}
            </Text>
            <Text fz={28} fw={700}>
              {formatMoney(data?.quotes.totalAmount ?? 0, currency)}
            </Text>
            <Text size="xs" c="dimmed">
              {t("commerce:reports.countValue", { count: data?.quotes.totalCount ?? 0 })}
            </Text>
          </Card>
          <Card withBorder padding="md">
            <Text size="xs" c="dimmed">
              {t("commerce:reports.ordersTotal")}
            </Text>
            <Text fz={28} fw={700}>
              {formatMoney(data?.orders.totalAmount ?? 0, currency)}
            </Text>
            <Text size="xs" c="dimmed">
              {t("commerce:reports.countValue", { count: data?.orders.totalCount ?? 0 })}
            </Text>
          </Card>
        </SimpleGrid>

        <Text fw={600}>{t("commerce:reports.quotesByStatus")}</Text>
        <ReportTable rows={quoteRows} rowKey={(row) => row.status} columns={columns("quote")} />

        <Text fw={600}>{t("commerce:reports.ordersByStatus")}</Text>
        <ReportTable rows={orderRows} rowKey={(row) => row.status} columns={columns("order")} />
        <Text size="xs" c="dimmed">
          {t("commerce:reports.cancelledNote")}
        </Text>
      </Stack>
    </ReportPanel>
  );
}
