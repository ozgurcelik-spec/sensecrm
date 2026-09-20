import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Select, SimpleGrid, TextInput, Textarea } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { AttachmentsTab } from "@/components/files/attachments-tab";
import { OwnerSelect } from "@/components/crm/owner-select";
import { useSaveActivity } from "@/hooks/use-activities";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { toast, toastApiError } from "@/hooks/use-toast";
import { buildActivityInput, usesDue, usesRange, type ActivityFormValues } from "@/lib/activity";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import { toZonedInput } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import {
  ACTIVITY_PRIORITIES,
  ACTIVITY_STATUSES,
  ACTIVITY_TYPES,
  type Activity,
  type ActivityType,
  type RelatedRecordRef,
} from "@/types";
import { RelatedRecordPicker } from "./related-record-picker";

const schema = z
  .object({
    type: z.enum(ACTIVITY_TYPES),
    subject: z.string().trim().min(1, "auth:validation.required"),
    description: z.string(),
    status: z.enum(ACTIVITY_STATUSES),
    priority: z.enum(ACTIVITY_PRIORITIES),
    dueAt: z.string(),
    startAt: z.string(),
    endAt: z.string(),
    relatedType: z.string(),
    relatedId: z.string(),
    assignedUserId: z.string(),
  })
  .superRefine((values, ctx) => {
    if (values.relatedType && !values.relatedId) {
      ctx.addIssue({
        code: "custom",
        path: ["relatedId"],
        message: "activities:validation.relatedRequired",
      });
    }
    // `datetime-local` values share one fixed-width format, so string comparison is chronological.
    if (usesRange(values.type) && values.startAt && values.endAt && values.endAt < values.startAt) {
      ctx.addIssue({
        code: "custom",
        path: ["endAt"],
        message: "activities:validation.invalidRange",
      });
    }
  });

const FIELDS = [
  "type",
  "subject",
  "description",
  "status",
  "priority",
  "dueAt",
  "startAt",
  "endAt",
  "relatedType",
  "relatedId",
  "assignedUserId",
] as const;

