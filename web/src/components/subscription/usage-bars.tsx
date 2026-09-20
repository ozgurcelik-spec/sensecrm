import { useTranslation } from "react-i18next";
import { Group, Progress, Stack, Text } from "@mantine/core";
import { formatNumber } from "@/lib/format";
import { usageLevel, usagePercent, type UsageLevel } from "@/lib/entitlements";

const LEVEL_COLOR: Record<UsageLevel, string> = { ok: "blue", warn: "orange", full: "red" };

interface UsageBarProps {
  label: string;
  used: number;
  /** Undefined = no limit: the count is shown with "Sınırsız" instead of a bar. */
  max?: number;
  /** Extra line under the bar (e.g. "3 active + 1 pending"). */
  detail?: string;
  testId: string;
}

/** One limit against its usage: the bar is orange from 80 % and red at 100 %; without a limit it says "Sınırsız". */
export function UsageBar({ label, used, max, detail, testId }: UsageBarProps) {
  const { t } = useTranslation(["subscription"]);
  if (max === undefined) {
    return (
      <Group justify="space-between" wrap="nowrap" data-testid={testId} data-level="unlimited">
        <Text size="sm">{label}</Text>
        <Text size="sm" c="dimmed">
          {formatNumber(used)} · {t("subscription:usage.unlimited")}
        </Text>
      </Group>
    );
  }
  const level = usageLevel(used, max);
  const percent = usagePercent(used, max);
  return (
    <Stack gap={4} data-testid={testId} data-level={level}>
      <Group justify="space-between" wrap="nowrap">
        <Text size="sm">{label}</Text>
        <Text size="sm" fw={500}>
          {formatNumber(used)} / {formatNumber(max)} ({percent}%)
        </Text>
      </Group>
      <Progress
        value={Math.min(100, percent)}
        color={LEVEL_COLOR[level]}
        size="md"
        radius="xl"
        aria-label={label}
        aria-valuetext={`${formatNumber(used)} / ${formatNumber(max)}`}
      />
      {detail && (
        <Text size="xs" c="dimmed">
          {detail}
        </Text>
      )}
    </Stack>
  );
}
