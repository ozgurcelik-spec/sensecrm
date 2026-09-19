import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Button, Card, Group, Select, Skeleton, Stack, Text, TextInput } from "@mantine/core";
import { LoadError } from "@/components/load-error";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { ActivityPriorityBadge, ActivityTypeBadge } from "@/components/crm/badges";
import { RowActions } from "@/components/crm/list-page-frame";
import { useActivities, useDeleteActivity, useSaveActivity } from "@/hooks/use-activities";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { usesDue } from "@/lib/activity";
import { fromZonedInput } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import {
  ACTIVITY_TYPES,
  type Activity,
  type ActivityInput,
  type ActivityType,
  type RelatedRecordRef,
} from "@/types";
import { ActivityDate } from "./activity-cells";
import { ActivityFormDialog } from "./activity-form-dialog";
import { ActivityStatusButton } from "./activity-status-control";

const TAB_PAGE_SIZE = 50;

type QuickField = "subject" | "dueAt";
const QUICK_FIELDS = ["subject", "dueAt"] as const satisfies readonly QuickField[];

/** Compact create form of the tab: type + subject (+ due for a task). The record is fixed. */
function QuickAdd({ related }: { related: RelatedRecordRef }) {
  const { t } = useTranslation(["activities", "common", "auth"]);
  const save = useSaveActivity();
  const ownerId = useDefaultOwnerId();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const [type, setType] = useState<ActivityType>("task");
  const [subject, setSubject] = useState("");
  const [dueAt, setDueAt] = useState("");
  const [errors, setErrors] = useState<Partial<Record<QuickField, string>>>({});
  const [detailed, setDetailed] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!subject.trim()) {
      setErrors({ subject: t("auth:validation.required") });
      return;
    }
    setErrors({});
    const input: ActivityInput = {
      type,
      subject: subject.trim(),
      relatedType: related.type,
      relatedId: related.id,
      assignedUserId: ownerId || undefined,
    };
    if (usesDue(type) && dueAt) input.dueAt = fromZonedInput(dueAt, timeZone);
    try {
      await save.mutateAsync(input);
      setSubject("");
      setDueAt("");
      toast({ variant: "success", description: t("activities:created") });
    } catch (error) {
      // Server field errors show under the inputs; anything else is a toast.
      const collected: Partial<Record<QuickField, string>> = {};
      const matched = applyValidationErrors<Record<QuickField, string>>(
        error,
        (field, { message }) => {
          if (field === "subject" || field === "dueAt") collected[field] = message;
        },
        QUICK_FIELDS
      );
      if (matched) setErrors(collected);
      else toastApiError(error);
    }
  }

  return (
    <>
      <form onSubmit={(event) => void submit(event)} noValidate aria-label={t("activities:new")}>
        <Group gap="sm" align="flex-start" wrap="wrap">
          <Select
            aria-label={t("activities:fields.type")}
            w={140}
            data={ACTIVITY_TYPES.map((v) => ({ value: v, label: t(`activities:types.${v}`) }))}
            value={type}
            onChange={(value) => setType((value as ActivityType | null) ?? "task")}
            allowDeselect={false}
          />
          <TextInput
            aria-label={t("activities:fields.subject")}
            placeholder={t("activities:tab.quickAdd.subject")}
            style={{ flex: 1, minWidth: 200 }}
            value={subject}
            onChange={(event) => setSubject(event.currentTarget.value)}
            error={errors.subject}
          />
          {usesDue(type) && (
            <TextInput
              type="datetime-local"
              aria-label={t("activities:fields.dueAt")}
              value={dueAt}
              onChange={(event) => setDueAt(event.currentTarget.value)}
              error={errors.dueAt}
            />
          )}
          <Button type="submit" loading={save.isPending}>
            {t("activities:tab.quickAdd.add")}
          </Button>
          <Button variant="default" onClick={() => setDetailed(true)}>
            {t("activities:tab.detailed")}
          </Button>
        </Group>
      </form>
      {detailed && (
        <ActivityFormDialog
          defaultType={type}
          defaultRelated={related}
          lockRelated
          onClose={() => setDetailed(false)}
        />
      )}
    </>
  );
}

/**
 * "Activities" tab of a record detail page: the activities linked to the record plus a quick-add
 * form whose related record is preset and locked. Needs `crm.activities.read`; the form and row
 * actions need `crm.activities.write`.
 */
export function RecordActivitiesTab({ related }: { related: RelatedRecordRef }) {
  const { t } = useTranslation(["activities", "common"]);
  const { canWriteActivities } = useCrmPermissions();
  const { data, isLoading, error, refetch } = useActivities({
    relatedType: related.type,
    relatedId: related.id,
    page: 1,
    pageSize: TAB_PAGE_SIZE,
  });
  const remove = useDeleteActivity();
  const [editing, setEditing] = useState<Activity | null>(null);
  const [deleting, setDeleting] = useState<Activity | null>(null);

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("activities:deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <Stack gap="md">
      {canWriteActivities && <QuickAdd related={related} />}

      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading ? (
        <Stack gap="sm">
          <Skeleton h={56} />
          <Skeleton h={56} />
        </Stack>
      ) : !data || data.items.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="lg">
          {t("activities:tab.empty")}
        </Text>
      ) : (
        <Stack gap="xs">
          {data.items.map((activity) => (
            <Card key={activity.id} withBorder padding="sm" data-testid="activity-row">
              <Group justify="space-between" wrap="nowrap" align="center">
                <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
                  {canWriteActivities && <ActivityStatusButton activity={activity} />}
                  <Stack gap={2} style={{ minWidth: 0 }}>
                    <Text
                      size="sm"
                      fw={500}
                      td={
                        activity.status === "completed" && activity.type !== "note"
                          ? "line-through"
                          : undefined
                      }
                    >
                      {activity.subject}
                    </Text>
                    <Group gap="xs">
                      <ActivityTypeBadge type={activity.type} />
                      {activity.type !== "note" && (
                        <ActivityPriorityBadge priority={activity.priority} />
                      )}
                      <ActivityDate activity={activity} />
                      {activity.assignedUserName && (
                        <Text size="xs" c="dimmed">
                          {activity.assignedUserName}
                        </Text>
                      )}
                    </Group>
                  </Stack>
                </Group>
                <RowActions
                  editLabel={t("common:edit")}
                  deleteLabel={t("common:delete")}
                  onEdit={canWriteActivities ? () => setEditing(activity) : undefined}
                  onDelete={canWriteActivities ? () => setDeleting(activity) : undefined}
                />
              </Group>
            </Card>
          ))}
        </Stack>
      )}

      {editing && (
        <ActivityFormDialog activity={editing} lockRelated onClose={() => setEditing(null)} />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("activities:deleteTitle")}
        message={t("activities:deleteMessage", { name: deleting?.subject })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </Stack>
  );
}
