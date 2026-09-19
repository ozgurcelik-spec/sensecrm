import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useDebouncedValue } from "@mantine/hooks";
import { MultiSelect, Select } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { useAddCampaignMembers } from "@/hooks/use-campaigns";
import { useContacts } from "@/hooks/use-contacts";
import { useLeads } from "@/hooks/use-leads";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { summarizeAddResult } from "@/lib/campaign";
import { PERMISSIONS, type Campaign, type MemberType } from "@/types";

/** The server accepts 1-500 ids per call. */
const MAX_MEMBERS = 500;

/** Search results (20 rows) of the chosen record type through the existing list endpoints. */
function useRecordOptions(type: MemberType, q: string | undefined) {
  const canLeads = usePermission(PERMISSIONS.crmLeadsRead);
  const canContacts = usePermission(PERMISSIONS.crmContactsRead);
  const query = { page: 1, pageSize: 20, q };
  const leads = useLeads(query, type === "lead" && canLeads);
  const contacts = useContacts(query, type === "contact" && canContacts);
  const active = type === "lead" ? leads : contacts;
  const options = useMemo(
    () =>
      (type === "lead" ? leads.data?.items : contacts.data?.items)?.map((r) => ({
        value: r.id,
        label: r.fullName,
      })) ?? [],
    [type, leads.data, contacts.data]
  );
  return { options, isFetching: active.isFetching };
}

/** "Add members" of a campaign: pick lead or contact, then search and multi-select records. */
export function AddMembersDialog({
  campaign,
  onClose,
}: {
  campaign: Campaign;
  onClose: () => void;
}) {
  const { t } = useTranslation(["campaigns", "common", "crm"]);
  const canLeads = usePermission(PERMISSIONS.crmLeadsRead);
  const canContacts = usePermission(PERMISSIONS.crmContactsRead);
  const add = useAddCampaignMembers();
  const allowedTypes = (["lead", "contact"] as const).filter((type) =>
    type === "lead" ? canLeads : canContacts
  );
  const [type, setType] = useState<MemberType>(allowedTypes[0] ?? "lead");
  const [picked, setPicked] = useState<Record<string, string>>({});
  const [search, setSearch] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const { options: found, isFetching } = useRecordOptions(type, debounced || undefined);

  // Chosen records stay listed (and labelled) even when a new search does not return them.
  const options = useMemo(() => {
    const list = [...found];
    for (const [value, label] of Object.entries(picked)) {
      if (!list.some((o) => o.value === value)) list.push({ value, label });
    }
    return list;
  }, [found, picked]);

  function change(values: string[]) {
    const next: Record<string, string> = {};
    for (const value of values) {
      const label = picked[value] ?? found.find((o) => o.value === value)?.label;
      next[value] = label ?? value;
    }
    setPicked(next);
    setError(undefined);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const memberIds = Object.keys(picked);
    if (memberIds.length === 0) {
      setError(t("campaigns:addMembers.noneSelected"));
      return;
    }
    try {
      const result = await add.mutateAsync({ campaignId: campaign.id, memberType: type, memberIds });
      toast({
        variant: result.addedCount > 0 ? "success" : "default",
        description: summarizeAddResult(result, t),
      });
      onClose();
    } catch (err) {
      toastApiError(err);
    }
  }

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={t("campaigns:addMembers.title")}
      onSubmit={(event) => void submit(event)}
      loading={add.isPending}
      submitLabel={t("campaigns:addMembers.submit")}
      size="md"
    >
      <Select
        label={t("campaigns:addMembers.type")}
        data={allowedTypes.map((v) => ({ value: v, label: t(`campaigns:memberTypes.${v}`) }))}
        value={type}
        onChange={(value) => {
          setType((value as MemberType | null) ?? type);
          // Ids of the other type are meaningless here.
          setPicked({});
          setSearch("");
        }}
        allowDeselect={false}
      />
      <MultiSelect
        label={t("campaigns:addMembers.records")}
        placeholder={t("campaigns:addMembers.placeholder")}
        data={options}
        value={Object.keys(picked)}
        onChange={change}
        searchable
        clearable
        maxValues={MAX_MEMBERS}
        nothingFoundMessage={
          isFetching ? t("campaigns:addMembers.searching") : t("crm:noOptions")
        }
        filter={({ options: all }) => all}
        searchValue={search}
        onSearchChange={setSearch}
        error={error}
        data-autofocus
      />
    </FormDialog>
  );
}
