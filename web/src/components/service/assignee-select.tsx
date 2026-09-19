import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Select, type SelectProps } from "@mantine/core";
import { useMembers } from "@/hooks/use-organization-queries";

/** Option value of "Unassigned" (a Select has no empty option value). */
export const UNASSIGNED = "__unassigned__";

type AssigneeSelectProps = Omit<SelectProps, "data" | "value" | "onChange"> & {
  /** User id; null = unassigned. */
  value: string | null;
  onChange: (userId: string | null) => void;
  /** Current assignee shown even if that member is inactive. */
  currentUserName?: string;
};

/** Case assignee picker: active members plus an explicit "Unassigned" (agent queue) option. */
export function AssigneeSelect({
  value,
  onChange,
  currentUserName,
  ...props
}: AssigneeSelectProps) {
  const { t } = useTranslation(["service", "crm"]);
  const members = useMembers();
  const options = useMemo(() => {
    const list = (members.data ?? [])
      .filter((m) => m.isActive || m.userId === value)
      .map((m) => ({ value: m.userId, label: m.displayName }));
    if (value && !list.some((o) => o.value === value)) {
      list.unshift({ value, label: currentUserName ?? value });
    }
    return [{ value: UNASSIGNED, label: t("service:unassigned") }, ...list];
  }, [members.data, value, currentUserName, t]);

  return (
    <Select
      data={options}
      value={value ?? UNASSIGNED}
      onChange={(next) => onChange(!next || next === UNASSIGNED ? null : next)}
      allowDeselect={false}
      searchable
      nothingFoundMessage={t("crm:noOptions")}
      disabled={members.isLoading || props.disabled}
      {...props}
    />
  );
}
