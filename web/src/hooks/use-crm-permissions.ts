import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";

/** Write permissions of the sales modules, used to show/hide the "New", edit, delete and convert actions. */
export function useCrmPermissions() {
  const accounts = usePermission(PERMISSIONS.crmAccountsWrite);
  const contacts = usePermission(PERMISSIONS.crmContactsWrite);
  const leads = usePermission(PERMISSIONS.crmLeadsWrite);
  const deals = usePermission(PERMISSIONS.crmDealsWrite);
  const settings = usePermission(PERMISSIONS.orgSettingsManage);
  return {
    canWriteAccounts: accounts,
    canWriteContacts: contacts,
    canWriteLeads: leads,
    canWriteDeals: deals,
    /** Converting creates an account and a contact (and optionally a deal), so it needs all of them. */
    canConvertLeads: leads && accounts && contacts,
    canManagePipelines: settings,
  };
}