interface ActivityFormDialogProps {
  activity?: Activity;
  /** Type preselected for a new activity. */
  defaultType?: ActivityType;
  /** Related record preselected for a new activity (from a detail page). */
  defaultRelated?: RelatedRecordRef;
  /** The related record cannot be changed (created from that record's own page). */
  lockRelated?: boolean;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

/**
 * Create / edit dialog. The fields adapt to the type: task has a due date, call and meeting a
 * start/end (end not before start), a note only a subject and description (no priority, no status).
 * Server validation errors and the activity error codes are mapped onto the fields.
 */
export function ActivityFormDialog({
  activity,
  defaultType = "task",
  defaultRelated,
  lockRelated = false,
  onClose,
  onSaved,
}: ActivityFormDialogProps) {
  const { t } = useTranslation(["activities", "common", "auth", "files"]);
  const save = useSaveActivity();
  const defaultOwnerId = useDefaultOwnerId();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const related = activity?.relatedType
    ? { type: activity.relatedType, id: activity.relatedId ?? "", name: activity.relatedName }
    : defaultRelated;
  // Display name of the selected related record (only the id is a form value).
  const [relatedName, setRelatedName] = useState<string | undefined>(related?.name);

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<ActivityFormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      type: activity?.type ?? defaultType,
      subject: activity?.subject ?? "",
      description: activity?.description ?? "",
      status: activity?.status ?? "open",
      priority: activity?.priority ?? "normal",
      dueAt: toZonedInput(activity?.dueAt, timeZone),
      startAt: toZonedInput(activity?.startAt, timeZone),
      endAt: toZonedInput(activity?.endAt, timeZone),
      relatedType: related?.type ?? "",
      relatedId: related?.id ?? "",
      assignedUserId: activity?.assignedUserId ?? defaultOwnerId,
    },
  });

  const type = useWatch({ control, name: "type" });
  const isEdit = !!activity;
  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: activity?.id,
        ...buildActivityInput(values, timeZone, isEdit),
      });
      toast({
        variant: "success",
        description: isEdit ? t("activities:updated") : t("activities:created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      const matched = applyValidationErrors(error, setError, FIELDS);
      const code = getApiProblem(error)?.code;
      const codeField =
        code === "activity.invalid_range"
          ? "endAt"
          : code === "activity.related_not_found"
            ? "relatedId"
            : code === "owner.not_member"
              ? "assignedUserId"
              : undefined;
      if (codeField) {
        setError(codeField, { type: "server", message: t(`common:errors.${code}`) });
      } else if (!matched) {
        toastApiError(error);
      }
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={isEdit ? t("activities:editTitle") : t("activities:createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={isEdit ? t("common:save") : t("common:create")}
    >
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <Controller
          control={control}
          name="type"
          render={({ field }) => (
            <Select
              label={t("activities:fields.type")}
              data={ACTIVITY_TYPES.map((v) => ({ value: v, label: t(`activities:types.${v}`) }))}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "task")}
              allowDeselect={false}
              // The type of an existing activity is fixed: dates and status semantics differ per type.
              disabled={isEdit}
              error={message(errors.type?.message)}
            />
          )}
        />
        {type !== "note" && (
          <Controller
            control={control}
            name="priority"
            render={({ field }) => (
              <Select
                label={t("activities:fields.priority")}
                data={ACTIVITY_PRIORITIES.map((v) => ({
                  value: v,
                  label: t(`activities:priorities.${v}`),
                }))}
                value={field.value}
                onChange={(value) => field.onChange(value ?? "normal")}
                allowDeselect={false}
                error={message(errors.priority?.message)}
              />
            )}
          />
        )}
      </SimpleGrid>

      <TextInput
        label={t("activities:fields.subject")}
        withAsterisk
        data-autofocus
        error={message(errors.subject?.message)}
        {...register("subject")}
      />

      {usesDue(type) && (
        <TextInput
          type="datetime-local"
          label={t("activities:fields.dueAt")}
          error={message(errors.dueAt?.message)}
          {...register("dueAt")}
        />
      )}
      {usesRange(type) && (
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
          <TextInput
            type="datetime-local"
            label={t("activities:fields.startAt")}
            error={message(errors.startAt?.message)}
            {...register("startAt")}
          />
          <TextInput
            type="datetime-local"
            label={t("activities:fields.endAt")}
            error={message(errors.endAt?.message)}
            {...register("endAt")}
          />
        </SimpleGrid>
      )}

      <Textarea
        label={t("activities:fields.description")}
        autosize
        minRows={2}
        maxRows={6}
        error={message(errors.description?.message)}
        {...register("description")}
      />

      {isEdit && type !== "note" && (
        <Controller
          control={control}
          name="status"
          render={({ field }) => (
            <Select
              label={t("activities:fields.status")}
              data={ACTIVITY_STATUSES.map((v) => ({
                value: v,
                label: t(`activities:statuses.${v}`),
              }))}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "open")}
              allowDeselect={false}
              error={message(errors.status?.message)}
            />
          )}
        />
      )}

      <Controller
        control={control}
        name="relatedType"
        render={({ field: typeField }) => (
          <Controller
            control={control}
            name="relatedId"
            render={({ field: idField }) => (
              <RelatedRecordPicker
                locked={lockRelated}
                value={{ type: typeField.value, id: idField.value, name: relatedName }}
                onChange={(next) => {
                  typeField.onChange(next.type);
                  idField.onChange(next.id);
                  setRelatedName(next.name);
                }}
                recordError={message(errors.relatedId?.message ?? errors.relatedType?.message)}
              />
            )}
          />
        )}
      />

      <Controller
        control={control}
        name="assignedUserId"
        render={({ field }) => (
          <OwnerSelect
            label={t("activities:fields.assignee")}
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            currentOwnerId={activity?.assignedUserId}
            currentOwnerName={activity?.assignedUserName}
            error={message(errors.assignedUserId?.message)}
          />
        )}
      />

      {/* Attachments need a saved activity (their record id): edit mode only. */}
      {isEdit && activity && (
        <AttachmentsTab
          recordType="activity"
          recordId={activity.id}
          compact
          heading={t("files:tab.title")}
        />
      )}
    </FormDialog>
  );
}
