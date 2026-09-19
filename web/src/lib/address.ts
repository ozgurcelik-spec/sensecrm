import type { Address } from "@/types";

/** Form shape of a nested address (`billingAddress` / `mailingAddress`): every line is a string. */
export type AddressValues = { [K in keyof Address]-?: string };

export const EMPTY_ADDRESS: AddressValues = {
  street: "",
  city: "",
  state: "",
  postalCode: "",
  country: "",
};

export function toAddressValues(address?: Address): AddressValues {
  return {
    street: address?.street ?? "",
    city: address?.city ?? "",
    state: address?.state ?? "",
    postalCode: address?.postalCode ?? "",
    country: address?.country ?? "",
  };
}

/** Sends the address only when at least one line is filled (no lines = no address). */
export function toAddressPayload(values: AddressValues | undefined): Address | undefined {
  if (!values) return undefined;
  const entries = Object.entries(values)
    .map(([key, value]) => [key, value.trim()] as const)
    .filter(([, value]) => value !== "");
  return entries.length > 0 ? (Object.fromEntries(entries) as Address) : undefined;
}
