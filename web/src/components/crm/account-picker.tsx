import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { Select, type SelectProps } from "@mantine/core";
import { useAccounts } from "@/hooks/use-accounts";

type AccountPickerProps = Omit<SelectProps, "data" | "searchValue" | "onSearchChange"> & {
  /** Name of the currently selected account so its label shows before/without a search. */
  selectedName?: string;
};

/**
 * Account picker with server-side search (`GET /accounts?q=`): the list is never loaded in full, so
 * it scales to large tenants. Client-side filtering is disabled; the server decides what matches.
 */
export function AccountPicker({ selectedName, value, onChange, ...props }: AccountPickerProps) {
  const { t } = useTranslation(["crm"]);
  const [search, setSearch] = useState("");
  // Label of the account picked in this session, so it survives the option list changing under new searches.
  const [pickedLabel, setPickedLabel] = useState<string | undefined>();
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const { data, isFetching } = useAccounts({ page: 1, pageSize: 20, q: debounced || undefined });

  const options = useMemo(() => {
    const list = (data?.items ?? []).map((a) => ({ value: a.id, label: a.name }));
    if (value && !list.some((o) => o.value === value)) {
      list.unshift({ value, label: pickedLabel ?? selectedName ?? value });
    }
    return list;
  }, [data, value, selectedName, pickedLabel]);

  return (
    <Select
      label={t("crm:accountPicker.label")}
      placeholder={t("crm:accountPicker.placeholder")}
      nothingFoundMessage={isFetching ? t("crm:accountPicker.searching") : t("crm:noOptions")}
      data={options}
      value={value}
      onChange={(next, option) => {
        setPickedLabel(option?.label);
        onChange?.(next, option);
      }}
      searchable
      filter={({ options: all }) => all}
      searchValue={search}
      onSearchChange={setSearch}
      {...props}
    />
  );
}
