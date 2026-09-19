import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { NumberInput, Select, SimpleGrid, Textarea, TextInput } from "@mantine/core";
import { useContacts } from "@/hooks/use-contacts";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { useSaveDeal } from "@/hooks/use-deals";
import { usePipelines } from "@/hooks/use-pipelines";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import type { Deal } from "@/types";
import { AccountPicker } from "./account-picker";
import { FormDialog } from "./form-dialog";
import { OwnerSelect } from "./owner-select";

const CURRENCIES = ["TRY", "USD", "EUR", "GBP"] as const;

const schema = z.object({
  name: z.string().trim().min(1, "auth:validation.required"),
  accountId: z.string().min(1, "auth:validation.required"),
  contactId: z.string(),
  pipelineId: z.string(),
  stageId: z.string(),
  amount: z.union([z.number().min(0, "crm:validation.amountMin"), z.literal("")]),
  currency: z.string().min(1, "auth:validation.required"),
  closingDate: z.string(),
  ownerUserId: z.string(),
  lostReason: z.string(),
});

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "name",
  "accountId",
  "contactId",
  "pipelineId",
  "stageId",
  "amount",
  "currency",
  "closingDate",
  "ownerUserId",
  "lostReason",
] as const;

interface DealFormDialogProps {
  deal?: Deal;
  /** Pre-selected account when creating from an account page. */
  defaultAccount?: { id: string; name: string };
  /** Pre-selected pipeline (the board's current pipeline). */
  defaultPipelineId?: string;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

export function DealFormDialog({
  deal,
  defaultAccount,
  defaultPipelineId,
  onClose,
  onSaved,
}: DealFormDialogProps) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const save = useSaveDeal();
  const defaultOwnerId = useDefaultOwnerId();
  const pipelines = usePipelines();

