import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Text } from "@mantine/core";
import { usePermission } from "@/hooks/use-permission";
import { RELATED_PATH, RELATED_READ_PERMISSION, activityDate } from "@/lib/activity";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { Activity } from "@/types";

/** "Account: Acme" as a link to the record (plain text when the user may not read that module). */
export function RelatedRecordLink({ activity }: { activity: Activity }) {
  const { t } = useTranslation(["activities"]);
  const type = activity.relatedType;
  const allowed = usePermission(type ? RELATED_READ_PERMISSION[type] : "");
  if (!type || !activity.relatedId) return <>-</>;
  const typeLabel = t(`activities:relatedTypes.${type}`);
  if (!activity.relatedName) {
    // The related record was deleted; the relation is soft, so the activity stays.
    return (
      <Text size="sm" c="dimmed">
        {typeLabel}: {t("activities:relatedDeleted")}
      </Text>
    );
  }
  return (
    <Text size="sm" component="span">
      {typeLabel}:{" "}
      {allowed ? (
        <Anchor component={Link} to={`/app/${RELATED_PATH[type]}/${activity.relatedId}`} size="sm">
          {activity.relatedName}
        </Anchor>
      ) : (
        activity.relatedName
      )}
    </Text>
  );
}

/** Due date (tasks) or start (calls, meetings) in the organization's time zone; red when overdue. */
export function ActivityDate({ activity }: { activity: Activity }) {
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const value = activityDate(activity);
  if (!value) return <>-</>;
  return (
    <Text
      size="sm"
      component="span"
      c={activity.isOverdue ? "red" : undefined}
      fw={activity.isOverdue ? 600 : undefined}
      data-overdue={activity.isOverdue ? "true" : undefined}
    >
      {formatDateTime(value, timeZone)}
    </Text>
  );
}
