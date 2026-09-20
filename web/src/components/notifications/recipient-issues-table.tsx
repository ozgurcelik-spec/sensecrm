import { useTranslation } from "react-i18next";
import { Alert, Button, Card, Skeleton, Stack, Table, Text } from "@mantine/core";
import { Info } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { useCanChangeNotificationSettings } from "@/hooks/use-notification-access";
import { useRecipientIssues, useResetRecipientIssue } from "@/hooks/use-notifications";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { NotificationRecipientIssue } from "@/types";

/** Recipients whose address the server stopped trying (repeated permanent rejections). An admin can reset one. */
export function RecipientIssuesTable() {
  const { t } = useTranslation(["notifications"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const canReset = useCanChangeNotificationSettings();
  const { data, isLoading, error, refetch } = useRecipientIssues();
  const reset = useResetRecipientIssue();

  function onReset(issue: NotificationRecipientIssue) {
    reset.mutate(
      { userId: issue.userId, channel: issue.channel },
      {
        onSuccess: () => toast({ variant: "success", description: t("notifications:issues.resetDone") }),
        onError: toastApiError,
      }
    );
  }

  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading || !data) return <Skeleton h={160} />;

  return (
    <Stack gap="md">
      <Alert color="blue" variant="light" icon={<Info size={16} />}>
        {t("notifications:issues.hint")}
      </Alert>
      <Card withBorder padding={0}>
        <Table.ScrollContainer minWidth={640}>
          <Table verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("notifications:issues.columns.user")}</Table.Th>
                <Table.Th>{t("notifications:issues.columns.channel")}</Table.Th>
                <Table.Th>{t("notifications:issues.columns.failures")}</Table.Th>
                <Table.Th>{t("notifications:issues.columns.invalidSince")}</Table.Th>
                <Table.Th>{t("notifications:issues.columns.lastFailure")}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.map((issue) => (
                <Table.Tr key={`${issue.userId}-${issue.channel}`} data-testid="issue-row">
                  <Table.Td>{issue.userName ?? issue.userId}</Table.Td>
                  <Table.Td>{t(`notifications:channels.${issue.channel}`)}</Table.Td>
                  <Table.Td>{issue.hardFailures}</Table.Td>
                  <Table.Td>{issue.invalidSince ? formatDateTime(issue.invalidSince, timeZone) : "-"}</Table.Td>
                  <Table.Td>{issue.lastFailureAt ? formatDateTime(issue.lastFailureAt, timeZone) : "-"}</Table.Td>
                  <Table.Td ta="right">
                    {canReset && (
                      <Button
                        size="xs"
                        variant="light"
                        loading={reset.isPending && reset.variables?.userId === issue.userId}
                        aria-label={t("notifications:issues.resetNamed", { name: issue.userName ?? issue.userId })}
                        onClick={() => onReset(issue)}
                      >
                        {t("notifications:issues.reset")}
                      </Button>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
              {data.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={6}>
                    <Text size="sm" c="dimmed" ta="center" py="md">
                      {t("notifications:issues.empty")}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>
    </Stack>
  );
}
