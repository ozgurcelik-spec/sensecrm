import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Select, type SelectProps } from "@mantine/core";
import { useMembers } from "@/hooks/use-organization-queries";
import type { ActiveMember } from "@/types";

/** Active organization members as Select options (value = user id). */
function useOwnerOptions(currentOwnerId?: string, currentOwnerName?: string) {
  const members = useMembers();
  const options = useMemo(() => {
    const list = (members.data ?? [])
      // Pending invitations have no account in this organization yet: never selectable as owner.
      .filter((m): m is ActiveMember => m.status !== "pending")
      .filter((m) => m.isActive || m.userId === currentOwnerId)
      .map((m) => ({ value: m.userId, label: m.displayName }));
    // An inactive/unknown current owner must still be displayed on the edit form.
    if (currentOwnerId && !list.some((o) => o.value === currentOwnerId)) {
      list.unshift({ value: currentOwnerId, label: currentOwnerName ?? currentOwnerId });
    }
    return list;
  }, [members.data, currentOwnerId, currentOwnerName]);
  return { options, isLoading: members.isLoading, isError: members.isError };
}

type OwnerSelectProps = Omit<SelectProps, "data"> & {
  currentOwnerId?: string;
  currentOwnerName?: string;
};

/** Owner picker (record owner or list filter), fed by `GET /organization/members`. */
export function OwnerSelect({ currentOwnerId, currentOwnerName, ...props }: OwnerSelectProps) {
  const { t } = useTranslation(["crm"]);
  const { options, isLoading } = useOwnerOptions(currentOwnerId, currentOwnerName);
  return (
    <Select
      label={t("crm:owner")}
      placeholder={t("crm:ownerPlaceholder")}
      data={options}
      searchable
      nothingFoundMessage={t("crm:noOptions")}
      disabled={isLoading || props.disabled}
      {...props}
    />
  );
}
