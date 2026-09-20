import { useTranslation } from "react-i18next";
import { Alert, Badge, Card, Group, Skeleton, Stack, Table, Text } from "@mantine/core";
import { Info } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { usePlatformPlans } from "@/hooks/use-platform";
import { formatNumber } from "@/lib/format";
import { GATED_MODULES, type PlatformPlan } from "@/types";

function LimitsCell({ plan }: { plan: PlatformPlan }) {
  const { t } = useTranslation(["platform", "subscription", "files"]);
  const value = (limit: number | null | undefined) =>
    limit === null || limit === undefined ? t("platform:plans.unlimited") : formatNumber(limit);
  const records = Object.entries(plan.limits.maxRecords ?? {});
  return (
    <Stack gap={2}>
      <Text size="sm">
        {t("platform:plans.usersLimit")}: {value(plan.limits.maxUsers)}
      </Text>
      {plan.limits.maxStorageMb !== undefined && (
        <Text size="xs" c="dimmed">
          {t("files:plan.storage")}:{" "}
          {plan.limits.maxStorageMb === null ? value(null) : `${value(plan.limits.maxStorageMb)} MB`}
        </Text>
      )}
      {records.map(([module, limit]) => (
        <Text size="xs" c="dimmed" key={module}>
          {t(`subscription:modules.${module}`, { defaultValue: module })}: {value(limit)}
        </Text>
      ))}
    </Stack>
  );
}

/** Read-only plan catalog: plans are managed by configuration, not from the UI. */
export default function PlatformPlansPage() {
  const { t } = useTranslation(["platform", "subscription"]);
  const { data, isLoading, error, refetch } = usePlatformPlans();

  return (
    <>
      <PageHeader title={t("platform:plans.title")} description={t("platform:plans.description")} />
      <Stack gap="md">
        <Alert color="blue" variant="light" icon={<Info size={16} />}>
          {t("platform:plans.configHint")}
        </Alert>
        {error ? (
          <LoadError error={error} onRetry={() => void refetch()} />
        ) : isLoading ? (
          <Skeleton h={160} />
        ) : (
          <Card withBorder padding={0}>
            <Table.ScrollContainer minWidth={860}>
              <Table verticalSpacing="sm" highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t("platform:plans.columns.code")}</Table.Th>
                    <Table.Th>{t("platform:plans.columns.name")}</Table.Th>
                    <Table.Th>{t("platform:plans.columns.trialDays")}</Table.Th>
                    <Table.Th>{t("platform:plans.columns.limits")}</Table.Th>
                    <Table.Th>{t("platform:plans.columns.modules")}</Table.Th>
                    <Table.Th ta="right">{t("platform:plans.columns.assigned")}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {(data ?? []).map((plan) => (
                    <Table.Tr key={plan.code} data-testid="plan-row">
                      <Table.Td>
                        <Text size="sm" ff="monospace">
                          {plan.code}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Group gap="xs" wrap="nowrap">
                          <Text size="sm" fw={500}>
                            {plan.name}
                          </Text>
                          {!plan.isActive && (
                            <Badge size="xs" color="gray" variant="light">
                              {t("platform:plans.inactive")}
                            </Badge>
                          )}
                        </Group>
                        {plan.description && (
                          <Text size="xs" c="dimmed">
                            {plan.description}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>{plan.trialDays ?? "-"}</Table.Td>
                      <Table.Td>
                        <LimitsCell plan={plan} />
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4}>
                          {GATED_MODULES.map((module) => (
                            <Badge
                              key={module}
                              size="sm"
                              variant="light"
                              color={plan.modules[module] ? "green" : "gray"}
                              aria-label={`${t(`subscription:modules.${module}`)}: ${
                                plan.modules[module] ? t("subscription:included") : t("subscription:notIncluded")
                              }`}
                            >
                              {t(`subscription:modules.${module}`)}
                            </Badge>
                          ))}
                        </Group>
                      </Table.Td>
                      <Table.Td ta="right">{formatNumber(plan.assignedCount)}</Table.Td>
                    </Table.Tr>
                  ))}
                  {data?.length === 0 && (
                    <Table.Tr>
                      <Table.Td colSpan={6}>
                        <Text size="sm" c="dimmed" ta="center" py="md">
                          {t("platform:plans.empty")}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  )}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Card>
        )}
      </Stack>
    </>
  );
}
