import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { Select, SimpleGrid, TextInput } from "@mantine/core";
import { useAccounts } from "@/hooks/use-accounts";
import { useContacts } from "@/hooks/use-contacts";
import { useDeals } from "@/hooks/use-deals";
import { useLeads } from "@/hooks/use-leads";
import { usePermission } from "@/hooks/use-permission";
import { RELATED_READ_PERMISSION } from "@/lib/activity";
import { RELATED_TYPES, type RelatedType } from "@/types";

export interface RelatedValue {
  type: string;
  id: string;
  name?: string;
}

interface RelatedRecordPickerProps {
  value: RelatedValue;
  onChange: (value: RelatedValue) => void;
  /** The record is fixed (quick-add from a detail page): shown read-only. */
  locked?: boolean;
  recordError?: string;
}

/** Search results of the chosen record type through the existing list endpoints (`?q=`, 20 rows). */
function useRelatedOptions(type: string, q: string | undefined) {
  const canAccounts = usePermission(RELATED_READ_PERMISSION.account);
  const canContacts = usePermission(RELATED_READ_PERMISSION.contact);
  const canLeads = usePermission(RELATED_READ_PERMISSION.lead);
  const canDeals = usePermission(RELATED_READ_PERMISSION.deal);
  const query = { page: 1, pageSize: 20, q };
  const accounts = useAccounts(query, type === "account" && canAccounts);
  const contacts = useContacts(query, type === "contact" && canContacts);
  const leads = useLeads(query, type === "lead" && canLeads);
  const deals = useDeals(query, type === "deal" && canDeals);

  const active = { account: accounts, contact: contacts, lead: leads, deal: deals }[
    type as RelatedType
  ];
  const options = useMemo(() => {
    switch (type) {
      case "account":
        return (accounts.data?.items ?? []).map((r) => ({ value: r.id, label: r.name }));
      case "contact":
        return (contacts.data?.items ?? []).map((r) => ({ value: r.id, label: r.fullName }));
      case "lead":
        return (leads.data?.items ?? []).map((r) => ({ value: r.id, label: r.fullName }));
      case "deal":
        return (deals.data?.items ?? []).map((r) => ({ value: r.id, label: r.name }));
      default:
        return [];
    }
  }, [type, accounts.data, contacts.data, leads.data, deals.data]);
  return { options, isFetching: !!active?.isFetching };
}

/**
 * Related record: a type select plus an async search of that type (never loads a whole module).
 * Types the user cannot read are not offered.
 */
export function RelatedRecordPicker({
  value,
  onChange,
  locked = false,
  recordError,
}: RelatedRecordPickerProps) {
  const { t } = useTranslation(["activities", "crm"]);
  const canAccounts = usePermission(RELATED_READ_PERMISSION.account);
  const canContacts = usePermission(RELATED_READ_PERMISSION.contact);
  const canLeads = usePermission(RELATED_READ_PERMISSION.lead);
  const canDeals = usePermission(RELATED_READ_PERMISSION.deal);
  const allowed: Record<RelatedType, boolean> = {
    account: canAccounts,
    contact: canContacts,
    lead: canLeads,
    deal: canDeals,
  };

  const [search, setSearch] = useState("");
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const { options: found, isFetching } = useRelatedOptions(value.type, debounced || undefined);

  const options = useMemo(() => {
    const list = [...found];
    // The current selection stays selectable/displayed even when a new search does not return it.
    if (value.id && !list.some((o) => o.value === value.id)) {
      list.unshift({ value: value.id, label: value.name ?? value.id });
    }
    return list;
  }, [found, value.id, value.name]);

  if (locked) {
    const typeLabel = value.type ? t(`activities:relatedTypes.${value.type}`) : "";
    return (
      <TextInput
        label={t("activities:fields.related")}
        value={t("activities:relatedPicker.locked", { type: typeLabel, name: value.name ?? "" })}
        readOnly
        disabled
      />
    );
  }

  const typeOptions = RELATED_TYPES.filter((type) => allowed[type] || type === value.type).map(
    (type) => ({ value: type, label: t(`activities:relatedTypes.${type}`) })
  );

  return (
    <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
      <Select
        label={t("activities:fields.relatedType")}
        placeholder={t("activities:relatedPicker.typePlaceholder")}
        data={typeOptions}
        value={value.type || null}
        clearable
        onChange={(next) => {
          setSearch("");
          onChange({ type: next ?? "", id: "", name: undefined });
        }}
      />
      <Select
        label={t("activities:fields.relatedRecord")}
        placeholder={t("activities:relatedPicker.recordPlaceholder")}
        disabled={!value.type}
        data={options}
        value={value.id || null}
        onChange={(next, option) =>
          onChange({ type: value.type, id: next ?? "", name: option?.label })
        }
        searchable
        clearable
        nothingFoundMessage={
          isFetching ? t("activities:relatedPicker.searching") : t("crm:noOptions")
        }
        filter={({ options: all }) => all}
        searchValue={search}
        onSearchChange={setSearch}
        error={recordError}
      />
    </SimpleGrid>
  );
}
