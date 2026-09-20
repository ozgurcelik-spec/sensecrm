import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { LineChart } from "@mantine/charts";
import { Button, Group, SegmentedControl, Select, Skeleton, Stack, Table, Text } from "@mantine/core";
import { RefreshCw } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { usePlatformUsage, useRefreshPlatformUsage } from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatNumber } from "@/lib/format";
import { daysAgo, metricKeys } from "@/lib/platform";

const RANGES = ["30", "90", "365"] as const;
type RangeDays = (typeof RANGES)[number];
const DEFAULT_RANGE: RangeDays = "90";
const CHART_HEIGHT = 300;

/** Loaded lazily (recharts stays out of the rest of the console). */
export default function UsageChart({ tenantId }: { tenantId: string }) {
  const { t } = useTranslation(["platform", "subscription"]);
  const [range, setRange] = useState<RangeDays>(DEFAULT_RANGE);
  const [view, setView] = useState<"chart" | "table">("chart");
  const [metricChoice, setMetricChoice] = useState<string | null>(null);

  const query = useMemo(() => ({ from: daysAgo(Number(range) - 1) }), [range]);
  const { data, isLoading, error, refetch } = usePlatformUsage(tenantId, query);
  const refresh = useRefreshPlatformUsage(tenantId);

  const days = useMemo(() => data ?? [], [data]);
  const keys = useMemo(() => metricKeys(days), [days]);
  const metric =
    metricChoice && keys.includes(metricChoice)
      ? metricChoice
      : (keys.find((k) => k === "sales.records") ?? keys[0] ?? null);

  const metricLabel = (key: string) => {
    const [module = "", name = ""] = key.split(/\.(.+)/);
    const moduleText = t(`subscription:modules.${module}`, { defaultValue: module });
    const nameText =
      name === "records"
        ? t("platform:usage.total")
        : t(`platform:usage.metrics.${name}`, { defaultValue: name });
    return `${moduleText}: ${nameText}`;
  };

  async function doRefresh() {
    try {
      await refresh.mutateAsync();
      toast({ variant: "success", description: t("platform:usage.refreshed") });
    } catch (err) {
      toastApiError(err);
    }
  }

  const chartData = days.map((d) => ({
    day: formatCalendarDate(d.day),
    users: d.usersActive,
    metric: metric ? (d.metrics[metric] ?? null) : null,
  }));

  return (
    <Stack gap="md">
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="sm" align="flex-end" wrap="wrap">
          <SegmentedControl
            aria-label={t("platform:usage.range")}
            value={range}
            onChange={(value) => setRange(value as RangeDays)}
            data={RANGES.map((value) => ({
              value,
              label: t("platform:usage.days", { count: Number(value) }),
            }))}
          />
          {keys.length > 0 && (
            <Select
              aria-label={t("platform:usage.metric")}
              w={260}
              allowDeselect={false}
              data={keys.map((key) => ({ value: key, label: metricLabel(key) }))}
              value={metric}
              onChange={setMetricChoice}
            />
          )}
          <SegmentedControl
            aria-label={t("platform:usage.view")}
            value={view}
            onChange={(value) => setView(value as "chart" | "table")}
            data={[
              { value: "chart", label: t("platform:usage.chart") },
              { value: "table", label: t("platform:usage.table") },
            ]}
          />
        </Group>
        <Button
          variant="default"
          leftSection={<RefreshCw size={16} />}
          onClick={() => void doRefresh()}
          loading={refresh.isPending}
        >
          {t("platform:usage.refresh")}
        </Button>
      </Group>

      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading ? (
        <Skeleton h={CHART_HEIGHT} />
      ) : days.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="xl" data-testid="usage-empty">
          {t("platform:usage.empty")}
        </Text>
      ) : view === "chart" ? (
        <LineChart
          h={CHART_HEIGHT}
          data={chartData}
          dataKey="day"
          series={[
            { name: "users", label: t("platform:usage.activeUsers"), color: "blue.6" },
            ...(metric ? [{ name: "metric", label: metricLabel(metric), color: "teal.6" }] : []),
          ]}
          curveType="linear"
          withLegend
          connectNulls
          yAxisProps={{ allowDecimals: false }}
        />
      ) : (
        <Table.ScrollContainer minWidth={480}>
          <Table verticalSpacing="xs" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("platform:usage.day")}</Table.Th>
                <Table.Th ta="right">{t("platform:usage.activeUsers")}</Table.Th>
                <Table.Th ta="right">{t("platform:usage.pendingUsers")}</Table.Th>
                {metric && <Table.Th ta="right">{metricLabel(metric)}</Table.Th>}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {[...days].reverse().map((d) => (
                <Table.Tr key={d.day}>
                  <Table.Td>{formatCalendarDate(d.day)}</Table.Td>
                  <Table.Td ta="right">{formatNumber(d.usersActive)}</Table.Td>
                  <Table.Td ta="right">{formatNumber(d.usersPending)}</Table.Td>
                  {metric && (
                    <Table.Td ta="right">
                      {d.metrics[metric] === undefined ? "-" : formatNumber(d.metrics[metric])}
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Stack>
  );
}
