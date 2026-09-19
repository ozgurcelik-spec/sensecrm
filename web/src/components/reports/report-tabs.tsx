import { useTranslation } from "react-i18next";
import { Badge, SegmentedControl, Select } from "@mantine/core";
import { BarChart, DonutChart, FunnelChart } from "@mantine/charts";
import {
  useActivitiesByUser,
  useLeadsBySource,
  useSalesByOwner,
  useSalesFunnel,
  useWonLost,
} from "@/hooks/use-reports";
import { usePermission } from "@/hooks/use-permission";
import { usePipelines } from "@/hooks/use-pipelines";
import { formatMoney, formatNumber } from "@/lib/format";
import {
  SOURCE_COLORS,
  formatPercent,
  formatPeriod,
  ratio,
  toFunnelCells,
} from "@/lib/report-format";
import type { DateRange } from "@/lib/report-range";
import { stageColor } from "@/lib/stage";
import { PERMISSIONS, type WonLostGroupBy } from "@/types";
import { ReportPanel, ReportTable, type CsvExport } from "./report-panel";

const CHART_HEIGHT = 300;

interface RangeTabProps {
  /** null while a custom range is incomplete: nothing is requested. */
  range: DateRange | null;
}

/** File name of a range report: `<name>_<from>_<to>`. */
function rangeFile(name: string, range: DateRange | null): string {
  return range ? `${name}_${range.from}_${range.to}` : name;
}

export function FunnelReport({
  pipelineId,
  onPipelineChange,
}: {
  pipelineId: string;
  onPipelineChange: (id: string | null) => void;
}) {
  const { t } = useTranslation(["reports"]);
  // Listing pipelines needs deals.read; without it the default pipeline is reported.
  const canListPipelines = usePermission(PERMISSIONS.crmDealsRead);
  const pipelines = usePipelines(canListPipelines);
  const { data, isLoading, error, refetch } = useSalesFunnel({
    pipelineId: pipelineId || undefined,
  });
  const stages = data?.stages ?? [];
  const cells = toFunnelCells(stages);

  const csv: CsvExport = {
    filename: t("reports:files.funnel"),
    headers: [
      t("reports:columns.stage"),
      t("reports:columns.kind"),
      t("reports:columns.probability"),
      t("reports:columns.count"),
      t("reports:columns.amount"),
    ],
    rows: stages.map((s) => [
      s.name,
      t(`reports:kinds.${s.kind}`),
      s.probability,
      s.count,
      s.totalAmount,
    ]),
  };

  return (
    <ReportPanel
      isLoading={isLoading}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={stages.length === 0}
      csv={csv}
      controls={
        canListPipelines &&
        (pipelines.data?.length ?? 0) > 0 && (
          <Select
            label={t("reports:pipeline")}
            w={240}
            data={(pipelines.data ?? []).map((p) => ({ value: p.id, label: p.name }))}
            value={pipelineId || data?.pipelineId || null}
            onChange={onPipelineChange}
            allowDeselect={false}
          />
        )
      }
    >
      <FunnelChart
        data={cells}
        size={CHART_HEIGHT}
        mx="auto"
        withLabels
        labelsPosition="inside"
        withLegend
        valueFormatter={formatNumber}
      />
      <ReportTable
        rows={stages}
        rowKey={(s) => s.id}
        columns={[
          { key: "stage", header: t("reports:columns.stage"), render: (s) => s.name },
          {
            key: "kind",
            header: t("reports:columns.kind"),
            render: (s) => (
              <Badge variant="light" color={stageColor(s.kind)}>
                {t(`reports:kinds.${s.kind}`)}
              </Badge>
            ),
          },
          {
            key: "probability",
            header: t("reports:columns.probability"),
            numeric: true,
            render: (s) => `${formatNumber(s.probability)}%`,
          },
          {
            key: "count",
            header: t("reports:columns.count"),
            numeric: true,
            render: (s) => formatNumber(s.count),
          },
          {
            key: "amount",
            header: t("reports:columns.amount"),
            numeric: true,
            render: (s) => formatMoney(s.totalAmount),
          },
        ]}
      />
    </ReportPanel>
  );
}

