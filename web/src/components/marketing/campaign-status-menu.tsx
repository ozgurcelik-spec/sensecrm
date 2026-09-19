import { useTranslation } from "react-i18next";
import { Button, Menu } from "@mantine/core";
import { ChevronDown } from "lucide-react";
import { useSetCampaignStatus } from "@/hooks/use-campaigns";
import { toast, toastApiError } from "@/hooks/use-toast";
import { CAMPAIGN_TRANSITIONS, type Campaign, type CampaignStatus } from "@/types";

/** Label key (`campaigns:statusMenu.<key>`) of moving from one status to another. */
function actionKey(from: CampaignStatus, to: CampaignStatus): string {
  if (to === "cancelled") return "cancel";
  if (to === "completed") return "complete";
  if (to === "planned") return "replan";
  return from === "planned" ? "start" : "reopen";
}

/**
 * Status menu of a campaign: offers only the targets the transition table allows from the current
 * status. A 409 from the server (a stale view) becomes an error toast and the data is refetched.
 */
export function CampaignStatusMenu({ campaign }: { campaign: Campaign }) {
  const { t } = useTranslation(["campaigns"]);
  const setStatus = useSetCampaignStatus();
  const targets = CAMPAIGN_TRANSITIONS[campaign.status];

  async function change(status: CampaignStatus) {
    try {
      await setStatus.mutateAsync({ id: campaign.id, status });
      toast({ variant: "success", description: t("campaigns:statusMenu.changed") });
    } catch (error) {
      toastApiError(error);
    }
  }

  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <Button
          variant="default"
          rightSection={<ChevronDown size={16} />}
          loading={setStatus.isPending}
        >
          {t("campaigns:statusMenu.label")}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        {targets.map((status) => (
          <Menu.Item
            key={status}
            color={status === "cancelled" ? "red" : undefined}
            onClick={() => void change(status)}
          >
            {t(`campaigns:statusMenu.${actionKey(campaign.status, status)}`)}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
