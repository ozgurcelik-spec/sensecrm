import { useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { Loader, NavLink, Popover, Stack, Text, TextInput } from "@mantine/core";
import { Search } from "lucide-react";
import { useAccounts } from "@/hooks/use-accounts";
import { useContacts } from "@/hooks/use-contacts";
import { useDeals } from "@/hooks/use-deals";
import { useLeads } from "@/hooks/use-leads";
import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";

const MIN_QUERY_LENGTH = 2;
const DEBOUNCE_MS = 250;
const RESULT_LIMIT = 5;

interface Hit {
  id: string;
  label: string;
  hint?: string;
}

function Group({
  title,
  hits,
  path,
  onPick,
}: {
  title: string;
  hits: Hit[];
  path: string;
  onPick: () => void;
}) {
  if (hits.length === 0) return null;
  return (
    <div>
      <Text size="xs" fw={600} tt="uppercase" c="dimmed" px="xs" mb={2}>
        {title}
      </Text>
      {hits.map((hit) => (
        <NavLink
          key={hit.id}
          component={Link}
          to={`${path}/${hit.id}`}
          label={hit.label}
          description={hit.hint}
          onClick={onPick}
        />
      ))}
    </div>
  );
}

/** Header search across leads, contacts, accounts and deals; each group only for what the user may read. */
export function GlobalSearch() {
  const { t } = useTranslation(["common", "navigation"]);
  const navigate = useNavigate();
  const [text, setText] = useState("");
  const [focused, setFocused] = useState(false);
  const [debounced] = useDebouncedValue(text.trim(), DEBOUNCE_MS);
  const active = debounced.length >= MIN_QUERY_LENGTH;

  const query = { q: debounced, page: 1, pageSize: RESULT_LIMIT };
  const canLeads = usePermission(PERMISSIONS.crmLeadsRead);
  const canContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canDeals = usePermission(PERMISSIONS.crmDealsRead);
  const leads = useLeads(query, active && canLeads);
  const contacts = useContacts(query, active && canContacts);
  const accounts = useAccounts(query, active && canAccounts);
  const deals = useDeals(query, active && canDeals);

  const groups = [
    {
      key: "leads",
      hits: (leads.data?.items ?? []).map((l) => ({
        id: l.id,
        label: l.fullName,
        hint: l.company,
      })),
    },
    {
      key: "contacts",
      hits: (contacts.data?.items ?? []).map((c) => ({
        id: c.id,
        label: c.fullName,
        hint: c.accountName,
      })),
    },
    {
      key: "accounts",
      hits: (accounts.data?.items ?? []).map((a) => ({ id: a.id, label: a.name })),
    },
    {
      key: "deals",
      hits: (deals.data?.items ?? []).map((d) => ({
        id: d.id,
        label: d.name,
        hint: d.accountName,
      })),
    },
  ] as const;
  const loading = [leads, contacts, accounts, deals].some((r) => r.isFetching);
  const empty = active && !loading && groups.every((g) => g.hits.length === 0);
  const close = () => {
    setText("");
    setFocused(false);
  };

  let rightSection: ReactNode = null;
  if (active && loading) rightSection = <Loader size={14} />;

  return (
    <Popover
      opened={focused && active}
      position="bottom-start"
      width="target"
      shadow="md"
      withinPortal
    >
      <Popover.Target>
        <TextInput
          value={text}
          onChange={(event) => setText(event.currentTarget.value)}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          onKeyDown={(event) => {
            if (event.key === "Escape") close();
            if (event.key === "Enter" && !active) void navigate("/app/leads");
          }}
          leftSection={<Search size={16} />}
          rightSection={rightSection}
          placeholder={t("common:shell.searchPlaceholder")}
          aria-label={t("common:shell.search")}
          radius="xl"
          w="100%"
        />
      </Popover.Target>
      {/* mousedown must not blur the input, or the dropdown closes before the click lands. */}
      <Popover.Dropdown p="xs" onMouseDown={(event) => event.preventDefault()}>
        <Stack gap="xs">
          {groups.map((g) => (
            <Group
              key={g.key}
              title={t(`navigation:${g.key}`)}
              hits={g.hits}
              path={`/app/${g.key}`}
              onPick={close}
            />
          ))}
          {empty && (
            <Text size="sm" c="dimmed" ta="center" py="sm">
              {t("common:shell.noResults")}
            </Text>
          )}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
