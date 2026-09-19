import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { SimpleGrid, Textarea, TextInput } from "@mantine/core";
import { useSaveAccount } from "@/hooks/use-accounts";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import { isHttpUrl } from "@/lib/url";
import type { Account } from "@/types";
import { EMPTY_ADDRESS, toAddressPayload, toAddressValues } from "@/lib/address";
import { AddressFields } from "./address-fields";
import { FormDialog } from "./form-dialog";
import { OwnerSelect } from "./owner-select";

const addressSchema = z.object({
  street: z.string(),
  city: z.string(),
  state: z.string(),
  postalCode: z.string(),
  country: z.string(),
});

const schema = z.object({
  name: z.string().trim().min(1, "auth:validation.required"),
  industry: z.string(),
  // Optional; when given it must be an absolute http:// or https:// URL (never javascript:, data: ...).
  website: z
    .string()
    .trim()
    .refine((v) => v === "" || isHttpUrl(v), "security:website.invalid"),
  phone: z.string(),
  email: z
    .string()
    .trim()
    .refine((v) => v === "" || z.email().safeParse(v).success, "auth:validation.email"),
  description: z.string(),
  ownerUserId: z.string(),
  billingAddress: addressSchema,
});

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "name",
  "industry",
  "website",
  "phone",
  "email",
  "description",
  "ownerUserId",
  "billingAddress.street",
  "billingAddress.city",
  "billingAddress.state",
  "billingAddress.postalCode",
  "billingAddress.country",
] as const;

interface AccountFormDialogProps {
  /** Account to edit; undefined creates a new one. */
  account?: Account;
  onClose: () => void;
  /** Called with the saved account's id after a successful save. */
  onSaved?: (id: string) => void;
}

/** Create/edit account dialog. Mount it only while it is open (it resets its form on mount). */
export function AccountFormDialog({ account, onClose, onSaved }: AccountFormDialogProps) {
  const { t } = useTranslation(["crm", "common", "auth", "security"]);
  const save = useSaveAccount();
  const defaultOwnerId = useDefaultOwnerId();
  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: account?.name ?? "",
      industry: account?.industry ?? "",
      website: account?.website ?? "",
      phone: account?.phone ?? "",
      email: account?.email ?? "",
      description: account?.description ?? "",
      ownerUserId: account?.ownerUserId ?? defaultOwnerId,
      billingAddress: account ? toAddressValues(account.billingAddress) : EMPTY_ADDRESS,
    },
  });

  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: account?.id,
        name: values.name.trim(),
        industry: blankToUndefined(values.industry),
        website: blankToUndefined(values.website),
        phone: blankToUndefined(values.phone),
        email: blankToUndefined(values.email),
        description: blankToUndefined(values.description),
        ownerUserId: blankToUndefined(values.ownerUserId),
        billingAddress: toAddressPayload(values.billingAddress),
      });
      toast({
        variant: "success",
        description: account ? t("crm:accounts.updated") : t("crm:accounts.created"),
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
      title={account ? t("crm:accounts.editTitle") : t("crm:accounts.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={account ? t("common:save") : t("common:create")}
    >
      <TextInput
        label={t("crm:accounts.fields.name")}
        withAsterisk
        data-autofocus
        error={message(errors.name?.message)}
        {...register("name")}
      />
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          label={t("crm:accounts.fields.industry")}
          error={message(errors.industry?.message)}
          {...register("industry")}
        />
        <TextInput
          label={t("crm:accounts.fields.website")}
          error={message(errors.website?.message)}
          {...register("website")}
        />
        <TextInput
          label={t("crm:accounts.fields.phone")}
          error={message(errors.phone?.message)}
          {...register("phone")}
        />
        <TextInput
          label={t("crm:accounts.fields.email")}
          type="email"
          error={message(errors.email?.message)}
          {...register("email")}
        />
      </SimpleGrid>
      <Controller
        control={control}
        name="ownerUserId"
        render={({ field }) => (
          <OwnerSelect
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            currentOwnerId={account?.ownerUserId}
            currentOwnerName={account?.ownerName}
            error={message(errors.ownerUserId?.message)}
          />
        )}
      />
      <AddressFields
        name="billingAddress"
        legend={t("crm:accounts.fields.billingAddress")}
        register={register}
        errors={errors}
      />
      <Textarea
        label={t("crm:accounts.fields.description")}
        autosize
        minRows={2}
        error={message(errors.description?.message)}
        {...register("description")}
      />
    </FormDialog>
  );
}
