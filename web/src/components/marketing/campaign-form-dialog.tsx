import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { NumberInput, Select, SimpleGrid, Textarea, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { OwnerSelect } from "@/components/crm/owner-select";
import { useSaveCampaign } from "@/hooks/use-campaigns";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import {
  CAMPAIGN_CREATE_STATUSES,
  CAMPAIGN_TYPES,
  type Campaign,
  type CampaignStatus,
} from "@/types";

const CURRENCIES = ["TRY", "USD", "EUR", "GBP"];

const amount = z.union([z.number().min(0, "campaigns:validation.amountMin"), z.literal("")]);

const schema = z
  .object({
    name: z.string().trim().min(1, "auth:validation.required").max(200),
    type: z.enum(CAMPAIGN_TYPES),
    status: z.enum(CAMPAIGN_CREATE_STATUSES),
    startDate: z.string(),
    endDate: z.string(),
    currency: z.string().regex(/^[A-Z]{3}$/, "campaigns:validation.currency"),
    budget: amount,
    expectedRevenue: amount,
    actualCost: amount,
    description: z.string().max(4000),
    ownerUserId: z.string(),
  })
  .superRefine((values, ctx) => {
    // `YYYY-MM-DD` strings compare chronologically.
    if (values.startDate && values.endDate && values.endDate < values.startDate) {
      ctx.addIssue({
        code: "custom",
        path: ["endDate"],
        message: "campaigns:validation.endBeforeStart",
      });
    }
  });

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "name",
  "type",
  "status",
  "startDate",
  "endDate",
  "currency",
  "budget",
  "expectedRevenue",
  "actualCost",
  "description",
  "ownerUserId",
] as const;

interface CampaignFormDialogProps {
  campaign?: Campaign;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

const orUndefined = (value: number | ""): number | undefined => (value === "" ? undefined : value);

/**
 * Create / edit dialog. A new campaign starts planned or active; an existing one has no status
 * field (the status menu changes it). Server field errors are put on their fields, and
 * `campaign.invalid_date_range` / `owner.not_member` on the end date / owner.
 */
export function CampaignFormDialog({ campaign, onClose, onSaved }: CampaignFormDialogProps) {
  const { t } = useTranslation(["campaigns", "common", "auth", "crm"]);
  const save = useSaveCampaign();
  const defaultOwnerId = useDefaultOwnerId();
  const isEdit = !!campaign;

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: campaign?.name ?? "",
      type: campaign?.type ?? "email",
      status: "planned",
      startDate: campaign?.startDate?.slice(0, 10) ?? "",
      endDate: campaign?.endDate?.slice(0, 10) ?? "",
      currency: campaign?.currency ?? "TRY",
      budget: campaign?.budget ?? "",
      expectedRevenue: campaign?.expectedRevenue ?? "",
      actualCost: campaign?.actualCost ?? "",
      description: campaign?.description ?? "",
      ownerUserId: campaign?.ownerUserId ?? defaultOwnerId,
    },
  });

  const message = (key?: string) => key && t(key, { defaultValue: key });

  const currencies = campaign && !CURRENCIES.includes(campaign.currency)
    ? [...CURRENCIES, campaign.currency]
    : CURRENCIES;

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: campaign?.id,
        name: values.name.trim(),
        type: values.type,
        // A campaign's status only changes through the status menu.
        status: isEdit ? undefined : (values.status as CampaignStatus),
        startDate: blankToUndefined(values.startDate),
        endDate: blankToUndefined(values.endDate),
        currency: values.currency,
        budget: orUndefined(values.budget),
        expectedRevenue: orUndefined(values.expectedRevenue),
        actualCost: orUndefined(values.actualCost),
        description: blankToUndefined(values.description),
        ownerUserId: blankToUndefined(values.ownerUserId),
      });
      toast({
        variant: "success",
        description: isEdit ? t("campaigns:updated") : t("campaigns:created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      const matched = applyValidationErrors(error, setError, FIELDS);
      const code = getApiProblem(error)?.code;
      const codeField =
        code === "campaign.invalid_date_range"
          ? "endDate"
          : code === "owner.not_member"
            ? "ownerUserId"
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
      title={isEdit ? t("campaigns:editTitle") : t("campaigns:createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={isEdit ? t("common:save") : t("common:create")}
    >
      <TextInput
        label={t("campaigns:fields.name")}
        withAsterisk
        data-autofocus
        error={message(errors.name?.message)}
        {...register("name")}
      />

      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <Controller
          control={control}
          name="type"
          render={({ field }) => (
            <Select
              label={t("campaigns:fields.type")}
              data={CAMPAIGN_TYPES.map((v) => ({ value: v, label: t(`campaigns:types.${v}`) }))}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "email")}
              allowDeselect={false}
              error={message(errors.type?.message)}
            />
          )}
        />
        {!isEdit && (
          <Controller
            control={control}
            name="status"
            render={({ field }) => (
              <Select
                label={t("campaigns:fields.status")}
                data={CAMPAIGN_CREATE_STATUSES.map((v) => ({
                  value: v,
                  label: t(`campaigns:statuses.${v}`),
                }))}
                value={field.value}
                onChange={(value) => field.onChange(value ?? "planned")}
                allowDeselect={false}
                error={message(errors.status?.message)}
              />
            )}
          />
        )}
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          type="date"
          label={t("campaigns:fields.startDate")}
          error={message(errors.startDate?.message)}
          {...register("startDate")}
        />
        <TextInput
          type="date"
          label={t("campaigns:fields.endDate")}
          error={message(errors.endDate?.message)}
          {...register("endDate")}
        />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <Controller
          control={control}
          name="currency"
          render={({ field }) => (
            <Select
              label={t("campaigns:fields.currency")}
              data={currencies}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "TRY")}
              allowDeselect={false}
              error={message(errors.currency?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="budget"
          render={({ field }) => (
            <NumberInput
              label={t("campaigns:fields.budget")}
              value={field.value}
              onChange={(value) => field.onChange(typeof value === "number" ? value : "")}
              min={0}
              clampBehavior="none"
              decimalScale={2}
              thousandSeparator=" "
              hideControls
              error={message(errors.budget?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="expectedRevenue"
          render={({ field }) => (
            <NumberInput
              label={t("campaigns:fields.expectedRevenue")}
              value={field.value}
              onChange={(value) => field.onChange(typeof value === "number" ? value : "")}
              min={0}
              clampBehavior="none"
              decimalScale={2}
              thousandSeparator=" "
              hideControls
              error={message(errors.expectedRevenue?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="actualCost"
          render={({ field }) => (
            <NumberInput
              label={t("campaigns:fields.actualCost")}
              value={field.value}
              onChange={(value) => field.onChange(typeof value === "number" ? value : "")}
              min={0}
              clampBehavior="none"
              decimalScale={2}
              thousandSeparator=" "
              hideControls
              error={message(errors.actualCost?.message)}
            />
          )}
        />
      </SimpleGrid>

      <Controller
        control={control}
        name="ownerUserId"
        render={({ field }) => (
          <OwnerSelect
            label={t("campaigns:fields.owner")}
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            currentOwnerId={campaign?.ownerUserId}
            currentOwnerName={campaign?.ownerName}
            error={message(errors.ownerUserId?.message)}
          />
        )}
      />

      <Textarea
        label={t("campaigns:fields.description")}
        autosize
        minRows={2}
        maxRows={6}
        error={message(errors.description?.message)}
        {...register("description")}
      />
    </FormDialog>
  );
}
