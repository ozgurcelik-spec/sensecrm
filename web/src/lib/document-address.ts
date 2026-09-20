/**
 * Address block of quotes, orders, invoices, purchase orders and vendors (docs/plan/m9c-envanter.md,
 * "Adres bloğu"): form values, the request payload (an empty block is left out, so the server
 * stores none) and the client-only "Adres Kopyala" moves.
 */
import type { Address, DocumentAddress } from "@/types";

/** Form shape of a block: every line is a string. */
export type DocumentAddressValues = { [K in keyof DocumentAddress]-?: string };

export const ADDRESS_FIELDS = ["country", "building", "street", "city", "state", "postalCode"] as const;
export type AddressField = (typeof ADDRESS_FIELDS)[number];

/** Server limits: `street` 200, everything else 100. */
export const ADDRESS_MAX: Record<AddressField, number> = {
  country: 100,
  building: 100,
  street: 200,
  city: 100,
  state: 100,
  postalCode: 100,
};

export const EMPTY_DOCUMENT_ADDRESS: DocumentAddressValues = {
  street: "",
  building: "",
  city: "",
  state: "",
  postalCode: "",
  country: "",
};

export function toDocumentAddressValues(address?: DocumentAddress | null): DocumentAddressValues {
  return {
    street: address?.street ?? "",
    building: address?.building ?? "",
    city: address?.city ?? "",
    state: address?.state ?? "",
    postalCode: address?.postalCode ?? "",
    country: address?.country ?? "",
  };
}

/** Sends the block only when at least one line is filled (all blank = no block = cleared on the server). */
export function toDocumentAddressPayload(
  values: DocumentAddressValues | undefined
): DocumentAddress | undefined {
  if (!values) return undefined;
  const entries = ADDRESS_FIELDS.map((key) => [key, (values[key] ?? "").trim()] as const).filter(
    ([, value]) => value !== ""
  );
  return entries.length > 0 ? (Object.fromEntries(entries) as DocumentAddress) : undefined;
}

export function isAddressBlank(values: DocumentAddressValues): boolean {
  return ADDRESS_FIELDS.every((key) => !(values[key] ?? "").trim());
}

/**
 * "Firmadan getir": the account's billing address (`street/city/state/postalCode/country`) as a billing
 * block; the account has no `building` line, so it stays empty.
 */
export function fromAccountAddress(address?: Address | null): DocumentAddressValues {
  return {
    ...EMPTY_DOCUMENT_ADDRESS,
    street: address?.street ?? "",
    city: address?.city ?? "",
    state: address?.state ?? "",
    postalCode: address?.postalCode ?? "",
    country: address?.country ?? "",
  };
}

/** Copy of a block (used by "Faturalama -> Teslimat" and back). */
export function copyAddress(values: DocumentAddressValues): DocumentAddressValues {
  return { ...values };
}
