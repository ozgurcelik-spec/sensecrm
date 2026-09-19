import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Card, Group, Skeleton, Stack, Table, Text } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { useRecordCases, type CaseRecordRef } from "@/hooks/use-cases";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import { CasePriorityBadge, CaseStatusBadge, SlaBadge } from "./case-badges";

/** "Cases" tab of an account / contact detail page (needs `crm.cases.read`). */
export function RecordCasesTab({ record }: { record: CaseRecordRef }) {
  const { t } = useTranslation(["service"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { data, isLoading, error, refetch } = useRecordCases(record);
  const param = record.type === "account" ? "accountId" : "contactId";

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading) {
    return (
      <Stack gap="sm">
        <Skeleton h={40} />
        <Skeleton h={40} />
      </Stack>
    );
  }
  if (!data || data.items.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="lg">
        {t("service:tab.empty")}
      </Text>
    );
  }

  return (
    <Stack gap="sm">
      <Card withBorder padding={0}>
        <Table.ScrollContainer minWidth={640}>
          <Table verticalSpacing="xs" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("service:fields.number")}</Table.Th>
                <Table.Th>{t("service:fields.subject")}</Table.Th>
                <Table.Th>{t("service:fields.status")}</Table.Th>
                <Table.Th>{t("service:fields.priority")}</Table.Th>
                <Table.Th>{t("service:fields.sla")}</Table.Th>
                <Table.Th>{t("service:fields.createdAt")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.items.map((c) => (
                <Table.Tr key={c.id} data-testid="case-row">
                  <Table.Td>
                    <Anchor component={Link} to={`/app/cases/${c.id}`} size="sm" fw={600}>
                      {c.number}
                    </Anchor>
                  </Table.Td>
                  <Table.Td>{c.subject}</Table.Td>
                  <Table.Td>
                    <CaseStatusBadge status={c.status} />
                  </Table.Td>
                  <Table.Td>
                    <CasePriorityBadge priority={c.priority} />
                  </Table.Td>
                  <Table.Td>
                    <SlaBadge item={c} />
                  </Table.Td>
                  <Table.Td>{formatDateTime(c.createdAt, timeZone)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>
      <Group justify="space-between">
        <Text size="sm" c="dimmed">
          {t("service:tab.total", { count: data.totalCount })}
        </Text>
        <Anchor component={Link} to={`/app/cases?${param}=${encodeURIComponent(record.id)}`} size="sm">
          {t("service:tab.viewAll")}
        </Anchor>
      </Group>
    </Stack>
  );
}
