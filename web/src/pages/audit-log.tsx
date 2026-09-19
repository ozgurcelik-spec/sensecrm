import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Badge, Card, Group, Pagination, Skeleton, Table, Text } from "@mantine/core";
import { AuditChanges } from "@/components/audit/audit-changes";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { useAuditEntries } from "@/hooks/use-organization-queries";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { AuditAction } from "@/types";

const PAGE_SIZE = 50;

const ACTION_COLOR: Record<AuditAction, string> = {
  created: "green",
  updated: "blue",
  deleted: "red",
};

export default function AuditLogPage() {
  const { t } = useTranslation(["audit", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const [page, setPage] = useState(1);
  const { data, isLoading, error, refetch, isFetching } = useAuditEntries(page, PAGE_SIZE);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <>
      <PageHeader title={t("audit:title")} description={t("audit:description")} />
      {error && <LoadError error={error} onRetry={() => void refetch()} />}
      <Card withBorder padding={0} mt={error ? "md" : 0}>
        <Table.ScrollContainer minWidth={760}>
          <Table
            verticalSpacing="sm"
            highlightOnHover
            style={{ opacity: isFetching && !isLoading ? 0.6 : 1 }}
          >
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("audit:occurredAt")}</Table.Th>
                <Table.Th>{t("audit:user")}</Table.Th>
                <Table.Th>{t("audit:action")}</Table.Th>
                <Table.Th>{t("audit:entity")}</Table.Th>
                <Table.Th>{t("audit:changes")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {isLoading &&
                Array.from({ length: 5 }, (_, i) => (
                  <Table.Tr key={i}>
                    <Table.Td colSpan={5}>
                      <Skeleton h={24} />
                    </Table.Td>
                  </Table.Tr>
                ))}
              {data?.items.map((entry) => (
                <Table.Tr key={entry.id}>
                  <Table.Td>
                    <Text size="sm" style={{ whiteSpace: "nowrap" }}>
                      {formatDateTime(entry.occurredAt, timeZone)}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{entry.userDisplayName ?? t("audit:system")}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Badge variant="light" color={ACTION_COLOR[entry.action] ?? "gray"}>
                      {t(`audit:actions.${entry.action}`, { defaultValue: entry.action })}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" fw={500}>
                      {entry.entityType}
                    </Text>
                    <Text size="xs" c="dimmed" ff="monospace">
                      {entry.entityId}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <AuditChanges changes={entry.changes} />
                  </Table.Td>
                </Table.Tr>
              ))}
              {data && data.items.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={5}>
                    <Text size="sm" c="dimmed" ta="center" py="md">
                      {t("common:noData")}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>
      {data && data.total > 0 && (
        <Group justify="space-between" mt="md">
          <Text size="sm" c="dimmed">
            {t("common:total", { count: data.total })}
          </Text>
          <Pagination total={totalPages} value={page} onChange={setPage} size="sm" />
        </Group>
      )}
    </>
  );
}