export function WonLostReport({
  range,
  groupBy,
  onGroupByChange,
}: RangeTabProps & { groupBy: WonLostGroupBy; onGroupByChange: (value: WonLostGroupBy) => void }) {
  const { t } = useTranslation(["reports"]);
  const { data, isLoading, error, refetch } = useWonLost(
    { from: range?.from, to: range?.to, groupBy },
    !!range
  );
  const rows = data ?? [];

  const csv: CsvExport = {
    filename: rangeFile(t("reports:files.wonLost"), range),
    headers: [
      t("reports:columns.period"),
      t("reports:columns.wonCount"),
      t("reports:columns.wonAmount"),
      t("reports:columns.lostCount"),
      t("reports:columns.lostAmount"),
    ],
    rows: rows.map((r) => [r.period, r.wonCount, r.wonAmount, r.lostCount, r.lostAmount]),
  };

  return (
    <ReportPanel
      isLoading={isLoading && !!range}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={rows.length === 0}
      csv={csv}
      controls={
        <SegmentedControl
          aria-label={t("reports:groupBy.label")}
          value={groupBy}
          onChange={(value) => onGroupByChange(value as WonLostGroupBy)}
          data={[
            { value: "month", label: t("reports:groupBy.month") },
            { value: "week", label: t("reports:groupBy.week") },
          ]}
        />
      }
    >
      <BarChart
        h={CHART_HEIGHT}
        data={rows.map((r) => ({ ...r, label: formatPeriod(r.period) }))}
        dataKey="label"
        series={[
          { name: "wonCount", label: t("reports:series.won"), color: "green.6" },
          { name: "lostCount", label: t("reports:series.lost"), color: "red.6" },
        ]}
        withLegend
        valueFormatter={formatNumber}
        yAxisProps={{ allowDecimals: false }}
      />
      <ReportTable
        rows={rows}
        rowKey={(r) => r.period}
        columns={[
          {
            key: "period",
            header: t("reports:columns.period"),
            render: (r) => formatPeriod(r.period),
          },
          {
            key: "wonCount",
            header: t("reports:columns.wonCount"),
            numeric: true,
            render: (r) => formatNumber(r.wonCount),
          },
          {
            key: "wonAmount",
            header: t("reports:columns.wonAmount"),
            numeric: true,
            render: (r) => formatMoney(r.wonAmount),
          },
          {
            key: "lostCount",
            header: t("reports:columns.lostCount"),
            numeric: true,
            render: (r) => formatNumber(r.lostCount),
          },
          {
            key: "lostAmount",
            header: t("reports:columns.lostAmount"),
            numeric: true,
            render: (r) => formatMoney(r.lostAmount),
          },
        ]}
      />
    </ReportPanel>
  );
}

export function LeadSourcesReport({ range }: RangeTabProps) {
  const { t } = useTranslation(["reports", "crm"]);
  const { data, isLoading, error, refetch } = useLeadsBySource(
    { from: range?.from, to: range?.to },
    !!range
  );
  const rows = (data ?? []).map((r) => ({
    ...r,
    label: t(`crm:leads.sources.${r.source}`, { defaultValue: r.source }),
    conversion: ratio(r.convertedCount, r.count),
  }));

  const csv: CsvExport = {
    filename: rangeFile(t("reports:files.leadSources"), range),
    headers: [
      t("reports:columns.source"),
      t("reports:columns.count"),
      t("reports:columns.convertedCount"),
      `${t("reports:columns.conversionRate")} (%)`,
    ],
    rows: rows.map((r) => [
      r.label,
      r.count,
      r.convertedCount,
      Math.round(r.conversion * 1000) / 10,
    ]),
  };

  return (
    <ReportPanel
      isLoading={isLoading && !!range}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={rows.length === 0}
      csv={csv}
    >
      <DonutChart
        data={rows.map((r, i) => ({
          name: r.label,
          value: r.count,
          color: SOURCE_COLORS[i % SOURCE_COLORS.length] as string,
        }))}
        size={CHART_HEIGHT - 60}
        mx="auto"
        withLegend
        withTooltip
      />
      <ReportTable
        rows={rows}
        rowKey={(r) => r.source}
        columns={[
          { key: "source", header: t("reports:columns.source"), render: (r) => r.label },
          {
            key: "count",
            header: t("reports:columns.count"),
            numeric: true,
            render: (r) => formatNumber(r.count),
          },
          {
            key: "converted",
            header: t("reports:columns.convertedCount"),
            numeric: true,
            render: (r) => formatNumber(r.convertedCount),
          },
          {
            key: "rate",
            header: t("reports:columns.conversionRate"),
            numeric: true,
            render: (r) => formatPercent(r.conversion),
          },
        ]}
      />
    </ReportPanel>
  );
}

