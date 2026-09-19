import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Text } from "@mantine/core";
import { Megaphone } from "lucide-react";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import type { MemberType } from "@/types";
import { AddToCampaignDialog } from "./add-to-campaign-dialog";

interface BulkAddToCampaignProps {
  memberType: MemberType;
  /** Selected lead / contact ids of the current page. */
  selectedIds: string[];
  /** Called after the records were added (the caller clears its selection). */
  onDone: () => void;
}

/**
 * Bulk action bar of the leads / contacts lists: shown while rows are selected and only for users
 * with `crm.campaigns.write`; opens the "add to campaign" dialog.
 */
export function BulkAddToCampaign({ memberType, selectedIds, onDone }: BulkAddToCampaignProps) {
  const { t } = useTranslation(["campaigns"]);
  const { canWriteCampaigns } = useCrmPermissions();
  const [open, setOpen] = useState(false);
  if (!canWriteCampaigns || selectedIds.length === 0) return null;

  return (
    <>
      <Group gap="sm" data-testid="bulk-bar">
        <Text size="sm" fw={500}>
          {t("campaigns:addToCampaign.summary", { count: selectedIds.length })}
        </Text>
        <Button
          size="xs"
          variant="default"
          leftSection={<Megaphone size={14} />}
          onClick={() => setOpen(true)}
        >
          {t("campaigns:addToCampaign.action")}
        </Button>
      </Group>
      {open && (
        <AddToCampaignDialog
          memberType={memberType}
          memberIds={selectedIds}
          onAdded={onDone}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}
