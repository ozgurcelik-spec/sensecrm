import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { Select, type SelectProps } from "@mantine/core";
import { useContacts } from "@/hooks/use-contacts";

export interface PickedContact {
  id: string | null;
  name?: string;
  accountId?: string;
  accountName?: string;
}

type ContactPickerProps = Omit<
  SelectProps,
  "data" | "value" | "onChange" | "searchValue" | "onSearchChange"
> & {
  value: string | null;
  onChange: (contact: PickedContact) => void;
  /** Only this account's contacts are listed (server-side `accountId` filter). */
  accountId?: string;
  /** Name of the currently selected contact so its label shows before any search. */
  selectedName?: string;
};

/** Contact picker with server-side search (`GET /contacts?q=&accountId=`), never loads a whole tenant. */
export function ContactPicker({
  value,
  onChange,
  accountId,
  selectedName,
  ...props
}: ContactPickerProps) {
  const { t } = useTranslation(["service", "crm"]);
  const [search, setSearch] = useState("");
  const [pickedLabel, setPickedLabel] = useState<string | undefined>();
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const { data, isFetching } = useContacts({
    page: 1,
    pageSize: 20,
    q: debounced || undefined,
    accountId: accountId || undefined,
  });

  const options = useMemo(() => {
    const list = (data?.items ?? []).map((c) => ({ value: c.id, label: c.fullName }));
    if (value && !list.some((o) => o.value === value)) {
      list.unshift({ value, label: pickedLabel ?? selectedName ?? value });
    }
    return list;
  }, [data, value, selectedName, pickedLabel]);

  return (
    <Select
      label={t("service:fields.contact")}
      placeholder={t("service:contactPlaceholder")}
      nothingFoundMessage={isFetching ? t("crm:accountPicker.searching") : t("crm:noOptions")}
      data={options}
      value={value}
      onChange={(next, option) => {
        setPickedLabel(option?.label);
        const contact = (data?.items ?? []).find((c) => c.id === next);
        onChange({
          id: next,
          name: option?.label,
          accountId: contact?.accountId,
          accountName: contact?.accountName,
        });
      }}
      searchable
      filter={({ options: all }) => all}
      searchValue={search}
      onSearchChange={setSearch}
      {...props}
    />
  );
}
