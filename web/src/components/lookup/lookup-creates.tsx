/** Quick-create definitions ("+ Yeni ...") of the lookup window: permission, label and the dialog. */
import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { PERMISSIONS, type Account, type Contact, type Deal, type PriceBook, type Vendor } from "@/types";
import {
  AccountCreateDialog,
  ContactCreateDialog,
  DealCreateDialog,
  PriceBookCreateDialog,
  VendorCreateDialog,
} from "./lookup-create-dialogs";
import type { LookupCreate } from "./lookup-types";

export function useAccountLookupCreate(): LookupCreate<Account> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({ permission: PERMISSIONS.crmAccountsWrite, label: t("inventory:lookup.newAccount"), Dialog: AccountCreateDialog }),
    [t]
  );
}

export function useContactLookupCreate(): LookupCreate<Contact> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({ permission: PERMISSIONS.crmContactsWrite, label: t("inventory:lookup.newContact"), Dialog: ContactCreateDialog }),
    [t]
  );
}

export function useDealLookupCreate(): LookupCreate<Deal> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({ permission: PERMISSIONS.crmDealsWrite, label: t("inventory:lookup.newDeal"), Dialog: DealCreateDialog }),
    [t]
  );
}

export function useVendorLookupCreate(): LookupCreate<Vendor> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({ permission: PERMISSIONS.crmVendorsWrite, label: t("inventory:lookup.newVendor"), Dialog: VendorCreateDialog }),
    [t]
  );
}

export function usePriceBookLookupCreate(): LookupCreate<PriceBook> {
  const { t } = useTranslation(["inventory"]);
  return useMemo(
    () => ({
      permission: PERMISSIONS.crmPriceBooksWrite,
      label: t("inventory:lookup.newPriceBook"),
      Dialog: PriceBookCreateDialog,
    }),
    [t]
  );
}
