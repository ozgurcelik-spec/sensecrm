import { useTranslation } from "react-i18next";
import { Alert, Badge, Card, Group, SimpleGrid, Stack, Table, Text } from "@mantine/core";
import { TriangleAlert } from "lucide-react";
import { formatDateTime } from "@/lib/dates";
import { formatBytes, mbToBytes } from "@/lib/files";
import { formatCalendarDate, formatNumber, orDash } from "@/lib/format";
import { toOverridesDraft } from "@/lib/platform";
import {
  GATED_MODULES,
  RECORD_MODULES,
  type PlatformOrganizationDetail,
  type PlatformOverLimit,
} from "@/types";

/** "Kullanıcı 6 / 5" style line for an over-limit entry. */
export function OverLimitAlert({
  overLimit,
  onDismiss,
}: {
  overLimit: PlatformOverLimit[];
  onDismiss: () => void;
}) {
  const { t } = useTranslation(["platform", "subscription", "files"]);
  if (overLimit.length === 0) return null;
  return (
    <Alert
      color="orange"
      variant="light"
      icon={<TriangleAlert size={16} />}
      title={t("platform:subscription.overLimitTitle")}
      withCloseButton
      onClose={onDismiss}
      closeButtonLabel={t("platform:subscription.dismiss")}
      data-testid="over-limit"
    >
      <Stack gap={4}>
        <Text size="sm">{t("platform:subscription.overLimitHint")}</Text>
        {overLimit.map((entry) => (
          <Text size="sm" key={`${entry.limit}-${entry.module ?? ""}`}>
            {entry.limit === "users"
              ? t("subscription:limits.users")
              : entry.limit === "storage"
                ? t("files:plan.storage")
                : t("subscription:limits.recordsOf", {
                    module: t(`subscription:modules.${entry.module}`, {
                      defaultValue: entry.module ?? "",
                    }),
                  })}
            {entry.limit === "storage"
              ? `: ${formatBytes(entry.used)} / ${formatBytes(entry.max)}`
              : `: ${formatNumber(entry.used)} / ${formatNumber(entry.max)}`}
          </Text>
        ))}
      </Stack>
    </Alert>
  );
}

interface OrganizationSummaryProps {
  org: PlatformOrganizationDetail;
  overLimit: PlatformOverLimit[];
  onDismissOverLimit: () => void;
}

/** "Özet" tab: plan card (effective limits and modules, overrides highlighted), suspension and latest usage. */
export function OrganizationSummary({
  org,
  overLimit,
  onDismissOverLimit,
}: OrganizationSummaryProps) {
  const { t } = useTranslation(["platform", "subscription", "files"]);
  const overrides = toOverridesDraft(org.overrides);
  const exception = (
    <Badge size="xs" color="violet" variant="light">
      {t("platform:summary.override")}
    </Badge>
  );
  const unlimited = t("platform:summary.unlimited");

  return (
    <Stack gap="md">
      <OverLimitAlert overLimit={overLimit} onDismiss={onDismissOverLimit} />
      {org.suspension && (
        <Alert color="red" variant="light" title={t("platform:summary.suspendedTitle")}>
          <Text size="sm">
            {t(`platform:suspend.modes.${org.suspension.mode}`, { defaultValue: org.suspension.mode })}
            {org.suspension.at ? ` - ${formatDateTime(org.suspension.at)}` : ""}
          </Text>
          <Text size="sm">{orDash(org.suspension.reason)}</Text>
        </Alert>
      )}

      <Card withBorder padding="md" data-testid="plan-card">
        <Group justify="space-between" mb="sm" wrap="nowrap">
          <Stack gap={0}>
            <Text fw={600}>{org.planName}</Text>
            <Text size="xs" c="dimmed">
              {org.planCode}
              {org.planChangedAt
                ? ` - ${t("platform:summary.planChanged", { date: formatDateTime(org.planChangedAt) })}`
                : ""}
            </Text>
          </Stack>
        </Group>
        <Text size="sm" mb="sm">
          {t("platform:summary.trialEnds")}: {formatCalendarDate(org.trialEndsOn)}
        </Text>

        <Table variant="vertical" withTableBorder={false} verticalSpacing="xs" layout="fixed">
          <Table.Tbody>
            <Table.Tr>
              <Table.Th w={220}>{t("platform:summary.maxUsers")}</Table.Th>
              <Table.Td data-testid="limit-users">
                <Group gap="xs">
                  <Text size="sm">
                    {org.limits.maxUsers === undefined ? unlimited : formatNumber(org.limits.maxUsers)}
                  </Text>
                  {overrides.maxUsers.mode !== "plan" && exception}
                </Group>
              </Table.Td>
            </Table.Tr>
            <Table.Tr>
              <Table.Th>{t("files:platform.storageLimit")}</Table.Th>
              <Table.Td data-testid="limit-storage">
                <Group gap="xs">
                  <Text size="sm">
                    {org.limits.maxStorageMb === undefined
                      ? unlimited
                      : formatBytes(mbToBytes(org.limits.maxStorageMb))}
                  </Text>
                  {overrides.maxStorageMb.mode !== "plan" && exception}
                </Group>
              </Table.Td>
            </Table.Tr>
            {RECORD_MODULES.map((module) => {
              const limit = org.limits.maxRecords[module];
              return (
                <Table.Tr key={module}>
                  <Table.Th>
                    {t("subscription:limits.recordsOf", {
                      module: t(`subscription:modules.${module}`),
                    })}
                  </Table.Th>
                  <Table.Td data-testid={`limit-${module}`}>
                    <Group gap="xs">
                      <Text size="sm">{limit === undefined ? unlimited : formatNumber(limit)}</Text>
                      {(overrides.maxRecords[module]?.mode ?? "plan") !== "plan" && exception}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>

        <Group gap="xs" mt="md">
          {GATED_MODULES.map((module) => {
            const on = org.limits.modules[module] === true;
            return (
              <Group gap={4} key={module} wrap="nowrap">
                <Badge variant="light" color={on ? "green" : "gray"} data-testid={`module-${module}`}>
                  {t(`subscription:modules.${module}`)}: {on ? t("subscription:included") : t("subscription:notIncluded")}
                </Badge>
                {overrides.modules[module] !== "plan" && exception}
              </Group>
            );
          })}
        </Group>
      </Card>

      <Card withBorder padding="md">
        <Text fw={600} mb="sm">
          {t("platform:summary.latestUsage")}
        </Text>
        {org.usage ? (
          <>
            <Text size="xs" c="dimmed" mb="sm">
              {t("platform:summary.snapshotDay", { day: formatCalendarDate(org.usage.day) })}
            </Text>
            <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
              <div>
                <Text size="xs" c="dimmed">
                  {t("platform:usage.activeUsers")}
                </Text>
                <Text fw={600}>{formatNumber(org.usage.usersActive)}</Text>
              </div>
              <div>
                <Text size="xs" c="dimmed">
                  {t("platform:usage.pendingUsers")}
                </Text>
                <Text fw={600}>{formatNumber(org.usage.usersPending)}</Text>
              </div>
              {Object.entries(org.usage.records).map(([module, count]) => (
                <div key={module}>
                  <Text size="xs" c="dimmed">
                    {t("subscription:limits.recordsOf", {
                      module: t(`subscription:modules.${module}`, { defaultValue: module }),
                    })}
                  </Text>
                  <Text fw={600}>{formatNumber(count)}</Text>
                </div>
              ))}
            </SimpleGrid>
          </>
        ) : (
          <Text size="sm" c="dimmed">
            {t("platform:summary.noUsage")}
          </Text>
        )}
      </Card>
    </Stack>
  );
}
