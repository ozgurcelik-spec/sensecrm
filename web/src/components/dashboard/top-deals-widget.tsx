import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Badge, Group, Stack, Text } from "@mantine/core";
import { useDeals } from "@/hooks/use-deals";
import { formatMoney } from "@/lib/format";
import { DashboardWidget } from "./dashboard-widget";

const DEAL_LIMIT = 5;

/** The open deals with the highest amount, largest first (needs crm.deals.read). */
export function TopDealsWidget() {
  const { t } = useTranslation(["home"]);
  const { data, isLoading, error, refetch } = useDeals({
    stageKind: "open",
    sort: "-amount",
    page: 1,
    pageSize: DEAL_LIMIT,
  });
  const deals = data?.items ?? [];

  return (
    <DashboardWidget
      testId="widget-top-deals"
      title={t("home:topDeals.title")}
      action={
        <Anchor component={Link} to="/app/deals" size="sm">
          {t("home:topDeals.viewAll")}
        </Anchor>
      }
      isLoading={isLoading}
      error={error}
      onRetry={() => void refetch()}
      isEmpty={deals.length === 0}
      emptyMessage={t("home:topDeals.empty")}
      skeletonHeight={200}
    >
      <Stack gap="sm">
        {deals.map((deal) => (
          <Group key={deal.id} justify="space-between" wrap="nowrap" data-testid="top-deal">
            <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
              <Anchor component={Link} to={`/app/deals/${deal.id}`} size="sm" fw={500} truncate>
                {deal.name}
              </Anchor>
              <Text size="xs" c="dimmed" truncate>
                {deal.accountName}
              </Text>
            </Stack>
            <Badge variant="light" size="sm">
              {deal.stageName}
            </Badge>
            <Text size="sm" fw={600} miw={90} ta="right">
              {formatMoney(deal.amount, deal.currency)}
            </Text>
          </Group>
        ))}
      </Stack>
    </DashboardWidget>
  );
}
