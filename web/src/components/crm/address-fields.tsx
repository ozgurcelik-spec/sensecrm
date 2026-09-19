import { useTranslation } from "react-i18next";
import type { FieldErrors, UseFormRegisterReturn } from "react-hook-form";
import { Fieldset, SimpleGrid, TextInput } from "@mantine/core";
import type { Address } from "@/types";

interface AddressFieldsProps<N extends "billingAddress" | "mailingAddress"> {
  /** Form field holding the address. */
  name: N;
  legend: string;
  register: (name: `${N}.${keyof Address}`) => UseFormRegisterReturn;
  errors: FieldErrors;
}

export function AddressFields<N extends "billingAddress" | "mailingAddress">({
  name,
  legend,
  register,
  errors,
}: AddressFieldsProps<N>) {
  const { t } = useTranslation(["crm"]);
  const nested = errors[name] as Record<string, { message?: string }> | undefined;
  const field = (key: keyof Address) => ({
    ...register(`${name}.${key}`),
    error: nested?.[key]?.message,
  });
  return (
    <Fieldset legend={legend}>
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
        <TextInput label={t("crm:address.street")} {...field("street")} />
        <TextInput label={t("crm:address.city")} {...field("city")} />
        <TextInput label={t("crm:address.state")} {...field("state")} />
        <TextInput label={t("crm:address.postalCode")} {...field("postalCode")} />
        <TextInput label={t("crm:address.country")} {...field("country")} />
      </SimpleGrid>
    </Fieldset>
  );
}
