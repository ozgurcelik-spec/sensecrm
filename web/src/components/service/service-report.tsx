import { useTranslation } from "react-i18next";
import { Badge, Card, SimpleGrid, Text } from "@mantine/core";
import { BarChart, DonutChart } from "@mantine/charts";
import { ReportPanel, ReportTable, type CsvExport } from "@/components/reports/report-panel";
import { useServiceByAssignee, useServiceSummary } from "@/hooks/use-reports";
import { formatDuration } from "@/lib/case";
import { formatNumber } from "@/lib/format";
import { formatPercent } from "@/lib/report-format";
import type { DateRange } from "@/lib/report-range";
import { CASE_PRIORITIES, CASE_STATUSES, type CaseStatus } from "@/types";

const CHART_HEIGHT = 300;

const STATUS_CHART_COLOR: Record<CaseStatus, string> = {
  new: "blue.6",
  open: "cyan.6",
  pending: "yellow.6",
  resolved: "green.6",
  closed: "gray.6",
};

function Kpi({ label, value, testId, alert }: { label: string; value: string; testId: string; alert?: boolean }) {
  return (
    <Card withBorder padding="md">
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text fz={24} fw={700} lh={1.2} c={alert ? "red" : undefined} data-testid={testId}>
        {value}
      </Text>
    </Card>
  );
}

const duration = (minutes: number | undefined) =>
  minutes === undefined ? "-" : formatDuration(minutes);

/**
 * "Service" tab of the reports page (`tab=service`): KPI cards, cases by status (donut) and priority
 * (bars), the per-assignee table and an in-browser CSV of that table. Durations read "2 sa 15 dk".
 */
export function ServiceReport({ range }: { range: DateRange | null }) {
  const { t } = useTranslation(["service", "reports"]);
  const query = { from: range?.from, to: range?.to };
  const summary = useServiceSummary(query, !!range);
  const assignees = useServiceByAssignee(query, !!range);

  const report = summary.data;
  const rows = assignees.data ?? [];
  const nameOf = (row: (typeof rows)[number]) =>
    row.assignedUserId ? (row.assignedUserName ?? row.assignedUserId) : t("service:unassigned");

  const csv: CsvExport = {
    filename: range
      ? `${t("service:report.file")}_${range.from}_${range.to}`
      : t("service:report.file"),
    headers: [
      t("service:report.columns.assignee"),
      t("service:report.columns.total"),
      t("service:report.columns.open"),
      t("service:report.columns.resolved"),
      t("service:report.columns.avgFirstResponse"),
      t("service:report.columns.avgResolution"),
      t("service:report.columns.slaBreached"),
    ],
    rows: rows.map((r) => [
      nameOf(r),
      r.totalCount,
      r.openCount,
      r.resolvedCount,
      r.avgFirstResponseMinutes,
      r.avgResolutionMinutes,
      r.slaBreachedCount,
    ]),
  };

  return (
    <ReportPanel
      isLoading={(summary.isLoading || assignees.isLoading) && !!range}
      error={summary.error ?? assignees.error}
      onRetry={() => {
        void summary.refetch();
        void assignees.refetch();
      }}
      isEmpty={!report || report.totalCount === 0}
      csv={csv}
    >
      {report && (
        <>
          <SimpleGrid cols={{ base: 2, sm: 3, lg: 5 }} spacing="sm">
            <Kpi
              label={t("service:report.kpi.total")}
              value={formatNumber(report.totalCount)}
              testId="kpi-total"
            />
            <Kpi
              label={t("service:report.kpi.resolved")}
              value={formatNumber(report.resolvedCount)}
              testId="kpi-resolved"
            />
            <Kpi
              label={t("service:report.kpi.avgFirstResponse")}
              value={duration(report.avgFirstResponseMinutes)}
              testId="kpi-first-response"
            />
            <Kpi
              label={t("service:report.kpi.avgResolution")}
              value={duration(report.avgResolutionMinutes)}
              testId="kpi-resolution"
            />
            <Kpi
              label={t("service:report.kpi.slaBreached")}
              value={`${formatNumber(report.slaBreachedCount)} (${formatPercent(report.slaBreachRate)})`}
              testId="kpi-sla"
              alert={report.slaBreachedCount > 0}
            />
          </SimpleGrid>

          <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
            <Card withBorder padding="md">
              <Text fw={600} mb="sm">
                {t("service:report.byStatus")}
              </Text>
              <DonutChart
                data={CASE_STATUSES.map((status) => ({
                  name: t(`service:statuses.${status}`),
                  value: report.byStatus.find((s) => s.status === status)?.count ?? 0,
                  color: STATUS_CHART_COLOR[status],
                }))}
                size={CHART_HEIGHT - 80}
                mx="auto"
                withLegend
                withTooltip
              />
            </Card>
            <Card withBorder padding="md">
              <Text fw={600} mb="sm">
                {t("service:report.byPriority")}
              </Text>
              <BarChart
                h={CHART_HEIGHT - 60}
                data={CASE_PRIORITIES.map((priority) => ({
                  label: t(`service:priorities.${priority}`),
                  count: report.byPriority.find((p) => p.priority === priority)?.count ?? 0,
                }))}
                dataKey="label"
                series={[{ name: "count", label: t("service:report.columns.total"), color: "blue.6" }]}
                valueFormatter={formatNumber}
                yAxisProps={{ allowDecimals: false }}
              />
            </Card>
          </SimpleGrid>

          <Text fw={600}>{t("service:report.byAssignee")}</Text>
          <ReportTable
            rows={rows}
            rowKey={(r) => r.assignedUserId ?? "unassigned"}
            columns={[
              {
                key: "assignee",
                header: t("service:report.columns.assignee"),
                render: (r) =>
                  r.assignedUserId ? (
                    nameOf(r)
                  ) : (
                    <Badge variant="light" color="gray">
                      {nameOf(r)}
                    </Badge>
                  ),
              },
              {
                key: "total",
                header: t("service:report.columns.total"),
                numeric: true,
                render: (r) => formatNumber(r.totalCount),
              },
              {
                key: "open",
                header: t("service:report.columns.open"),
                numeric: true,
                render: (r) => formatNumber(r.openCount),
              },
              {
                key: "resolved",
                header: t("service:report.columns.resolved"),
                numeric: true,
                render: (r) => formatNumber(r.resolvedCount),
              },
              {
                key: "avgFirstResponse",
                header: t("service:report.columns.avgFirstResponse"),
                numeric: true,
                render: (r) => duration(r.avgFirstResponseMinutes),
              },
              {
                key: "avgResolution",
                header: t("service:report.columns.avgResolution"),
                numeric: true,
                render: (r) => duration(r.avgResolutionMinutes),
              },
              {
                key: "slaBreached",
                header: t("service:report.columns.slaBreached"),
                numeric: true,
                render: (r) => formatNumber(r.slaBreachedCount),
              },
            ]}
          />
        </>
      )}
    </ReportPanel>
  );
}
