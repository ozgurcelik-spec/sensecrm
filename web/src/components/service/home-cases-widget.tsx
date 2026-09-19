import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, SimpleGrid, Text } from "@mantine/core";
import { DashboardWidget } from "@/components/dashboard/dashboard-widget";
import { useCasesSummary } from "@/hooks/use-cases";
import { formatNumber } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";

const OPEN = "status=new,open,pending";

/** Home "Service" card: open, overdue, mine and unassigned counters, each linking to the filtered list. */
export function HomeCasesWidget() {
  const { t } = useTranslation(["service"]);
  const meId = useAuthStore((state) => state.me?.user.id);
  const summary = useCasesSummary();
  const data = summary.data;

  const counters = [
    { key: "open", value: data?.openCount, to: `/app/cases?${OPEN}` },
    {
      key: "overdue",
      value: data?.overdueCount,
      to: `/app/cases?${OPEN}&slaState=breached`,
      alert: true,
    },
    {
      key: "mine",
      value: data?.mineCount,
      to: `/app/cases?${OPEN}${meId ? `&assignedUserId=${encodeURIComponent(meId)}` : ""}`,
    },
    { key: "unassigned", value: data?.unassignedCount, to: `/app/cases?${OPEN}&unassigned=true` },
  ] as const;

  return (
    <DashboardWidget
      testId="widget-cases"
      title={t("service:home.title")}
      action={
        <Anchor component={Link} to="/app/cases" size="sm">
          {t("service:home.viewAll")}
        </Anchor>
      }
      isLoading={summary.isLoading}
      error={summary.error}
      onRetry={() => void summary.refetch()}
      skeletonHeight={72}
    >
      <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
        {counters.map((c) => (
          <Anchor
            key={c.key}
            component={Link}
            to={c.to}
            underline="never"
            c="var(--mantine-color-text)"
            data-testid={`cases-link-${c.key}`}
          >
            <Text
              fz={24}
              fw={700}
              lh={1.1}
              c={"alert" in c && c.alert && (c.value ?? 0) > 0 ? "red" : undefined}
              data-testid={`cases-${c.key}`}
            >
              {formatNumber(c.value ?? 0)}
            </Text>
            <Text size="xs" c="dimmed">
              {t(`service:home.${c.key}`)}
            </Text>
          </Anchor>
        ))}
      </SimpleGrid>
    </DashboardWidget>
  );
}