export function ByOwnerReport({ range }: RangeTabProps) {
  const { t } = useTranslation(["reports"]);
  const { data, isLoading, error, refetch } = useSalesByOwner(
    { from: range?.from, to: range?.to },
    !!range
  );
  const rows = data ?? [];

  const csv: CsvExport = {
    filename: rangeFile(t("reports:files.byOwner"), range),
    headers: [
      t("reports:columns.owner"),
      t("reports:columns.openDealCount"),
      t("reports:columns.openDealAmount"),
      t("reports:columns.wonCount"),
      t("reports:columns.wonAmount"),
      t("reports:columns.leadCount"),
    ],
    rows: rows.map((r) => [
      r.ownerName,
      r.openDealCount,
      r.openDealAmount,
      r.wonCount,
      r.wonAmount,
      r.leadCount,
    ]),
  };

  return (
    <ReportPanel
      isLoading={isLoading && !!range}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={rows.length === 0}
      csv={csv}
    >
      <BarChart
        h={CHART_HEIGHT}
        data={rows}
        dataKey="ownerName"
        series={[
          { name: "openDealAmount", label: t("reports:series.openAmount"), color: "blue.6" },
          { name: "wonAmount", label: t("reports:series.wonAmount"), color: "green.6" },
        ]}
        withLegend
        valueFormatter={(value) => formatMoney(value)}
      />
      <ReportTable
        rows={rows}
        rowKey={(r) => r.ownerUserId}
        columns={[
          { key: "owner", header: t("reports:columns.owner"), render: (r) => r.ownerName },
          {
            key: "openCount",
            header: t("reports:columns.openDealCount"),
            numeric: true,
            render: (r) => formatNumber(r.openDealCount),
          },
          {
            key: "openAmount",
            header: t("reports:columns.openDealAmount"),
            numeric: true,
            render: (r) => formatMoney(r.openDealAmount),
          },
          {
            key: "wonCount",
            header: t("reports:columns.wonCount"),
            numeric: true,
            render: (r) => formatNumber(r.wonCount),
          },
          {
            key: "wonAmount",
            header: t("reports:columns.wonAmount"),
            numeric: true,
            render: (r) => formatMoney(r.wonAmount),
          },
          {
            key: "leadCount",
            header: t("reports:columns.leadCount"),
            numeric: true,
            render: (r) => formatNumber(r.leadCount),
          },
        ]}
      />
    </ReportPanel>
  );
}

export function ActivitiesReport({ range }: RangeTabProps) {
  const { t } = useTranslation(["reports"]);
  const { data, isLoading, error, refetch } = useActivitiesByUser(
    { from: range?.from, to: range?.to },
    !!range
  );
  const rows = data ?? [];

  const csv: CsvExport = {
    filename: rangeFile(t("reports:files.activities"), range),
    headers: [
      t("reports:columns.user"),
      t("reports:columns.completedCount"),
      t("reports:columns.openCount"),
      t("reports:columns.overdueCount"),
    ],
    rows: rows.map((r) => [r.userName, r.completedCount, r.openCount, r.overdueCount]),
  };

  return (
    <ReportPanel
      isLoading={isLoading && !!range}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={rows.length === 0}
      csv={csv}
    >
      <BarChart
        h={CHART_HEIGHT}
        data={rows}
        dataKey="userName"
        type="stacked"
        series={[
          { name: "completedCount", label: t("reports:series.completed"), color: "green.6" },
          { name: "openCount", label: t("reports:series.open"), color: "blue.6" },
          { name: "overdueCount", label: t("reports:series.overdue"), color: "red.6" },
        ]}
        withLegend
        valueFormatter={formatNumber}
        yAxisProps={{ allowDecimals: false }}
      />
      <ReportTable
        rows={rows}
        rowKey={(r) => r.userId}
        columns={[
          { key: "user", header: t("reports:columns.user"), render: (r) => r.userName },
          {
            key: "completed",
            header: t("reports:columns.completedCount"),
            numeric: true,
            render: (r) => formatNumber(r.completedCount),
          },
          {
            key: "open",
            header: t("reports:columns.openCount"),
            numeric: true,
            render: (r) => formatNumber(r.openCount),
          },
          {
            key: "overdue",
            header: t("reports:columns.overdueCount"),
            numeric: true,
            render: (r) => formatNumber(r.overdueCount),
          },
        ]}
      />
    </ReportPanel>
  );
}