  const {
    register,
    control,
    handleSubmit,
    setError,
    setValue,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: deal?.name ?? "",
      accountId: deal?.accountId ?? defaultAccount?.id ?? "",
      contactId: deal?.contactId ?? "",
      pipelineId: deal?.pipelineId ?? defaultPipelineId ?? "",
      stageId: "",
      amount: deal?.amount ?? "",
      currency: deal?.currency ?? "TRY",
      closingDate: deal?.closingDate?.slice(0, 10) ?? "",
      ownerUserId: deal?.ownerUserId ?? defaultOwnerId,
      lostReason: deal?.lostReason ?? "",
    },
  });

  const accountId = useWatch({ control, name: "accountId" });
  const pipelineId = useWatch({ control, name: "pipelineId" });
  const contacts = useContacts(
    { page: 1, pageSize: 100, accountId: accountId || undefined },
    !!accountId
  );

  const pipelineOptions = useMemo(
    () => (pipelines.data ?? []).map((p) => ({ value: p.id, label: p.name })),
    [pipelines.data]
  );
  const effectivePipelineId =
    pipelineId || pipelines.data?.find((p) => p.isDefault)?.id || pipelines.data?.[0]?.id || "";
  const stageOptions = useMemo(
    () =>
      (pipelines.data?.find((p) => p.id === effectivePipelineId)?.stages ?? []).map((s) => ({
        value: s.id,
        label: s.name,
      })),
    [pipelines.data, effectivePipelineId]
  );
  const contactOptions = useMemo(() => {
    const list = (contacts.data?.items ?? []).map((c) => ({ value: c.id, label: c.fullName }));
    if (deal?.contactId && !list.some((o) => o.value === deal.contactId)) {
      list.unshift({ value: deal.contactId, label: deal.contactName ?? deal.contactId });
    }
    return list;
  }, [contacts.data, deal]);

  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: deal?.id,
        name: values.name.trim(),
        accountId: values.accountId,
        contactId: blankToUndefined(values.contactId),
        pipelineId:
          blankToUndefined(values.pipelineId) ??
          (deal ? undefined : effectivePipelineId || undefined),
        // The stage can only be chosen on creation; existing deals move through the board / stage action.
        stageId: deal ? undefined : blankToUndefined(values.stageId),
        amount: values.amount === "" ? undefined : values.amount,
        currency: values.currency,
        closingDate: blankToUndefined(values.closingDate),
        ownerUserId: blankToUndefined(values.ownerUserId),
        lostReason: deal?.stageKind === "lost" ? blankToUndefined(values.lostReason) : undefined,
      });
      toast({
        variant: "success",
        description: deal ? t("crm:deals.updated") : t("crm:deals.created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={deal ? t("crm:deals.editTitle") : t("crm:deals.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={deal ? t("common:save") : t("common:create")}
    >
      <TextInput
        label={t("crm:deals.fields.name")}
        withAsterisk
        data-autofocus
        error={message(errors.name?.message)}
        {...register("name")}
      />
      <Controller
        control={control}
        name="accountId"
        render={({ field }) => (
          <AccountPicker
            value={field.value || null}
            onChange={(value) => {
              field.onChange(value ?? "");
              // The contact belongs to the previous account.
              setValue("contactId", "");
            }}
            selectedName={deal?.accountName ?? defaultAccount?.name}
            withAsterisk
            error={message(errors.accountId?.message)}
          />
        )}
      />
      <Controller
        control={control}
        name="contactId"
        render={({ field }) => (
          <Select
            label={t("crm:deals.fields.contact")}
            placeholder={accountId ? undefined : t("crm:deals.contactNeedsAccount")}
            data={contactOptions}
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            disabled={!accountId}
            searchable
            clearable
            nothingFoundMessage={t("crm:noOptions")}
            error={message(errors.contactId?.message)}
          />
        )}
      />
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <Controller
          control={control}
          name="pipelineId"
          render={({ field }) => (
            <Select
              label={t("crm:deals.fields.pipeline")}
              data={pipelineOptions}
              value={field.value || effectivePipelineId || null}
              onChange={(value) => {
                field.onChange(value ?? "");
                setValue("stageId", "");
              }}
              disabled={!!deal}
              allowDeselect={false}
              error={message(errors.pipelineId?.message)}
            />
          )}
        />
        {!deal && (
          <Controller
            control={control}
            name="stageId"
            render={({ field }) => (
              <Select
                label={t("crm:deals.fields.stage")}
                placeholder={t("crm:deals.firstStage")}
                data={stageOptions}
                value={field.value || null}
                onChange={(value) => field.onChange(value ?? "")}
                clearable
                error={message(errors.stageId?.message)}
              />
            )}
          />
        )}
        <Controller
          control={control}
          name="amount"
          render={({ field }) => (
            <NumberInput
              label={t("crm:deals.fields.amount")}
              value={field.value}
              onChange={(value) => field.onChange(typeof value === "number" ? value : "")}
              min={0}
              decimalScale={2}
              thousandSeparator=" "
              hideControls
              error={message(errors.amount?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="currency"
          render={({ field }) => (
            <Select
              label={t("crm:deals.fields.currency")}
              data={[...CURRENCIES]}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "TRY")}
              allowDeselect={false}
              error={message(errors.currency?.message)}
            />
          )}
        />
        <TextInput
          label={t("crm:deals.fields.closingDate")}
          type="date"
          error={message(errors.closingDate?.message)}
          {...register("closingDate")}
        />
        <Controller
          control={control}
          name="ownerUserId"
          render={({ field }) => (
            <OwnerSelect
              value={field.value || null}
              onChange={(value) => field.onChange(value ?? "")}
              currentOwnerId={deal?.ownerUserId}
              currentOwnerName={deal?.ownerName}
              error={message(errors.ownerUserId?.message)}
            />
          )}
        />
      </SimpleGrid>
      {deal?.stageKind === "lost" && (
        <Textarea
          label={t("crm:deals.fields.lostReason")}
          autosize
          minRows={2}
          error={message(errors.lostReason?.message)}
          {...register("lostReason")}
        />
      )}
    </FormDialog>
  );
}
