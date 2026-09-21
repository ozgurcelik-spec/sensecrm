import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Badge, Card, Code, Group, Pagination, Skeleton, Stack, Table, Text } from "@mantine/core";
import { ApiKeyAuditBadge } from "@/components/integrations/badges";
import { LoadError } from "@/components/load-error";
import { useRecordAudit } from "@/hooks/use-record-audit";
import { displayValue, parseChanges } from "@/lib/audit";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { AuditAction, AuditEntityType } from "@/types";

const PAGE_SIZE = 20;

const ACTION_COLOR: Record<AuditAction, string> = {
  created: "green",
  updated: "blue",
  deleted: "red",
};

interface RecordAuditTabProps {
  entityType: AuditEntityType;
  entityId: string;
}

/** "Audit" tab of a detail page: who changed which field of this record, and when. */
export function RecordAuditTab({ entityType, entityId }: RecordAuditTabProps) {
  const { t } = useTranslation(["crm", "audit", "common", "campaigns", "commerce"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const [page, setPage] = useState(1);
  const { data, isLoading, error, refetch } = useRecordAudit(entityType, entityId, page, PAGE_SIZE);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) {
    return (
      <Stack gap="sm">
        <Skeleton h={64} />
        <Skeleton h={64} />
      </Stack>
    );
  }
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("crm:audit.empty")}
      </Text>
    );
  }

  return (
    <Stack gap="sm">
      {data.items.map((entry) => {
        const rows = parseChanges(entry.changes);
        return (
          <Card key={entry.id} withBorder padding="sm" data-testid="audit-entry">
            <Group justify="space-between" mb={rows.length ? "xs" : 0} wrap="wrap">
              <Group gap="xs">
                <Badge variant="light" color={ACTION_COLOR[entry.action] ?? "gray"}>
                  {t(`audit:actions.${entry.action}`, { defaultValue: entry.action })}
                </Badge>
                <Text size="sm" fw={500}>
                  {entry.userDisplayName ?? t("audit:system")}
                </Text>
                <ApiKeyAuditBadge apiKeyId={entry.apiKeyId} />
              </Group>
              <Text size="xs" c="dimmed">
                {formatDateTime(entry.occurredAt, timeZone)}
              </Text>
            </Group>
            {rows.length > 0 && (
              <Table fz="sm" verticalSpacing={4} withRowBorders={false}>
                <Table.Tbody>
                  {rows.map((row) => (
                    <Table.Tr key={row.field}>
                      <Table.Td w={180} fw={600}>
                        {t(`crm:auditFields.${row.field}`, {
                          defaultValue: t(`campaigns:auditFields.${row.field}`, {
                            defaultValue: t(`commerce:auditFields.${row.field}`, {
                              defaultValue: row.field,
                            }),
                          }),
                        })}
                      </Table.Td>
                      <Table.Td style={{ wordBreak: "break-word" }}>
                        {row.before !== undefined && (
                          <>
                            <Code color="red.1">{displayValue(row.before)}</Code> {"→"}{" "}
                          </>
                        )}
                        <Code color="green.1">{displayValue(row.after)}</Code>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Card>
        );
      })}
      {data.total > PAGE_SIZE && (
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
            {t("common:total", { count: data.total })}
          </Text>
          <Pagination total={totalPages} value={page} onChange={setPage} size="sm" />
        </Group>
      )}
    </Stack>
  );
}
