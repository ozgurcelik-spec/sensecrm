import { useTranslation } from "react-i18next";
import { Alert, Badge, Card, Group, Skeleton, Stack, Text } from "@mantine/core";
import { Info, TriangleAlert } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { StorageBreakdownCard, StorageUsageBar } from "@/components/files/storage-usage";
import { TenantStatusBadge } from "@/components/platform/status-badge";
import { UsageBar } from "@/components/subscription/usage-bars";
import { useSubscription } from "@/hooks/use-subscription";
import { formatDateTime } from "@/lib/dates";
import { formatBytes } from "@/lib/files";
import { formatCalendarDate } from "@/lib/format";
import { GATED_MODULES, RECORD_MODULES, type SubscriptionInfo } from "@/types";

function PlanUsage({ info }: { info: SubscriptionInfo }) {
  const { t } = useTranslation(["subscription", "files"]);
  const moduleName = (module: string) => t(`subscription:modules.${module}`, { defaultValue: module });
  const isOff = (module: string) =>
    (GATED_MODULES as readonly string[]).includes(module) &&
    info.modules[module as keyof typeof info.modules] === false;

  // Records are counted for every module that is on; a bar needs a limit, otherwise only the count is shown.
  const recordModules = RECORD_MODULES.filter(
    (module) => !isOff(module) && (info.usage.records[module] !== undefined || info.limits.maxRecords[module] !== undefined)
  );

  return (
    <Stack gap="md" maw={760}>
      <Card withBorder padding="lg" data-testid="plan-card">
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Stack gap={2}>
            <Text size="xs" c="dimmed">
              {t("subscription:plan.current")}
            </Text>
            <Text fw={700} fz="xl">
              {info.planName}
            </Text>
          </Stack>
          <TenantStatusBadge status={info.status} />
        </Group>
        {info.trialEndsOn && (
          <Text size="sm" mt="sm">
            {t("subscription:plan.trialEnds", { date: formatCalendarDate(info.trialEndsOn) })}
            {info.status === "trial" && info.trialDaysLeft !== undefined
              ? ` (${t("subscription:plan.daysLeft", { count: info.trialDaysLeft })})`
              : ""}
          </Text>
        )}
      </Card>

      {info.overLimit.length > 0 && (
        <Alert
          color="orange"
          variant="light"
          icon={<TriangleAlert size={16} />}
          title={t("subscription:overLimit.title")}
          data-testid="over-limit"
        >
          <Stack gap={2}>
            <Text size="sm">{t("subscription:overLimit.hint")}</Text>
            {info.overLimit.map((entry) => (
              <Text size="sm" key={`${entry.limit}-${entry.module ?? ""}`}>
                {entry.limit === "users"
                  ? t("subscription:limits.users")
                  : entry.limit === "storage"
                    ? t("files:plan.storage")
                    : t("subscription:limits.recordsOf", { module: moduleName(entry.module ?? "") })}
                {entry.limit === "storage"
                  ? `: ${formatBytes(entry.used)} / ${formatBytes(entry.max)}`
                  : `: ${entry.used} / ${entry.max}`}
              </Text>
            ))}
          </Stack>
        </Alert>
      )}

      <Card withBorder padding="lg">
        <Text fw={600} mb="md">
          {t("subscription:limits.title")}
        </Text>
        <Stack gap="md">
          <UsageBar
            testId="usage-users"
            label={t("subscription:limits.users")}
            used={info.usage.users + info.usage.pendingUsers}
            max={info.limits.maxUsers}
            detail={t("subscription:usage.usersDetail", {
              active: info.usage.users,
              pending: info.usage.pendingUsers,
            })}
          />
          {recordModules.map((module) => (
            <UsageBar
              key={module}
              testId={`usage-${module}`}
              label={t("subscription:limits.recordsOf", { module: moduleName(module) })}
              used={info.usage.records[module] ?? 0}
              max={info.limits.maxRecords[module]}
            />
          ))}
          <StorageUsageBar info={info} />
        </Stack>
        <Text size="xs" c="dimmed" mt="md" data-testid="as-of">
          {t("subscription:usage.asOf", { date: formatDateTime(info.usage.asOf) })}
        </Text>
      </Card>

      <StorageBreakdownCard />

      <Card withBorder padding="lg">
        <Text fw={600} mb="md">
          {t("subscription:modulesTitle")}
        </Text>
        <Group gap="xs">
          {GATED_MODULES.map((module) => {
            const on = info.modules[module] !== false;
            return (
              <Badge
                key={module}
                variant="light"
                color={on ? "green" : "gray"}
                data-testid={`module-${module}`}
              >
                {moduleName(module)}: {on ? t("subscription:included") : t("subscription:notIncluded")}
              </Badge>
            );
          })}
        </Group>
      </Card>

      <Alert color="blue" variant="light" icon={<Info size={16} />}>
        {t("subscription:upgradeHint")}
      </Alert>
    </Stack>
  );
}

/** "Plan ve kullanım" (`org.settings.manage`): plan, trial end, limits against usage, modules. */
export default function PlanUsagePage() {
  const { t } = useTranslation(["subscription"]);
  const { data, isLoading, error, refetch } = useSubscription();

  return (
    <>
      <PageHeader title={t("subscription:title")} description={t("subscription:description")} />
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Skeleton h={220} maw={760} />
      ) : (
        <PlanUsage info={data} />
      )}
    </>
  );
}
