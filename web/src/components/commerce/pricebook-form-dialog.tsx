import { useTranslation } from "react-i18next";
import { Controller, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { NumberInput, Select, SimpleGrid, Switch, Textarea, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { OwnerSelect } from "@/components/crm/owner-select";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { useSavePriceBook } from "@/hooks/use-pricebooks";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import { CURRENCIES, PRICING_MODELS, type PriceBook, type PriceBookPricingModel } from "@/types";

const numberish = z.union([z.number(), z.string()]);

function toNumber(value: number | string): number {
  if (typeof value === "number") return value;
  return value.trim() === "" ? Number.NaN : Number(value);
}

function decimalPlaces(value: number): number {
  const text = value.toString();
  return text.includes("e") ? Number.POSITIVE_INFINITY : (text.split(".")[1]?.length ?? 0);
}

const schema = z
  .object({
    name: z.string().trim().min(1, "auth:validation.required").max(200, "commerce:validation.nameMax"),
    ownerUserId: z.string(),
    isActive: z.boolean(),
    pricingModel: z.enum(PRICING_MODELS),
    adjustmentPercent: numberish,
    currency: z.string().min(1, "auth:validation.required"),
    validFrom: z.string(),
    validTo: z.string(),
    description: z.string().max(2000, "commerce:validation.descriptionMax2000"),
  })
  .superRefine((values, ctx) => {
    if (values.pricingModel === "flat") {
      const percent = toNumber(values.adjustmentPercent);
      if (Number.isNaN(percent)) {
        ctx.addIssue({ code: "custom", path: ["adjustmentPercent"], message: "inventory:priceBooks.validation.percentRequired" });
      } else if (percent < -99.99 || percent > 1000) {
        ctx.addIssue({ code: "custom", path: ["adjustmentPercent"], message: "inventory:priceBooks.validation.percentRange" });
      } else if (decimalPlaces(percent) > 2) {
        ctx.addIssue({ code: "custom", path: ["adjustmentPercent"], message: "commerce:validation.decimals2" });
      }
    }
    if (values.validFrom && values.validTo && values.validTo < values.validFrom) {
      ctx.addIssue({ code: "custom", path: ["validTo"], message: "inventory:priceBooks.validation.validRange" });
    }
  });

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "name",
  "ownerUserId",
  "isActive",
  "pricingModel",
  "adjustmentPercent",
  "currency",
  "validFrom",
  "validTo",
  "description",
] as const;

interface PriceBookFormDialogProps {
  priceBook?: PriceBook;
  initialName?: string;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

/**
 * Create / edit price book dialog (Zoho form). The pricing model and the currency are fixed after
 * creation (entries and documents depend on them): they show locked while editing, and the server
 * answers `pricebook.model_immutable` if they are sent changed anyway.
 */
export function PriceBookFormDialog({ priceBook, initialName, onClose, onSaved }: PriceBookFormDialogProps) {
  const { t } = useTranslation(["inventory", "common", "auth", "commerce"]);
  const save = useSavePriceBook();
  const defaultOwnerId = useDefaultOwnerId();
  const editing = !!priceBook;
  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: priceBook?.name ?? initialName ?? "",
      ownerUserId: priceBook?.ownerUserId ?? defaultOwnerId,
      isActive: priceBook?.isActive ?? true,
      pricingModel: priceBook?.pricingModel ?? "perProduct",
      adjustmentPercent: priceBook?.adjustmentPercent ?? "",
      currency: priceBook?.currency ?? "TRY",
      validFrom: priceBook?.validFrom?.slice(0, 10) ?? "",
      validTo: priceBook?.validTo?.slice(0, 10) ?? "",
      description: priceBook?.description ?? "",
    },
  });
  const model = useWatch({ control, name: "pricingModel" });

  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: priceBook?.id,
        name: values.name.trim(),
        ownerUserId: blankToUndefined(values.ownerUserId),
        isActive: values.isActive,
        pricingModel: values.pricingModel,
        adjustmentPercent: values.pricingModel === "flat" ? toNumber(values.adjustmentPercent) : undefined,
        currency: values.currency,
        validFrom: blankToUndefined(values.validFrom),
        validTo: blankToUndefined(values.validTo),
        description: blankToUndefined(values.description),
      });
      toast({
        variant: "success",
        description: priceBook ? t("inventory:priceBooks.updated") : t("inventory:priceBooks.created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      if (getApiProblem(error)?.code === "pricebook.name_taken") {
        setError("name", { type: "server", message: t("inventory:errors.pricebook.name_taken") });
        return;
      }
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={editing ? t("inventory:priceBooks.editTitle") : t("inventory:priceBooks.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={editing ? t("common:save") : t("common:create")}
    >
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <Controller
          control={control}
          name="ownerUserId"
          render={({ field }) => (
            <OwnerSelect
              value={field.value || null}
              onChange={(value) => field.onChange(value ?? "")}
              currentOwnerId={priceBook?.ownerUserId}
              currentOwnerName={priceBook?.ownerName}
              error={message(errors.ownerUserId?.message)}
            />
          )}
        />
        <TextInput
          label={t("inventory:priceBooks.fields.name")}
          withAsterisk
          data-autofocus
          error={message(errors.name?.message)}
          {...register("name")}
        />
        <Controller
          control={control}
          name="pricingModel"
          render={({ field }) => (
            <Select
              label={t("inventory:priceBooks.fields.pricingModel")}
              description={editing ? t("inventory:priceBooks.lockedHint") : undefined}
              data={PRICING_MODELS.map((m) => ({ value: m, label: t(`inventory:priceBooks.models.${m}`) }))}
              value={field.value}
              onChange={(value) => field.onChange((value ?? "perProduct") as PriceBookPricingModel)}
              allowDeselect={false}
              disabled={editing}
              error={message(errors.pricingModel?.message)}
            />
          )}
        />
        {model === "flat" && (
          <Controller
            control={control}
            name="adjustmentPercent"
            render={({ field }) => (
              <NumberInput
                label={t("inventory:priceBooks.fields.adjustmentPercent")}
                description={t("inventory:priceBooks.percentHint")}
                withAsterisk
                value={field.value}
                onChange={field.onChange}
                allowNegative
                decimalScale={2}
                min={-99.99}
                max={1000}
                hideControls
                suffix=" %"
                error={message(errors.adjustmentPercent?.message)}
              />
            )}
          />
        )}
        <Controller
          control={control}
          name="currency"
          render={({ field }) => (
            <Select
              label={t("inventory:priceBooks.fields.currency")}
              data={[...CURRENCIES]}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "TRY")}
              allowDeselect={false}
              disabled={editing}
              error={message(errors.currency?.message)}
            />
          )}
        />
        <TextInput
          label={t("inventory:priceBooks.fields.validFrom")}
          type="date"
          error={message(errors.validFrom?.message)}
          {...register("validFrom")}
        />
        <TextInput
          label={t("inventory:priceBooks.fields.validTo")}
          type="date"
          error={message(errors.validTo?.message)}
          {...register("validTo")}
        />
        <Controller
          control={control}
          name="isActive"
          render={({ field }) => (
            <Switch
              mt={{ base: 0, sm: 28 }}
              label={t("inventory:priceBooks.fields.isActive")}
              checked={field.value}
              onChange={(event) => field.onChange(event.currentTarget.checked)}
            />
          )}
        />
      </SimpleGrid>
      <Textarea
        label={t("inventory:priceBooks.fields.description")}
        autosize
        minRows={2}
        error={message(errors.description?.message)}
        {...register("description")}
      />
    </FormDialog>
  );
}
