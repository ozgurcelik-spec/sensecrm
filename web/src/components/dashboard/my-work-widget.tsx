import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Group, SimpleGrid, Stack, Text } from "@mantine/core";
import { ActivityDate } from "@/components/activities/activity-cells";
import { ActivityCheckbox } from "@/components/activities/activity-status-control";
import { useActivities, useActivitySummary } from "@/hooks/use-activities";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { formatNumber } from "@/lib/format";
import { todayBoundsIso } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import { DashboardWidget } from "./dashboard-widget";

const TASK_LIMIT = 5;

/**
 * "My work": the caller's activity counts and the first five open tasks that are due today or
 * overdue, each with a complete checkbox (when the user may write activities).
 */
export function MyWorkWidget() {
  const { t } = useTranslation(["home", "activities"]);
  const me = useAuthStore((state) => state.me);
  const timeZone = me?.organization.timeZone;
  const { canWriteActivities } = useCrmPermissions();
  const userId = me?.user.id;

  const summary = useActivitySummary();
  // Everything due up to the end of today, oldest first: overdue tasks lead, then today's.
  const tasks = useActivities(
    {
      type: "task",
      status: "open",
      assignedUserId: userId,
      dueTo: todayBoundsIso(timeZone).to,
      sort: "dueAt",
      page: 1,
      pageSize: TASK_LIMIT,
    },
    !!userId
  );

  const error = summary.error ?? tasks.error;
  const counts = [
    { key: "open", value: summary.data?.openCount },
    { key: "overdue", value: summary.data?.overdueCount, alert: true },
    { key: "dueToday", value: summary.data?.dueTodayCount },
    { key: "completedThisWeek", value: summary.data?.completedThisWeek },
  ] as const;

  return (
    <DashboardWidget
      testId="widget-my-work"
      title={t("home:myWork.title")}
      action={
        <Anchor component={Link} to="/app/activities" size="sm">
          {t("home:myWork.viewAll")}
        </Anchor>
      }
      isLoading={summary.isLoading || tasks.isLoading}
      error={error}
      onRetry={() => {
        void summary.refetch();
        void tasks.refetch();
      }}
      skeletonHeight={200}
    >
      <Stack gap="md">
        <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
          {counts.map((c) => (
            <div key={c.key}>
              <Text
                fz={24}
                fw={700}
                lh={1.1}
                c={"alert" in c && c.alert && (c.value ?? 0) > 0 ? "red" : undefined}
                data-testid={`my-work-${c.key}`}
              >
                {formatNumber(c.value ?? 0)}
              </Text>
              <Text size="xs" c="dimmed">
                {t(`home:myWork.${c.key}`)}
              </Text>
            </div>
          ))}
        </SimpleGrid>

        <Text size="sm" fw={500}>
          {t("home:myWork.upcoming")}
        </Text>
        {(tasks.data?.items ?? []).length === 0 ? (
          <Text size="sm" c="dimmed">
            {t("home:myWork.empty")}
          </Text>
        ) : (
          <Stack gap="xs">
            {(tasks.data?.items ?? []).map((task) => (
              <Group key={task.id} gap="sm" wrap="nowrap" data-testid="my-work-task">
                <ActivityCheckbox
                  activity={task}
                  disabled={!canWriteActivities}
                  label={t("home:myWork.complete", { subject: task.subject })}
                />
                <Text
                  size="sm"
                  style={{ flex: 1, minWidth: 0 }}
                  truncate
                  td={task.status === "completed" ? "line-through" : undefined}
                >
                  {task.subject}
                </Text>
                <ActivityDate activity={task} />
              </Group>
            ))}
          </Stack>
        )}
      </Stack>
    </DashboardWidget>
  );
}
