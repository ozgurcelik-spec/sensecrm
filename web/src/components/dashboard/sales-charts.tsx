import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, SimpleGrid } from "@mantine/core";
import { BarChart, DonutChart, FunnelChart } from "@mantine/charts";
import { useLeadsBySource, useSalesFunnel, useWonLost } from "@/hooks/use-reports";
import { formatNumber } from "@/lib/format";
import { SOURCE_COLORS, formatPeriod, toFunnelCells } from "@/lib/report-format";
import { lastSixMonthsRange } from "@/lib/report-range";
import { useAuthStore } from "@/store/auth.store";
import { DashboardWidget } from "./dashboard-widget";

const REPORTS_LINK = (label: string) => (
  <Anchor component={Link} to="/app/reports" size="sm">
    {label}
  </Anchor>
);

function FunnelWidget() {
  const { t } = useTranslation(["home"]);
  const { data, isLoading, error, refetch } = useSalesFunnel({});
  const cells = toFunnelCells(data?.stages ?? []);
  return (
    <DashboardWidget
      testId="widget-funnel"
      title={t("home:charts.funnel")}
      action={REPORTS_LINK(t("home:charts.viewReports"))}
      isLoading={isLoading}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={cells.every((c) => c.value === 0)}
      skeletonHeight={260}
    >
      <FunnelChart
        data={cells}
        size={240}
        mx="auto"
        withLabels
        labelsPosition="inside"
        withLegend
        valueFormatter={formatNumber}
      />
    </DashboardWidget>
  );
}

function WonLostWidget() {
  const { t } = useTranslation(["home", "reports"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const range = lastSixMonthsRange(timeZone);
  const { data, isLoading, error, refetch } = useWonLost({ ...range, groupBy: "month" });
  const rows = (data ?? []).map((r) => ({ ...r, label: formatPeriod(r.period) }));
  return (
    <DashboardWidget
      testId="widget-won-lost"
      title={t("home:charts.wonLost")}
      action={REPORTS_LINK(t("home:charts.viewReports"))}
      isLoading={isLoading}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={rows.every((r) => r.wonCount === 0 && r.lostCount === 0)}
      skeletonHeight={240}
    >
      <BarChart
        h={240}
        data={rows}
        dataKey="label"
        series={[
          { name: "wonCount", label: t("reports:series.won"), color: "green.6" },
          { name: "lostCount", label: t("reports:series.lost"), color: "red.6" },
        ]}
        withLegend
        valueFormatter={formatNumber}
        yAxisProps={{ allowDecimals: false }}
      />
    </DashboardWidget>
  );
}

function LeadSourceWidget() {
  const { t } = useTranslation(["home", "crm"]);
  const { data, isLoading, error, refetch } = useLeadsBySource({});
  const cells = (data ?? []).map((r, i) => ({
    name: t(`crm:leads.sources.${r.source}`, { defaultValue: r.source }),
    value: r.count,
    color: SOURCE_COLORS[i % SOURCE_COLORS.length] as string,
  }));
  return (
    <DashboardWidget
      testId="widget-lead-sources"
      title={t("home:charts.leadSources")}
      action={REPORTS_LINK(t("home:charts.viewReports"))}
      isLoading={isLoading}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={cells.every((c) => c.value === 0)}
      skeletonHeight={240}
    >
      <DonutChart data={cells} size={200} mx="auto" withLegend withTooltip />
    </DashboardWidget>
  );
}

/**
 * Report-backed dashboard widgets (sales funnel, won/lost per month, lead sources). Loaded lazily
 * and only mounted when the user has `crm.reports.read`, so recharts stays out of the main bundle.
 */
export default function SalesCharts() {
  return (
    <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
      <FunnelWidget />
      <WonLostWidget />
      <LeadSourceWidget />
    </SimpleGrid>
  );
}
