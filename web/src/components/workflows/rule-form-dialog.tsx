import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { MultiSelect, NumberInput, Select, Text, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { useRoles } from "@/hooks/use-role-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useSaveWorkflowRule } from "@/hooks/use-workflows";
import { getApiProblem } from "@/lib/api-error";
import {
  MAX_FOLLOW_UP_HOURS,
  MIN_FOLLOW_UP_HOURS,
  RULE_FORM_FIELDS,
  applyRuleServerErrors,
  buildRuleInput,
  roleFieldOf,
  ruleToFormValues,
  type RuleFormValues,
} from "@/lib/workflow";
import { LEAD_SOURCES, WORKFLOW_KINDS, type WorkflowRule } from "@/types";

const isNumber = (value: unknown): value is number =>
  typeof value === "number" && Number.isFinite(value);

const schema = z
  .object({
    name: z.string().trim().min(1, "auth:validation.required"),
    kind: z.enum(WORKFLOW_KINDS),
    sources: z.array(z.string()),
    assigneeRoleId: z.string(),
    followUpHours: z.union([z.number(), z.string()]),
    minAmount: z.union([z.number(), z.string()]),
    approverRoleId: z.string(),
  })
  // Only the fields of the selected kind are validated; the others keep their (unused) defaults.
  .superRefine((values, ctx) => {
    const add = (path: keyof RuleFormValues, message: string) =>
      ctx.addIssue({ code: "custom", path: [path], message });
    if (values.kind === "leadAssignment") {
      if (!values.assigneeRoleId) add("assigneeRoleId", "workflows:validation.roleRequired");
      const hours = values.followUpHours;
      if (
        !isNumber(hours) ||
        !Number.isInteger(hours) ||
        hours < MIN_FOLLOW_UP_HOURS ||
        hours > MAX_FOLLOW_UP_HOURS
      ) {
        add("followUpHours", "workflows:validation.followUpRange");
      }
    } else {
      if (!values.approverRoleId) add("approverRoleId", "workflows:validation.roleRequired");
      if (!isNumber(values.minAmount) || values.minAmount <= 0) {
        add("minAmount", "workflows:validation.minAmountPositive");
      }
    }
  });

interface RuleFormDialogProps {
  /** Rule to edit; undefined creates a new one. */
  rule?: WorkflowRule;
  onClose: () => void;
}

/**
 * Create / edit dialog of a workflow rule. The parameter fields follow the kind: lead assignment
 * has sources, an assignee role and a follow-up time; deal approval has a minimum amount and an
 * approver role. The kind of an existing rule is fixed. Server field errors (`params.<field>`) and
 * `workflow.role_not_found` are put on their fields.
 */
export function RuleFormDialog({ rule, onClose }: RuleFormDialogProps) {
  const { t } = useTranslation(["workflows", "common", "auth"]);
  const save = useSaveWorkflowRule();
  const roles = useRoles();
  const isEdit = !!rule;

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<RuleFormValues>({
    resolver: zodResolver(schema),
    defaultValues: ruleToFormValues(rule),
  });
  const kind = useWatch({ control, name: "kind" });

  const roleOptions = useMemo(
    () => (roles.data ?? []).map((role) => ({ value: role.id, label: role.name })),
    [roles.data]
  );
  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);
  const roleProps = {
    data: roleOptions,
    placeholder: t("workflows:rules.rolePlaceholder"),
    searchable: true,
    allowDeselect: false,
    disabled: roles.isLoading,
    withAsterisk: true,
  } as const;
  const roleLoadError = roles.isError ? t("workflows:rules.rolesUnavailable") : undefined;

  const onSubmit = handleSubmit(async (values) => {
    try {
      await save.mutateAsync({
        id: rule?.id,
        ...buildRuleInput(values, isEdit ? undefined : true),
      });
      toast({
        variant: "success",
        description: isEdit ? t("workflows:rules.updated") : t("workflows:rules.created"),
      });
      onClose();
    } catch (error) {
      const matched = applyRuleServerErrors(error, setError, RULE_FORM_FIELDS);
      if (getApiProblem(error)?.code === "workflow.role_not_found") {
        setError(roleFieldOf(values.kind), {
          type: "server",
          message: t("common:errors.workflow.role_not_found"),
        });
      } else if (!matched) {
        toastApiError(error);
      }
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={isEdit ? t("workflows:rules.editTitle") : t("workflows:rules.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={isEdit ? t("common:save") : t("common:create")}
      size="md"
    >
      <TextInput
        label={t("workflows:rules.name")}
        withAsterisk
        data-autofocus
        error={message(errors.name?.message)}
        {...register("name")}
      />

      <Controller
        control={control}
        name="kind"
        render={({ field }) => (
          <Select
            label={t("workflows:rules.kind")}
            data={WORKFLOW_KINDS.map((k) => ({ value: k, label: t(`workflows:kinds.${k}`) }))}
            value={field.value}
            onChange={(value) => field.onChange(value ?? "leadAssignment")}
            allowDeselect={false}
            // Params differ per kind, so the kind of an existing rule is fixed.
            disabled={isEdit}
            description={isEdit ? t("workflows:rules.kindFixed") : undefined}
            error={message(errors.kind?.message)}
          />
        )}
      />
      <Text size="sm" c="dimmed">
        {t(`workflows:kindHints.${kind}`)}
      </Text>

      {kind === "leadAssignment" ? (
        <>
          <Controller
            control={control}
            name="sources"
            render={({ field }) => (
              <MultiSelect
                label={t("workflows:rules.sources")}
                placeholder={t("workflows:rules.sourcesPlaceholder")}
                description={t("workflows:rules.sourcesHint")}
                data={LEAD_SOURCES.map((s) => ({ value: s, label: t(`workflows:sources.${s}`) }))}
                value={field.value}
                onChange={field.onChange}
                clearable
                error={message(errors.sources?.message)}
              />
            )}
          />
          <Controller
            control={control}
            name="assigneeRoleId"
            render={({ field }) => (
              <Select
                {...roleProps}
                label={t("workflows:rules.assigneeRole")}
                value={field.value || null}
                onChange={(value) => field.onChange(value ?? "")}
                error={message(errors.assigneeRoleId?.message) ?? roleLoadError}
              />
            )}
          />
          <Controller
            control={control}
            name="followUpHours"
            render={({ field }) => (
              <NumberInput
                label={t("workflows:rules.followUpHours")}
                description={t("workflows:rules.followUpHint")}
                withAsterisk
                allowDecimal={false}
                allowNegative={false}
                clampBehavior="none"
                hideControls
                value={field.value}
                onChange={field.onChange}
                error={message(errors.followUpHours?.message)}
              />
            )}
          />
        </>
      ) : (
        <>
          <Controller
            control={control}
            name="minAmount"
            render={({ field }) => (
              <NumberInput
                label={t("workflows:rules.minAmount")}
                description={t("workflows:rules.minAmountHint")}
                withAsterisk
                decimalScale={2}
                allowNegative={false}
                clampBehavior="none"
                hideControls
                value={field.value}
                onChange={field.onChange}
                error={message(errors.minAmount?.message)}
              />
            )}
          />
          <Controller
            control={control}
            name="approverRoleId"
            render={({ field }) => (
              <Select
                {...roleProps}
                label={t("workflows:rules.approverRole")}
                value={field.value || null}
                onChange={(value) => field.onChange(value ?? "")}
                error={message(errors.approverRoleId?.message) ?? roleLoadError}
              />
            )}
          />
        </>
      )}
    </FormDialog>
  );
}
