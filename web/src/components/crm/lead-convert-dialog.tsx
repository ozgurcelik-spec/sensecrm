import { useMemo, useState } from "react";
import { useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import {
  Alert,
  Button,
  NumberInput,
  SegmentedControl,
  Select,
  SimpleGrid,
  Switch,
  Text,
  TextInput,
} from "@mantine/core";
import { useAccounts } from "@/hooks/use-accounts";
import { useConvertLead } from "@/hooks/use-leads";
import { usePipelines } from "@/hooks/use-pipelines";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import type { Lead } from "@/types";
import { AccountPicker } from "./account-picker";
import { FormDialog } from "./form-dialog";

const schema = z
  .object({
    accountMode: z.enum(["new", "existing"]),
    accountId: z.string(),
    createDeal: z.boolean(),
    dealName: z.string(),
    amount: z.union([z.number().min(0, "crm:validation.amountMin"), z.literal("")]),
    closingDate: z.string(),
    pipelineId: z.string(),
  })
  .superRefine((values, ctx) => {
    if (values.accountMode === "existing" && !values.accountId) {
      ctx.addIssue({ code: "custom", path: ["accountId"], message: "auth:validation.required" });
    }
    if (values.createDeal && !values.dealName.trim()) {
      ctx.addIssue({ code: "custom", path: ["dealName"], message: "auth:validation.required" });
    }
  });

type FormValues = z.infer<typeof schema>;

const FIELDS = ["accountId", "dealName", "amount", "closingDate", "pipelineId"] as const;

interface LeadConvertDialogProps {
  lead: Lead;
  /** False hides the "create a deal" option (no `crm.deals.write`). */
  canCreateDeal: boolean;
  onClose: () => void;
}

/**
 * Lead conversion: pick a new or an existing account, optionally open a deal, then jump to the
 * created record (the deal when one was created, otherwise the account).
 */
export function LeadConvertDialog({ lead, canCreateDeal, onClose }: LeadConvertDialogProps) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const navigate = useNavigate();
  const convert = useConvertLead();
  const pipelines = usePipelines(canCreateDeal);
  const [suggestionDismissed, setSuggestionDismissed] = useState(false);

  // A same-named account probably is the lead's company: offer it instead of creating a duplicate.
  const matches = useAccounts({ page: 1, pageSize: 5, q: lead.company });
  const sameNameAccount = useMemo(
    () =>
      matches.data?.items.find(
        (a) => a.name.trim().toLocaleLowerCase() === lead.company.trim().toLocaleLowerCase()
      ),
    [matches.data, lead.company]
  );

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
      accountMode: "new",
      accountId: "",
      createDeal: false,
      dealName: lead.company,
      amount: "",
      closingDate: "",
      pipelineId: "",
    },
  });

  const accountMode = useWatch({ control, name: "accountMode" });
  const createDeal = useWatch({ control, name: "createDeal" });
  const accountId = useWatch({ control, name: "accountId" });
  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    const withDeal = canCreateDeal && values.createDeal;
    try {
      const result = await convert.mutateAsync({
        id: lead.id,
        accountId: values.accountMode === "existing" ? values.accountId : undefined,
        createDeal: withDeal,
        dealName: withDeal ? values.dealName.trim() : undefined,
        amount: withDeal && values.amount !== "" ? values.amount : undefined,
        closingDate: withDeal ? blankToUndefined(values.closingDate) : undefined,
        pipelineId: withDeal ? blankToUndefined(values.pipelineId) : undefined,
      });
      toast({ variant: "success", description: t("crm:leads.convert.done") });
      onClose();
      navigate(result.dealId ? `/app/deals/${result.dealId}` : `/app/accounts/${result.accountId}`);
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  const showSuggestion = !!sameNameAccount && !suggestionDismissed && accountMode === "new";

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={t("crm:leads.convert.title", { name: lead.fullName })}
      onSubmit={onSubmit}
      loading={convert.isPending}
      submitLabel={t("crm:leads.convert.submit")}
    >
      <Text size="sm" c="dimmed">
        {t("crm:leads.convert.intro", { company: lead.company, name: lead.fullName })}
      </Text>

      <Controller
        control={control}
        name="accountMode"
        render={({ field }) => (
          <SegmentedControl
            fullWidth
            aria-label={t("crm:leads.convert.accountMode")}
            value={field.value}
            onChange={(value) => field.onChange(value)}
            data={[
              { value: "new", label: t("crm:leads.convert.newAccount", { company: lead.company }) },
              { value: "existing", label: t("crm:leads.convert.existingAccount") },
            ]}
          />
        )}
      />

      {showSuggestion && sameNameAccount && (
        <Alert color="yellow" variant="light" title={t("crm:leads.convert.sameNameTitle")}>
          <Text size="sm" mb="xs">
            {t("crm:leads.convert.sameNameText", { name: sameNameAccount.name })}
          </Text>
          <Button
            size="xs"
            variant="default"
            onClick={() => {
              setValue("accountMode", "existing");
              setValue("accountId", sameNameAccount.id);
            }}
          >
            {t("crm:leads.convert.useExisting")}
          </Button>
          <Button size="xs" variant="subtle" ml="xs" onClick={() => setSuggestionDismissed(true)}>
            {t("crm:leads.convert.createNew")}
          </Button>
        </Alert>
      )}

      {accountMode === "existing" && (
        <Controller
          control={control}
          name="accountId"
          render={({ field }) => (
            <AccountPicker
              value={field.value || null}
              onChange={(value) => field.onChange(value ?? "")}
              selectedName={accountId === sameNameAccount?.id ? sameNameAccount?.name : undefined}
              withAsterisk
              data-autofocus
              error={message(errors.accountId?.message)}
            />
          )}
        />
      )}

      {canCreateDeal && (
        <>
          <Switch label={t("crm:leads.convert.createDeal")} {...register("createDeal")} />
          {createDeal && (
            <>
              <TextInput
                label={t("crm:deals.fields.name")}
                withAsterisk
                error={message(errors.dealName?.message)}
                {...register("dealName")}
              />
              <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
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
                <TextInput
                  label={t("crm:deals.fields.closingDate")}
                  type="date"
                  error={message(errors.closingDate?.message)}
                  {...register("closingDate")}
                />
              </SimpleGrid>
              {(pipelines.data?.length ?? 0) > 1 && (
                <Controller
                  control={control}
                  name="pipelineId"
                  render={({ field }) => (
                    <Select
                      label={t("crm:deals.fields.pipeline")}
                      placeholder={t("crm:leads.convert.defaultPipeline")}
                      data={(pipelines.data ?? []).map((p) => ({ value: p.id, label: p.name }))}
                      value={field.value || null}
                      onChange={(value) => field.onChange(value ?? "")}
                      clearable
                      error={message(errors.pipelineId?.message)}
                    />
                  )}
                />
              )}
            </>
          )}
        </>
      )}
    </FormDialog>
  );
}
