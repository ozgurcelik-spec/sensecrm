import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";

/** Write permissions of the sales modules and activities, used to show/hide the "New", edit, delete and convert actions. */
export function useCrmPermissions() {
  const accounts = usePermission(PERMISSIONS.crmAccountsWrite);
  const contacts = usePermission(PERMISSIONS.crmContactsWrite);
  const leads = usePermission(PERMISSIONS.crmLeadsWrite);
  const deals = usePermission(PERMISSIONS.crmDealsWrite);
  const activities = usePermission(PERMISSIONS.crmActivitiesWrite);
  const settings = usePermission(PERMISSIONS.orgSettingsManage);
  const products = usePermission(PERMISSIONS.crmProductsWrite);
  const quotes = usePermission(PERMISSIONS.crmQuotesWrite);
  const orders = usePermission(PERMISSIONS.crmOrdersWrite);
  return {
    canWriteProducts: products,
    canWriteQuotes: quotes,
    canWriteOrders: orders,
    canWriteAccounts: accounts,
    canWriteContacts: contacts,
    canWriteLeads: leads,
    canWriteDeals: deals,
    canWriteActivities: activities,
    /** Converting creates an account and a contact (and optionally a deal), so it needs all of them. */
    canConvertLeads: leads && accounts && contacts,
    canManagePipelines: settings,
  };
}
