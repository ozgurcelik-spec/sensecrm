import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Card, SimpleGrid, Stack, Text } from "@mantine/core";
import { BarChart, DonutChart } from "@mantine/charts";
import { CampaignStatusBadge, CampaignTypeBadge } from "@/components/marketing/campaign-badges";
import { useMarketingSummary } from "@/hooks/use-campaigns";
import { CAMPAIGN_STATUS_COLOR, CAMPAIGN_TYPE_COLOR } from "@/lib/campaign";
import { formatMoney, formatNumber } from "@/lib/format";
import type { DateRange } from "@/lib/report-range";
import { ReportPanel, ReportTable, type CsvExport } from "./report-panel";

const CHART_HEIGHT = 280;

/** Percent from the server's 0-100 number. */
function percent(value: number): string {
  return `${formatNumber(Math.round(value * 100) / 100)}%`;
}

function TotalCard({ label, value, testId }: { label: string; value: string; testId: string }) {
  return (
    <Card withBorder padding="md" data-testid={testId}>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text fz={24} fw={700} data-testid="total-value">
        {value}
      </Text>
    </Card>
  );
}

/**
 * "Marketing" tab of the reports page (`GET /reports/marketing/summary?from&to`): status and type
 * distribution, budget vs actual cost per type, total cards and the top campaigns. Amounts are summed
 * regardless of currency (a known limitation shared with the other reports).
 */
export function MarketingReport({ range }: { range: DateRange | null }) {
  const { t } = useTranslation(["campaigns", "reports"]);
  const { data, isLoading, error, refetch } = useMarketingSummary(
    { from: range?.from, to: range?.to },
    !!range
  );
  const byType = data?.byType ?? [];
  const totals = data?.totals;

  const csv: CsvExport = {
    filename: range
      ? `${t("campaigns:report.file")}_${range.from}_${range.to}`
      : t("campaigns:report.file"),
    headers: [
      t("campaigns:report.type"),
      t("campaigns:report.campaignCount"),
      t("campaigns:report.budget"),
      t("campaigns:report.actualCost"),
      t("campaigns:report.memberCount"),
      t("campaigns:report.convertedCount"),
    ],
    rows: byType.map((r) => [
      t(`campaigns:types.${r.type}`),
      r.count,
      r.budget,
      r.actualCost,
      r.memberCount,
      r.convertedCount,
    ]),
  };

  return (
    <ReportPanel
      isLoading={isLoading && !!range}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={!data || data.campaignCount === 0}
      csv={csv}
    >
      {data && totals && (
        <Stack gap="md">
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
            <TotalCard
              testId="total-campaigns"
              label={t("campaigns:report.campaignCount")}
              value={formatNumber(data.campaignCount)}
            />
            <TotalCard
              testId="total-response-rate"
              label={t("campaigns:report.responseRate")}
              value={percent(totals.responseRate)}
            />
            <TotalCard
              testId="total-conversion-rate"
              label={t("campaigns:report.conversionRate")}
              value={percent(totals.conversionRate)}
            />
            <TotalCard
              testId="total-cost-per-lead"
              label={t("campaigns:report.costPerLead")}
              value={totals.costPerLead === undefined ? "—" : formatMoney(totals.costPerLead)}
            />
            <TotalCard
              testId="total-budget"
              label={t("campaigns:report.budget")}
              value={formatMoney(totals.budget)}
            />
            <TotalCard
              testId="total-actual-cost"
              label={t("campaigns:report.actualCost")}
              value={formatMoney(totals.actualCost)}
            />
            <TotalCard
              testId="total-budget-gap"
              label={`${t("campaigns:report.budget")} - ${t("campaigns:report.actualCost")}`}
              // The difference is derived here; the server returns only the two totals.
              value={formatMoney(totals.budget - totals.actualCost)}
            />
            <TotalCard
              testId="total-expected-revenue"
              label={t("campaigns:report.expectedRevenue")}
              value={formatMoney(totals.expectedRevenue)}
            />
          </SimpleGrid>

          <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
            <Card withBorder padding="md">
              <Text fw={600} mb="sm">
                {t("campaigns:report.byStatus")}
              </Text>
              <DonutChart
                size={200}
                mx="auto"
                withLegend
                withTooltip
                data={data.byStatus.map((r) => ({
                  name: t(`campaigns:statuses.${r.status}`),
                  value: r.count,
                  color: `${CAMPAIGN_STATUS_COLOR[r.status]}.6`,
                }))}
              />
            </Card>
            <Card withBorder padding="md">
              <Text fw={600} mb="sm">
                {t("campaigns:report.byType")}
              </Text>
              <DonutChart
                size={200}
                mx="auto"
                withLegend
                withTooltip
                data={byType.map((r) => ({
                  name: t(`campaigns:types.${r.type}`),
                  value: r.count,
                  color: `${CAMPAIGN_TYPE_COLOR[r.type]}.6`,
                }))}
              />
            </Card>
          </SimpleGrid>

          <Card withBorder padding="md">
            <Text fw={600} mb="sm">
              {t("campaigns:report.budgetVsCost")}
            </Text>
            <BarChart
              h={CHART_HEIGHT}
              data={byType.map((r) => ({ ...r, label: t(`campaigns:types.${r.type}`) }))}
              dataKey="label"
              series={[
                { name: "budget", label: t("campaigns:report.budget"), color: "blue.6" },
                { name: "actualCost", label: t("campaigns:report.actualCost"), color: "orange.6" },
              ]}
              withLegend
              valueFormatter={(value) => formatMoney(value)}
            />
            <Text size="xs" c="dimmed" mt="xs">
              {t("campaigns:report.amountsNote")}
            </Text>
          </Card>

          <Text fw={600}>{t("campaigns:report.topCampaigns")}</Text>
          <ReportTable
            rows={data.topCampaigns}
            rowKey={(c) => c.id}
            columns={[
              {
                key: "name",
                header: t("campaigns:report.campaign"),
                render: (c) => (
                  <Anchor component={Link} to={`/app/campaigns/${c.id}`} size="sm">
                    {c.name}
                  </Anchor>
                ),
              },
              {
                key: "type",
                header: t("campaigns:report.type"),
                render: (c) => <CampaignTypeBadge type={c.type} />,
              },
              {
                key: "status",
                header: t("campaigns:report.status"),
                render: (c) => <CampaignStatusBadge status={c.status} />,
              },
              {
                key: "budget",
                header: t("campaigns:report.budget"),
                numeric: true,
                render: (c) => (c.budget === undefined ? "-" : formatMoney(c.budget)),
              },
              {
                key: "actualCost",
                header: t("campaigns:report.actualCost"),
                numeric: true,
                render: (c) => (c.actualCost === undefined ? "-" : formatMoney(c.actualCost)),
              },
              {
                key: "members",
                header: t("campaigns:report.memberCount"),
                numeric: true,
                render: (c) => formatNumber(c.memberCount),
              },
              {
                key: "responseRate",
                header: t("campaigns:report.responseRate"),
                numeric: true,
                render: (c) => percent(c.responseRate),
              },
              {
                key: "converted",
                header: t("campaigns:report.convertedCount"),
                numeric: true,
                render: (c) => formatNumber(c.convertedCount),
              },
            ]}
          />
        </Stack>
      )}
    </ReportPanel>
  );
}
