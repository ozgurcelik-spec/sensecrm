import { useSearchParams } from "react-router";
import { useAccount } from "@/hooks/use-accounts";
import { useDeal } from "@/hooks/use-deals";
import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";
import type { EditorPrefill } from "./document-editor";

/**
 * Prefill of a new quote / order from the URL (`?accountId=&contactId=&dealId=`): only ids travel in
 * the URL, names, currency and (from a deal) the subject are looked up. `ready` turns true once the
 * lookups finished (a failed lookup, for example a missing read permission, still counts: the ids
 * alone are enough to create the document).
 */
export function useEditorPrefill(): { ready: boolean; prefill: EditorPrefill } {
  const [searchParams] = useSearchParams();
  const accountId = searchParams.get("accountId") || undefined;
  const contactId = searchParams.get("contactId") || undefined;
  const dealId = searchParams.get("dealId") || undefined;

  // The lookups need the read permissions; without them the ids alone prefill the form.
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);

  const deal = useDeal(canReadDeals ? dealId : undefined);
  const dealSettled = !dealId || !canReadDeals || deal.isSuccess || deal.isError;
  // The deal already names its account; ask for the account itself only when it is not known that way.
  const needsAccountLookup =
    canReadAccounts && !!accountId && (!dealId || !deal.data || deal.data.accountId !== accountId);
  const account = useAccount(needsAccountLookup && dealSettled ? accountId : undefined);
  const accountSettled = !needsAccountLookup || (dealSettled && (account.isSuccess || account.isError));

  const dealData = deal.data;
  const resolvedAccountId = accountId ?? dealData?.accountId;
  const accountName =
    dealData && dealData.accountId === resolvedAccountId ? dealData.accountName : account.data?.name;

  return {
    ready: dealSettled && accountSettled,
    prefill: {
      accountId: resolvedAccountId,
      accountName,
      contactId: contactId ?? dealData?.contactId,
      contactName: dealData?.contactName,
      dealId,
      dealName: dealData?.name,
      currency: dealData?.currency,
      subject: dealData?.name,
    },
  };
}
