import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { useDebouncedValue } from "@mantine/hooks";
import { Select, Text } from "@mantine/core";
import { FormDialog } from "@/components/crm/form-dialog";
import { useAddCampaignMembers, useCampaigns } from "@/hooks/use-campaigns";
import { toast, toastApiError } from "@/hooks/use-toast";
import { summarizeAddResult } from "@/lib/campaign";
import { campaignKeys } from "@/services/campaigns.service";
import type { AddMembersResult, MemberType } from "@/types";

interface AddToCampaignDialogProps {
  memberType: MemberType;
  /** Lead / contact ids (not membership ids). */
  memberIds: string[];
  /** Called after a successful add with the server's summary. */
  onAdded?: (result: AddMembersResult) => void;
  onClose: () => void;
}

/**
 * Adds the given leads or contacts to one campaign. Only planned and active campaigns are offered
 * (a closed one would answer `campaign.closed`), found through a server-side search.
 */
export function AddToCampaignDialog({
  memberType,
  memberIds,
  onAdded,
  onClose,
}: AddToCampaignDialogProps) {
  const { t } = useTranslation(["campaigns", "common"]);
  const queryClient = useQueryClient();
  const add = useAddCampaignMembers();
  const [campaignId, setCampaignId] = useState<string | null>(null);
  const [campaignName, setCampaignName] = useState<string | undefined>();
  const [search, setSearch] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const found = useCampaigns({
    page: 1,
    pageSize: 20,
    q: debounced || undefined,
    status: "planned,active",
  });

  const options = useMemo(() => {
    const list = (found.data?.items ?? []).map((c) => ({ value: c.id, label: c.name }));
    if (campaignId && !list.some((o) => o.value === campaignId)) {
      list.unshift({ value: campaignId, label: campaignName ?? campaignId });
    }
    return list;
  }, [found.data, campaignId, campaignName]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!campaignId) {
      setError(t("campaigns:addToCampaign.campaignRequired"));
      return;
    }
    try {
      const result = await add.mutateAsync({ campaignId, memberType, memberIds });
      toast({
        variant: result.addedCount > 0 ? "success" : "default",
        description: summarizeAddResult(result, t),
      });
      onAdded?.(result);
      onClose();
    } catch (err) {
      toastApiError(err);
      // A closed campaign (a race with another user) must disappear from the options.
      void queryClient.invalidateQueries({ queryKey: campaignKeys.all });
    }
  }

  return (
    <FormDialog
      opened
      onClose={onClose}
      title={t("campaigns:addToCampaign.title")}
      onSubmit={(event) => void submit(event)}
      loading={add.isPending}
      submitLabel={t("campaigns:addToCampaign.submit")}
      size="md"
    >
      <Text size="sm" c="dimmed">
        {t("campaigns:addToCampaign.summary", { count: memberIds.length })}
      </Text>
      <Select
        label={t("campaigns:addToCampaign.campaign")}
        placeholder={t("campaigns:addToCampaign.placeholder")}
        withAsterisk
        data={options}
        value={campaignId}
        onChange={(value, option) => {
          setCampaignId(value);
          setCampaignName(option?.label);
          setError(undefined);
        }}
        searchable
        clearable
        nothingFoundMessage={
          found.isFetching
            ? t("campaigns:addToCampaign.searching")
            : t("campaigns:addToCampaign.noCampaigns")
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
