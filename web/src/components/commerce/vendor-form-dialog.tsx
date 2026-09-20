import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Autocomplete, SimpleGrid, Switch, Textarea, TextInput } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { OwnerSelect } from "@/components/crm/owner-select";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { useSaveVendor } from "@/hooks/use-vendors";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors, getApiProblem } from "@/lib/api-error";
import {
  toDocumentAddressPayload,
  toDocumentAddressValues,
  type AddressField,
  type DocumentAddressValues,
} from "@/lib/document-address";
import { blankToUndefined } from "@/lib/format";
import { isHttpUrl } from "@/lib/url";
import type { Vendor } from "@/types";
import { AddressBlock, type AddressErrors } from "./address-blocks";

/** Zoho's example GL accounts, offered as suggestions only (free text; there is no accounting mapping). */
const GL_ACCOUNT_SUGGESTIONS = [
  "Advertising and Marketing",
  "Consulting",
  "Cost of Goods Sold",
  "Office Supplies",
  "Other Expenses",
  "Rent Expense",
  "Telephone Expense",
  "Travel Expense",
] as const;

const schema = z.object({
  name: z.string().trim().min(1, "auth:validation.required").max(200, "commerce:validation.nameMax"),
  ownerUserId: z.string(),
  phone: z.string().trim().max(32, "inventory:vendors.validation.phoneMax"),
  email: z
    .string()
    .trim()
    .max(254, "inventory:vendors.validation.emailMax")
    .refine((v) => v === "" || z.email().safeParse(v).success, "auth:validation.email"),
  website: z
    .string()
    .trim()
    .max(200, "inventory:vendors.validation.websiteMax")
    .refine((v) => v === "" || isHttpUrl(v), "security:website.invalid"),
  category: z.string().trim().max(100, "inventory:vendors.validation.categoryMax"),
  glAccount: z.string().trim().max(100, "inventory:vendors.validation.glAccountMax"),
  description: z.string().max(2000, "commerce:validation.descriptionMax2000"),
  emailOptOut: z.boolean(),
});

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "name",
  "ownerUserId",
  "phone",
  "email",
  "website",
  "category",
  "glAccount",
  "description",
  "emailOptOut",
] as const;

interface VendorFormDialogProps {
  vendor?: Vendor;
  /** A starting name (what was typed in a lookup window's search box). */
  initialName?: string;
  onClose: () => void;
  onSaved?: (id: string) => void;
}

/** Create / edit vendor dialog (Zoho form). Mount it only while it is open (it starts fresh). */
export function VendorFormDialog({ vendor, initialName, onClose, onSaved }: VendorFormDialogProps) {
  const { t } = useTranslation(["inventory", "common", "auth", "commerce", "security"]);
  const save = useSaveVendor();
  const defaultOwnerId = useDefaultOwnerId();
  const [address, setAddress] = useState<DocumentAddressValues>(() => toDocumentAddressValues(vendor?.address));
  const [addressErrors, setAddressErrors] = useState<AddressErrors>({});
  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: vendor?.name ?? initialName ?? "",
      ownerUserId: vendor?.ownerUserId ?? defaultOwnerId,
      phone: vendor?.phone ?? "",
      email: vendor?.email ?? "",
      website: vendor?.website ?? "",
      category: vendor?.category ?? "",
      glAccount: vendor?.glAccount ?? "",
      description: vendor?.description ?? "",
      emailOptOut: vendor?.emailOptOut ?? false,
    },
  });

  const message = (key?: string) => (key ? t(key, { defaultValue: key }) : undefined);

  const onSubmit = handleSubmit(async (values) => {
    setAddressErrors({});
    try {
      const id = await save.mutateAsync({
        id: vendor?.id,
        name: values.name.trim(),
        ownerUserId: blankToUndefined(values.ownerUserId),
        phone: blankToUndefined(values.phone),
        email: blankToUndefined(values.email),
        website: blankToUndefined(values.website),
        category: blankToUndefined(values.category),
        glAccount: blankToUndefined(values.glAccount),
        address: toDocumentAddressPayload(address),
        description: blankToUndefined(values.description),
        emailOptOut: values.emailOptOut,
      });
      toast({
        variant: "success",
        description: vendor ? t("inventory:vendors.updated") : t("inventory:vendors.created"),
      });
      onSaved?.(id);
      onClose();
    } catch (error) {
      const fromServer = getApiProblem(error)?.errors ?? {};
      const found: AddressErrors = {};
      for (const [key, messages] of Object.entries(fromServer)) {
        const match = /^address\.(\w+)$/i.exec(key);
        const field = match?.[1] ? (match[1].charAt(0).toLowerCase() + match[1].slice(1)) : undefined;
        if (field && messages[0]) found[field as AddressField] = messages[0];
      }
      setAddressErrors(found);
      const matched = applyValidationErrors(error, setError, FIELDS);
      if (!matched && Object.keys(found).length === 0) toastApiError(error);
    }
  });

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={vendor ? t("inventory:vendors.editTitle") : t("inventory:vendors.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={vendor ? t("common:save") : t("common:create")}
      size="xl"
    >
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <Controller
          control={control}
          name="ownerUserId"
          render={({ field }) => (
            <OwnerSelect
              value={field.value || null}
              onChange={(value) => field.onChange(value ?? "")}
              currentOwnerId={vendor?.ownerUserId}
              currentOwnerName={vendor?.ownerName}
              error={message(errors.ownerUserId?.message)}
            />
          )}
        />
        <TextInput
          label={t("inventory:vendors.fields.name")}
          withAsterisk
          data-autofocus
          error={message(errors.name?.message)}
          {...register("name")}
        />
        <TextInput
          label={t("inventory:vendors.fields.phone")}
          error={message(errors.phone?.message)}
          {...register("phone")}
        />
        <TextInput
          label={t("inventory:vendors.fields.email")}
          type="email"
          error={message(errors.email?.message)}
          {...register("email")}
        />
        <TextInput
          label={t("inventory:vendors.fields.website")}
          error={message(errors.website?.message)}
          {...register("website")}
        />
        <TextInput
          label={t("inventory:vendors.fields.category")}
          error={message(errors.category?.message)}
          {...register("category")}
        />
        <Controller
          control={control}
          name="glAccount"
          render={({ field }) => (
            <Autocomplete
              label={t("inventory:vendors.fields.glAccount")}
              description={t("inventory:vendors.glAccountHint")}
              data={[...GL_ACCOUNT_SUGGESTIONS]}
              value={field.value}
              onChange={field.onChange}
              error={message(errors.glAccount?.message)}
            />
          )}
        />
        <Controller
          control={control}
          name="emailOptOut"
          render={({ field }) => (
            <Switch
              mt={{ base: 0, sm: 28 }}
              label={t("inventory:vendors.fields.emailOptOut")}
              checked={field.value}
              onChange={(event) => field.onChange(event.currentTarget.checked)}
            />
          )}
        />
      </SimpleGrid>
      <AddressBlock
        name="address"
        legend={t("inventory:vendors.fields.address")}
        values={address}
        onChange={setAddress}
        errors={addressErrors}
      />
      <Textarea
        label={t("inventory:vendors.fields.description")}
        autosize
        minRows={2}
        error={message(errors.description?.message)}
        {...register("description")}
      />
    </FormDialog>
  );
}
