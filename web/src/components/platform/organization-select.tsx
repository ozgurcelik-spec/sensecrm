import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Select } from "@mantine/core";
import { useDebouncedValue } from "@mantine/hooks";
import { usePlatformOrganization, usePlatformOrganizations } from "@/hooks/use-platform";

interface OrganizationSelectProps {
  value: string | null;
  onChange: (tenantId: string | null) => void;
  label?: string;
  placeholder?: string;
  w?: number | string;
}

/** Searchable organization picker: the organizations are searched on the server (`q`), never all loaded. */
export function OrganizationSelect({
  value,
  onChange,
  label,
  placeholder,
  w = 260,
}: OrganizationSelectProps) {
  const { t } = useTranslation(["platform"]);
  const [search, setSearch] = useState("");
  const [debounced] = useDebouncedValue(search, 250);
  const found = usePlatformOrganizations({ page: 1, pageSize: 20, q: debounced || undefined, sort: "name" });
  const selected = usePlatformOrganization(value ?? undefined);

  const options = new Map<string, string>();
  for (const org of found.data?.items ?? []) options.set(org.tenantId, org.name);
  // The chosen organization must stay an option even when the current search does not list it.
  if (value && !options.has(value)) options.set(value, selected.data?.name ?? value);

  return (
    <Select
      label={label}
      aria-label={label ?? placeholder ?? t("platform:audit.filters.tenant")}
      placeholder={placeholder ?? t("platform:audit.filters.tenant")}
      w={w}
      searchable
      clearable
      nothingFoundMessage={t("platform:organizations.empty")}
      data={[...options].map(([id, name]) => ({ value: id, label: name }))}
      value={value}
      onChange={onChange}
      searchValue={search}
      onSearchChange={setSearch}
      filter={({ options: all }) => all}
    />
  );
}
