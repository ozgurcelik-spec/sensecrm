import type { ReactNode } from "react";
import { Link } from "react-router";
import { Card, Group, Skeleton, Text, ThemeIcon } from "@mantine/core";
import { ArrowDownRight, ArrowUpRight } from "lucide-react";
import { formatNumber } from "@/lib/format";

interface KpiCardProps {
  to: string;
  icon: ReactNode;
  label: string;
  value: string;
  loading: boolean;
  /** Shown instead of the value when the request failed. */
  failedText?: string;
  /** Change against the previous period, in percent; omitted when there is nothing to compare. */
  trend?: number | null;
}

function Trend({ percent }: { percent: number }) {
  const up = percent >= 0;
  const Icon = up ? ArrowUpRight : ArrowDownRight;
  return (
    <Group gap={2} wrap="nowrap" c={up ? "green.7" : "red.7"} data-testid="kpi-trend">
      <Icon size={16} aria-hidden="true" />
      <Text size="sm" fw={600} c="inherit">
        {formatNumber(Math.abs(Math.round(percent)))}%
      </Text>
    </Group>
  );
}

/** Dashboard KPI tile: icon, label, big value and an optional up/down trend; the whole tile is a link. */
export function KpiCard({ to, icon, label, value, loading, failedText, trend }: KpiCardProps) {
  return (
    <Card
      component={Link}
      to={to}
      withBorder
      padding="lg"
      radius="lg"
      shadow="xs"
      style={{ textDecoration: "none" }}
    >
      <Group gap="sm" mb="sm" wrap="nowrap">
        <ThemeIcon variant="light" size="lg" radius="md">
          {icon}
        </ThemeIcon>
        <Text size="sm" c="dimmed">
          {label}
        </Text>
      </Group>
      {loading ? (
        <Skeleton h={32} w={120} />
      ) : (
        <Group gap="sm" align="baseline" wrap="nowrap">
          <Text fz={28} fw={700} lh={1.1} c="var(--mantine-color-text)" data-testid="stat-value">
            {failedText ?? value}
          </Text>
          {!failedText && trend != null && <Trend percent={trend} />}
        </Group>
      )}
    </Card>
  );
}
