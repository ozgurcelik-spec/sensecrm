import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { NumberInput, Select, SimpleGrid, Switch, Textarea, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { useSaveProduct } from "@/hooks/use-products";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import { CURRENCIES, type Product } from "@/types";

/** Suggested VAT rate of a new product (the API default is 0). */
const DEFAULT_PRODUCT_TAX_RATE = 20;

/**
 * The number inputs hand back text while a value is being typed ("250." on the way to "250.5"), and the
 * form must keep that text or the input would clear itself: the fields hold `number | string`.
 */
const numberish = z.union([z.number(), z.string()]);

function toNumber(value: number | string): number {
  if (typeof value === "number") return value;
  return value.trim() === "" ? 0 : Number(value);
}

function decimalPlaces(value: number): number {
  const text = value.toString();
  return text.includes("e") ? Number.POSITIVE_INFINITY : (text.split(".")[1]?.length ?? 0);
}

const schema = z
  .object({
    name: z.string().trim().min(1, "auth:validation.required").max(200, "commerce:validation.nameMax"),
    code: z.string().trim().max(64, "commerce:validation.codeMax"),
    description: z.string().max(2000, "commerce:validation.descriptionMax2000"),
    unitPrice: numberish,
    currency: z.string().min(1, "auth:validation.required"),
    taxRate: numberish,
    unit: z.string().trim().max(32, "commerce:validation.unitMax"),
    isActive: z.boolean(),
  })
  .superRefine((values, ctx) => {
    const price = toNumber(values.unitPrice);
    if (!Number.isFinite(price) || price < 0) {
      ctx.addIssue({ code: "custom", path: ["unitPrice"], message: "commerce:validation.priceMin" });
    } else if (price > 1_000_000_000) {
      ctx.addIssue({ code: "custom", path: ["unitPrice"], message: "commerce:validation.priceMax" });
    } else if (decimalPlaces(price) > 4) {
      ctx.addIssue({ code: "custom", path: ["unitPrice"], message: "commerce:validation.decimals4" });
    }
    const tax = toNumber(values.taxRate);
    if (!Number.isFinite(tax) || tax < 0 || tax > 100) {
      ctx.addIssue({ code: "custom", path: ["taxRate"], message: "commerce:validation.percentRange" });
    } else if (decimalPlaces(tax) > 2) {
      ctx.addIssue({ code: "custom", path: ["taxRate"], message: "commerce:validation.decimals2" });
    }
  });

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "name",
  "code",
  "description",
  "unitPrice",
  "currency",
  "taxRate",
  "unit",
  "isActive",
] as const;

interface ProductFormDialogProps {
  product?: Product;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

export function ProductFormDialog({ product, onClose, onSaved }: ProductFormDialogProps) {
  const { t } = useTranslation(["commerce", "common", "auth"]);
  const save = useSaveProduct();
  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: product?.name ?? "",
      code: product?.code ?? "",
      description: product?.description ?? "",
      unitPrice: product?.unitPrice ?? 0,
      currency: product?.currency ?? "TRY",
      taxRate: product?.taxRate ?? DEFAULT_PRODUCT_TAX_RATE,
      unit: product?.unit ?? "",
      isActive: product?.isActive ?? true,
    },
  });

  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: product?.id,
        name: values.name.trim(),
        code: blankToUndefined(values.code),
        description: blankToUndefined(values.description),
        unitPrice: toNumber(values.unitPrice),
        currency: values.currency,
        taxRate: toNumber(values.taxRate),
        unit: blankToUndefined(values.unit),
        isActive: values.isActive,
      });
      toast({
        variant: "success",
        description: product ? t("commerce:products.updated") : t("commerce:products.created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      if (getApiProblem(error)?.code === "product.code_taken") {
        // The SKU is unique per organization (case-insensitive): show it on the field.
        setError("code", { type: "server", message: t("common:errors.product.code_taken") });
        return;
      }
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={product ? t("commerce:products.editTitle") : t("commerce:products.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={product ? t("common:save") : t("common:create")}
    >
      <TextInput
        label={t("commerce:products.fields.name")}
        withAsterisk
        data-autofocus
        error={message(errors.name?.message)}
        {...register("name")}
      />
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          label={t("commerce:products.fields.code")}
          error={message(errors.code?.message)}
          {...register("code")}
        />
        <TextInput
          label={t("commerce:products.fields.unit")}
          placeholder={t("commerce:products.unitPlaceholder")}
          error={message(errors.unit?.message)}
          {...register("unit")}
        />
        <Controller
          control={control}
          name="unitPrice"
          render={({ field }) => (
            <NumberInput
              label={t("commerce:products.fields.unitPrice")}
              value={field.value}
              onChange={field.onChange}
              min={0}
              decimalScale={4}
              hideControls
              error={message(errors.unitPrice?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="currency"
          render={({ field }) => (
            <Select
              label={t("commerce:products.fields.currency")}
              data={[...CURRENCIES]}
              value={field.value}
              onChange={(value) => field.onChange(value ?? "TRY")}
              allowDeselect={false}
              error={message(errors.currency?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="taxRate"
          render={({ field }) => (
            <NumberInput
              label={t("commerce:products.fields.taxRate")}
              value={field.value}
              onChange={field.onChange}
              min={0}
              max={100}
              decimalScale={2}
              hideControls
              error={message(errors.taxRate?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="isActive"
          render={({ field }) => (
            <Switch
              mt={{ base: 0, sm: 28 }}
              label={t("commerce:products.fields.isActive")}
              checked={field.value}
              onChange={(event) => field.onChange(event.currentTarget.checked)}
            />
          )}
        />
      </SimpleGrid>
      <Textarea
        label={t("commerce:products.fields.description")}
        autosize
        minRows={2}
        error={message(errors.description?.message)}
        {...register("description")}
      />
    </FormDialog>
  );
}
