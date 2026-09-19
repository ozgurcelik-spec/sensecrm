import { useTranslation } from "react-i18next";
import { AccountPicker } from "@/components/crm/account-picker";
import { useAccount } from "@/hooks/use-accounts";
import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";

interface AccountFilterProps {
  value: string;
  onChange: (accountId: string | null) => void;
}

/** Account filter of the quote / order lists. Searching accounts needs `crm.accounts.read`; without it the filter is not shown. */
export function AccountFilter({ value, onChange }: AccountFilterProps) {
  const { t } = useTranslation(["commerce"]);
  const canRead = usePermission(PERMISSIONS.crmAccountsRead);
  // The URL only carries the id: look the name up so the picker shows it after a reload.
  const account = useAccount(canRead && value ? value : undefined);
  if (!canRead) return null;
  return (
    <AccountPicker
      label={undefined}
      aria-label={t("commerce:fields.account")}
      placeholder={t("commerce:fields.account")}
      w={240}
      clearable
      value={value || null}
      selectedName={account.data?.name}
      onChange={(next) => onChange(next)}
    />
  );
}
