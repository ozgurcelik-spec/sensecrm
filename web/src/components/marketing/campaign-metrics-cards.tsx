import { useTranslation } from "react-i18next";
import { Badge, Card, Group, SimpleGrid, Skeleton, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { useCampaignMetrics } from "@/hooks/use-campaigns";
import { MEMBER_STATUS_COLOR } from "@/lib/campaign";
import { formatMoney, formatNumber } from "@/lib/format";
import { MEMBER_STATUSES } from "@/types";

function MetricCard({
  label,
  value,
  hint,
  testId,
}: {
  label: string;
  value: string;
  hint?: string;
  testId: string;
}) {
  return (
    <Card withBorder padding="md" data-testid={testId}>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text fz={26} fw={700} data-testid="metric-value">
        {value}
      </Text>
      {hint && (
        <Text size="xs" c="dimmed">
          {hint}
        </Text>
      )}
    </Card>
  );
}

/** Percentage from the server's 0-100 number ("46.67" -> "%46,67" / "46.67%"). */
function percent(value: number): string {
  return `${formatNumber(Math.round(value * 100) / 100)}%`;
}

/** Metric cards of the campaign "General" tab (`GET /campaigns/{id}/metrics`, computed on the server). */
export function CampaignMetricsCards({ campaignId }: { campaignId: string }) {
  const { t } = useTranslation(["campaigns"]);
  const { data, isLoading, error, refetch } = useCampaignMetrics(campaignId);

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading || !data) {
    return (
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} h={92} data-testid="metric-skeleton" />
        ))}
      </SimpleGrid>
    );
  }

  return (
    <>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
        <MetricCard
          testId="metric-members"
          label={t("campaigns:metrics.members")}
          value={formatNumber(data.memberCount)}
          hint={t("campaigns:metrics.membersHint", {
            leads: formatNumber(data.leadCount),
            contacts: formatNumber(data.contactCount),
          })}
        />
        <MetricCard
          testId="metric-response-rate"
          label={t("campaigns:metrics.responseRate")}
          value={percent(data.responseRate)}
          hint={t("campaigns:metrics.responseHint", {
            responded: formatNumber(data.responseCount),
            contacted: formatNumber(data.contactedCount),
          })}
        />
        <MetricCard
          testId="metric-converted"
          label={t("campaigns:metrics.converted")}
          value={formatNumber(data.convertedCount)}
          hint={t("campaigns:metrics.convertedHint", { rate: percent(data.conversionRate) })}
        />
        <MetricCard
          testId="metric-cost-per-lead"
          label={t("campaigns:metrics.costPerLead")}
          value={data.costPerLead === undefined ? "—" : formatMoney(data.costPerLead, data.currency)}
          hint={data.costPerLead === undefined ? t("campaigns:metrics.costPerLeadNone") : undefined}
        />
      </SimpleGrid>
      <Card withBorder padding="md" mt="sm">
        <Text size="sm" fw={600} mb="xs">
          {t("campaigns:metrics.breakdown")}
        </Text>
        <Group gap="xs" data-testid="metric-breakdown">
          {MEMBER_STATUSES.map((status) => (
            <Badge key={status} variant="light" color={MEMBER_STATUS_COLOR[status]}>
              {t(`campaigns:memberStatuses.${status}`)}: {formatNumber(data.statusCounts[status] ?? 0)}
            </Badge>
          ))}
        </Group>
      </Card>
    </>
  );
}
