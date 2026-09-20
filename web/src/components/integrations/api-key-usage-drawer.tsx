import { lazy, Suspense, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Drawer, Group, Select, SimpleGrid, Skeleton, Stack, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { useApiKeyUsage } from "@/hooks/use-integrations";
import { formatNumber } from "@/lib/format";
import { usageRange } from "@/lib/integrations";
import type { ApiKey } from "@/types";

const UsageChart = lazy(() => import("./api-key-usage-chart"));

const RANGES = ["7", "30", "90"] as const;

/** Usage of one key (`GET .../usage`): totals and a daily bar chart for the last 7 / 30 / 90 days. */
export function ApiKeyUsageDrawer({ apiKey, onClose }: { apiKey: ApiKey; onClose: () => void }) {
  const { t } = useTranslation(["integrations"]);
  const [range, setRange] = useState<(typeof RANGES)[number]>("30");
  const { from, to } = useMemo(() => usageRange(Number(range)), [range]);
  const { data, isLoading, error, refetch } = useApiKeyUsage(apiKey.id, from, to);

  const totals = (data ?? []).reduce(
    (sum, day) => ({
      requests: sum.requests + day.requests,
      errors: sum.errors + day.errors,
      throttled: sum.throttled + day.throttled,
    }),
    { requests: 0, errors: 0, throttled: 0 }
  );
  const empty = !!data && data.every((day) => day.requests === 0 && day.errors === 0 && day.throttled === 0);

  return (
    <Drawer
      opened
      onClose={onClose}
      position="right"
      size="lg"
      title={t("integrations:apiKeys.usage.title", { name: apiKey.name })}
    >
      <Stack gap="md" data-testid="usage-drawer">
        <Group justify="space-between">
          <Text size="sm" ff="monospace" c="dimmed">
            {apiKey.prefix}
          </Text>
          <Select
            aria-label={t("integrations:apiKeys.usage.range")}
            w={170}
            allowDeselect={false}
            data={RANGES.map((days) => ({ value: days, label: t("integrations:apiKeys.usage.lastDays", { count: Number(days) }) }))}
            value={range}
            onChange={(value) => value && setRange(value as (typeof RANGES)[number])}
          />
        </Group>
        {error ? (
          <LoadError error={error} onRetry={() => void refetch()} />
        ) : isLoading || !data ? (
          <Skeleton h={220} />
        ) : empty ? (
          <Text size="sm" c="dimmed" ta="center" py="lg" data-testid="usage-empty">
            {t("integrations:apiKeys.usage.empty")}
          </Text>
        ) : (
          <>
            <SimpleGrid cols={3} spacing="sm" data-testid="usage-totals">
              <Stack gap={0}>
                <Text size="xs" c="dimmed">
                  {t("integrations:apiKeys.usage.requests")}
                </Text>
                <Text fw={700} fz="xl" data-testid="usage-total-requests">
                  {formatNumber(totals.requests)}
                </Text>
              </Stack>
              <Stack gap={0}>
                <Text size="xs" c="dimmed">
                  {t("integrations:apiKeys.usage.errors")}
                </Text>
                <Text fw={700} fz="xl" data-testid="usage-total-errors">
                  {formatNumber(totals.errors)}
                </Text>
              </Stack>
              <Stack gap={0}>
                <Text size="xs" c="dimmed">
                  {t("integrations:apiKeys.usage.throttled")}
                </Text>
                <Text fw={700} fz="xl" data-testid="usage-total-throttled">
                  {formatNumber(totals.throttled)}
                </Text>
              </Stack>
            </SimpleGrid>
            <Suspense fallback={<Skeleton h={220} />}>
              <UsageChart items={data} />
            </Suspense>
          </>
        )}
      </Stack>
    </Drawer>
  );
}
