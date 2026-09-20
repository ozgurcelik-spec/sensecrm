import { useTranslation } from "react-i18next";
import { Card, Group, Skeleton, Table, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { UsageBar } from "@/components/subscription/usage-bars";
import { useFilesUsage } from "@/hooks/use-files";
import { usePermission } from "@/hooks/use-permission";
import { formatNumber } from "@/lib/format";
import { formatBytes, mbToBytes } from "@/lib/files";
import { PERMISSIONS, type SubscriptionInfo } from "@/types";

/**
 * "Depolama" bar of the plan page: bytes used (`usage.storageBytes`, the cached view of
 * `GET /subscription`) against `limits.maxStorageMb`; orange from 80 %, red at 100 %, only the used
 * amount without a limit. Renders nothing on a server that does not report storage.
 */
export function StorageUsageBar({ info }: { info: SubscriptionInfo }) {
  const { t } = useTranslation(["files"]);
  const used = info.usage.storageBytes;
  if (used === undefined) return null;
  const maxMb = info.limits.maxStorageMb;
  const details: string[] = [];
  if (info.usage.fileCount !== undefined) {
    details.push(t("files:plan.storageDetail", { count: info.usage.fileCount }));
  }
  return (
    <UsageBar
      testId="usage-storage"
      label={t("files:plan.storage")}
      used={used}
      max={maxMb === undefined ? undefined : mbToBytes(maxMb)}
      format={formatBytes}
      detail={details.length > 0 ? details.join(" · ") : undefined}
    />
  );
}

/** Live storage usage per record type (`GET /files/usage`, `org.settings.manage`), with the quarantined / missing counts. */
export function StorageBreakdownCard() {
  const { t } = useTranslation(["files"]);
  const canManage = usePermission(PERMISSIONS.orgSettingsManage);
  const { data, isLoading, error, refetch } = useFilesUsage(canManage);
  if (!canManage) return null;

  return (
    <Card withBorder padding="lg" data-testid="storage-breakdown">
      <Text fw={600} mb="md">
        {t("files:plan.byType")}
      </Text>
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Skeleton h={80} />
      ) : data.byRecordType.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t("files:plan.byTypeEmpty")}
        </Text>
      ) : (
        <>
          <Table verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("files:plan.columns.type")}</Table.Th>
                <Table.Th ta="right">{t("files:plan.columns.files")}</Table.Th>
                <Table.Th ta="right">{t("files:plan.columns.size")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.byRecordType.map((row) => (
                <Table.Tr key={row.recordType} data-testid={`storage-row-${row.recordType}`}>
                  <Table.Td>
                    {t(`files:recordTypes.${row.recordType}`, { defaultValue: row.recordType })}
                  </Table.Td>
                  <Table.Td ta="right">{formatNumber(row.fileCount)}</Table.Td>
                  <Table.Td ta="right">{formatBytes(row.sizeBytes)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
            <Table.Tfoot>
              <Table.Tr>
                <Table.Th>{t("files:plan.total")}</Table.Th>
                <Table.Th ta="right">{formatNumber(data.fileCount)}</Table.Th>
                <Table.Th ta="right">{formatBytes(data.usedBytes)}</Table.Th>
              </Table.Tr>
            </Table.Tfoot>
          </Table>
          {(data.quarantinedCount > 0 || data.missingCount > 0) && (
            <Group gap="md" mt="sm">
              {data.quarantinedCount > 0 && (
                <Text size="xs" c="red">
                  {t("files:plan.storageQuarantined", { count: data.quarantinedCount })}
                </Text>
              )}
              {data.missingCount > 0 && (
                <Text size="xs" c="dimmed">
                  {t("files:plan.storageMissing", { count: data.missingCount })}
                </Text>
              )}
            </Group>
          )}
        </>
      )}
    </Card>
  );
}
