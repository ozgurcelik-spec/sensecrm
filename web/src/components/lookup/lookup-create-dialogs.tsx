/**
 * Quick-create dialogs of the lookup window's "+ Yeni ..." button: each wraps an existing form dialog
 * (which reports only the saved id) and hands the saved row back, so the window selects it at once.
 */
import { AccountFormDialog } from "@/components/crm/account-form-dialog";
import { ContactFormDialog } from "@/components/crm/contact-form-dialog";
import { DealFormDialog } from "@/components/crm/deal-form-dialog";
import { PriceBookFormDialog } from "@/components/commerce/pricebook-form-dialog";
import { VendorFormDialog } from "@/components/commerce/vendor-form-dialog";
import { useAccount } from "@/hooks/use-accounts";
import { toastApiError } from "@/hooks/use-toast";
import { getAccount } from "@/services/accounts.service";
import { getContact } from "@/services/contacts.service";
import { getDeal } from "@/services/deals.service";
import { getPriceBook } from "@/services/pricebooks.service";
import { getVendor } from "@/services/vendors.service";
import type { Account, Contact, Deal, PriceBook, Vendor } from "@/types";
import type { LookupCreateDialogProps } from "./lookup-types";

/** Loads the saved record and reports it; a failed load is toasted (the record exists, only the pick is lost). */
function report<T>(load: Promise<T>, onCreated: (row: T) => void) {
  load.then(onCreated).catch((error: unknown) => toastApiError(error));
}

export function AccountCreateDialog({ opened, onClose, initialName, onCreated }: LookupCreateDialogProps<Account>) {
  if (!opened) return null;
  return (
    <AccountFormDialog
      initialName={initialName}
      onClose={onClose}
      onSaved={(id) => report(getAccount(id), onCreated)}
    />
  );
}

export function ContactCreateDialog({
  opened,
  onClose,
  initialName,
  presetFilters,
  onCreated,
}: LookupCreateDialogProps<Contact>) {
  const accountId = presetFilters?.accountId;
  const account = useAccount(opened ? accountId : undefined);
  if (!opened) return null;
  return (
    <ContactFormDialog
      initialName={initialName}
      defaultAccount={accountId ? { id: accountId, name: account.data?.name ?? accountId } : undefined}
      onClose={onClose}
      onSaved={(id) => report(getContact(id), onCreated)}
    />
  );
}

export function DealCreateDialog({
  opened,
  onClose,
  initialName,
  presetFilters,
  onCreated,
}: LookupCreateDialogProps<Deal>) {
  const accountId = presetFilters?.accountId;
  const account = useAccount(opened ? accountId : undefined);
  if (!opened) return null;
  return (
    <DealFormDialog
      initialName={initialName}
      defaultAccount={accountId ? { id: accountId, name: account.data?.name ?? accountId } : undefined}
      onClose={onClose}
      onSaved={(id) => report(getDeal(id), onCreated)}
    />
  );
}

export function VendorCreateDialog({ opened, onClose, initialName, onCreated }: LookupCreateDialogProps<Vendor>) {
  if (!opened) return null;
  return (
    <VendorFormDialog
      initialName={initialName}
      onClose={onClose}
      onSaved={(id) => report(getVendor(id), onCreated)}
    />
  );
}

export function PriceBookCreateDialog({ opened, onClose, initialName, onCreated }: LookupCreateDialogProps<PriceBook>) {
  if (!opened) return null;
  return (
    <PriceBookFormDialog
      initialName={initialName}
      onClose={onClose}
      onSaved={(id) => report(getPriceBook(id), onCreated)}
    />
  );
}
