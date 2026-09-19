import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { SimpleGrid, TextInput } from "@mantine/core";
import { useSaveContact } from "@/hooks/use-contacts";
import { useDefaultOwnerId } from "@/hooks/use-default-owner";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import type { Contact } from "@/types";
import { AccountPicker } from "./account-picker";
import { EMPTY_ADDRESS, toAddressPayload, toAddressValues } from "@/lib/address";
import { AddressFields } from "./address-fields";
import { FormDialog } from "./form-dialog";
import { OwnerSelect } from "./owner-select";

const schema = z.object({
  firstName: z.string(),
  lastName: z.string().trim().min(1, "auth:validation.required"),
  email: z
    .string()
    .trim()
    .refine((v) => v === "" || z.email().safeParse(v).success, "auth:validation.email"),
  phone: z.string(),
  mobile: z.string(),
  title: z.string(),
  accountId: z.string(),
  ownerUserId: z.string(),
  mailingAddress: z.object({
    street: z.string(),
    city: z.string(),
    state: z.string(),
    postalCode: z.string(),
    country: z.string(),
  }),
});

type FormValues = z.infer<typeof schema>;

const FIELDS = [
  "firstName",
  "lastName",
  "email",
  "phone",
  "mobile",
  "title",
  "accountId",
  "ownerUserId",
  "mailingAddress.street",
  "mailingAddress.city",
  "mailingAddress.state",
  "mailingAddress.postalCode",
  "mailingAddress.country",
] as const;

interface ContactFormDialogProps {
  contact?: Contact;
  /** Pre-selected account when creating from an account page. */
  defaultAccount?: { id: string; name: string };
  onClose: () => void;
  onSaved?: (id: string) => void;
}

export function ContactFormDialog({
  contact,
  defaultAccount,
  onClose,
  onSaved,
}: ContactFormDialogProps) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const save = useSaveContact();
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
      firstName: contact?.firstName ?? "",
      lastName: contact?.lastName ?? "",
      email: contact?.email ?? "",
      phone: contact?.phone ?? "",
      mobile: contact?.mobile ?? "",
      title: contact?.title ?? "",
      accountId: contact?.accountId ?? defaultAccount?.id ?? "",
      ownerUserId: contact?.ownerUserId ?? defaultOwnerId,
      mailingAddress: contact ? toAddressValues(contact.mailingAddress) : EMPTY_ADDRESS,
    },
  });

  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const id = await save.mutateAsync({
        id: contact?.id,
        firstName: blankToUndefined(values.firstName),
        lastName: values.lastName.trim(),
        email: blankToUndefined(values.email),
        phone: blankToUndefined(values.phone),
        mobile: blankToUndefined(values.mobile),
        title: blankToUndefined(values.title),
        accountId: blankToUndefined(values.accountId),
        ownerUserId: blankToUndefined(values.ownerUserId),
        mailingAddress: toAddressPayload(values.mailingAddress),
      });
      toast({
        variant: "success",
        description: contact ? t("crm:contacts.updated") : t("crm:contacts.created"),
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
      title={contact ? t("crm:contacts.editTitle") : t("crm:contacts.createTitle")}
      onSubmit={onSubmit}
      loading={save.isPending}
      submitLabel={contact ? t("common:save") : t("common:create")}
    >
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput
          label={t("crm:contacts.fields.firstName")}
          data-autofocus
          error={message(errors.firstName?.message)}
          {...register("firstName")}
        />
        <TextInput
          label={t("crm:contacts.fields.lastName")}
          withAsterisk
          error={message(errors.lastName?.message)}
          {...register("lastName")}
        />
        <TextInput
          label={t("crm:contacts.fields.email")}
          type="email"
          error={message(errors.email?.message)}
          {...register("email")}
        />
        <TextInput
          label={t("crm:contacts.fields.title")}
          error={message(errors.title?.message)}
          {...register("title")}
        />
        <TextInput
          label={t("crm:contacts.fields.phone")}
          error={message(errors.phone?.message)}
          {...register("phone")}
        />
        <TextInput
          label={t("crm:contacts.fields.mobile")}
          error={message(errors.mobile?.message)}
          {...register("mobile")}
        />
      </SimpleGrid>
      <Controller
        control={control}
        name="accountId"
        render={({ field }) => (
          <AccountPicker
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            selectedName={contact?.accountName ?? defaultAccount?.name}
            clearable
            error={message(errors.accountId?.message)}
          />
        )}
      />
      <Controller
        control={control}
        name="ownerUserId"
        render={({ field }) => (
          <OwnerSelect
            value={field.value || null}
            onChange={(value) => field.onChange(value ?? "")}
            currentOwnerId={contact?.ownerUserId}
            currentOwnerName={contact?.ownerName}
            error={message(errors.ownerUserId?.message)}
          />
        )}
      />
      <AddressFields
        name="mailingAddress"
        legend={t("crm:contacts.fields.mailingAddress")}
        register={register}
        errors={errors}
      />
    </FormDialog>
  );
}
