import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, SimpleGrid, Text } from "@mantine/core";
import { useMarketingSummary } from "@/hooks/use-campaigns";
import { formatNumber } from "@/lib/format";
import { DashboardWidget } from "./dashboard-widget";

function Stat({ label, value, testId }: { label: string; value: string; testId: string }) {
  return (
    <div data-testid={testId}>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text fz={24} fw={700} data-testid="stat-value">
        {value}
      </Text>
    </div>
  );
}

/**
 * "Campaign summary" dashboard card from the marketing report's default range. The home page mounts
 * it only for users with both `crm.reports.read` and `crm.campaigns.read`.
 */
export function CampaignSummaryWidget() {
  const { t } = useTranslation(["campaigns"]);
  const { data, isLoading, error, refetch } = useMarketingSummary({});
  const active = data?.byStatus.find((s) => s.status === "active")?.count ?? 0;

  return (
    <DashboardWidget
      testId="widget-campaigns"
      title={t("campaigns:dashboard.title")}
      action={
        <Anchor component={Link} to="/app/campaigns" size="sm">
          {t("campaigns:dashboard.viewAll")}
        </Anchor>
      }
      isLoading={isLoading}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={!data || data.campaignCount === 0}
      emptyMessage={t("campaigns:dashboard.empty")}
      skeletonHeight={80}
    >
      {data && (
        <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="md">
          <Stat
            testId="campaign-stat-count"
            label={t("campaigns:dashboard.campaigns")}
            value={formatNumber(data.campaignCount)}
          />
          <Stat
            testId="campaign-stat-active"
            label={t("campaigns:dashboard.active")}
            value={formatNumber(active)}
          />
          <Stat
            testId="campaign-stat-response"
            label={t("campaigns:dashboard.responseRate")}
            value={`${formatNumber(Math.round(data.totals.responseRate * 100) / 100)}%`}
          />
          <Stat
            testId="campaign-stat-converted"
            label={t("campaigns:dashboard.converted")}
            value={formatNumber(data.totals.convertedCount)}
          />
        </SimpleGrid>
      )}
    </DashboardWidget>
  );
}
