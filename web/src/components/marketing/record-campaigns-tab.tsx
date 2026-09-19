import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Group, Skeleton, Stack, Table, Text } from "@mantine/core";
import { Plus } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { RowActions } from "@/components/crm/list-page-frame";
import { LoadError } from "@/components/load-error";
import { useCampaignsByMember, useRemoveCampaignMembers } from "@/hooks/use-campaigns";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { MemberType, RecordCampaignMembership } from "@/types";
import { AddToCampaignDialog } from "./add-to-campaign-dialog";
import { CampaignStatusBadge, CampaignTypeBadge, MemberStatusBadge } from "./campaign-badges";

interface RecordCampaignsTabProps {
  memberType: MemberType;
  memberId: string;
  /** A converted lead cannot be added to campaigns any more. */
  converted?: boolean;
}

/**
 * "Campaigns" tab of a lead / contact detail page: the campaigns the record belongs to
 * (`GET /campaigns/by-member`). Shown with `crm.campaigns.read`; adding and removing need
 * `crm.campaigns.write`.
 */
export function RecordCampaignsTab({ memberType, memberId, converted = false }: RecordCampaignsTabProps) {
  const { t } = useTranslation(["campaigns", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteCampaigns } = useCrmPermissions();
  const { data, isLoading, error, refetch } = useCampaignsByMember(memberType, memberId);
  const remove = useRemoveCampaignMembers();
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<RecordCampaignMembership | null>(null);

  async function confirmRemove() {
    if (!removing) return;
    try {
      await remove.mutateAsync({
        campaignId: removing.campaignId,
        memberIds: [removing.membershipId],
      });
      toast({ variant: "success", description: t("campaigns:recordTab.removedOne") });
    } catch (err) {
      toastApiError(err);
    }
    setRemoving(null);
  }

  return (
    <Stack gap="md">
      {canWriteCampaigns && !converted && (
        <Group>
          <Button leftSection={<Plus size={16} />} onClick={() => setAdding(true)}>
            {t("campaigns:addToCampaign.action")}
          </Button>
        </Group>
      )}

      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading ? (
        <Stack gap="sm">
          <Skeleton h={48} />
          <Skeleton h={48} />
        </Stack>
      ) : !data || data.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="lg">
          {t("campaigns:recordTab.empty")}
        </Text>
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={640}>
            <Table verticalSpacing="sm" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("campaigns:recordTab.campaign")}</Table.Th>
                  <Table.Th>{t("campaigns:recordTab.type")}</Table.Th>
                  <Table.Th>{t("campaigns:recordTab.campaignStatus")}</Table.Th>
                  <Table.Th>{t("campaigns:recordTab.memberStatus")}</Table.Th>
                  <Table.Th>{t("campaigns:recordTab.addedAt")}</Table.Th>
                  <Table.Th w={60} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {data.map((row) => (
                  <Table.Tr key={row.membershipId} data-testid="record-campaign-row">
                    <Table.Td>
                      <Anchor component={Link} to={`/app/campaigns/${row.campaignId}`} size="sm" fw={500}>
                        {row.campaignName}
                      </Anchor>
                    </Table.Td>
                    <Table.Td>
                      <CampaignTypeBadge type={row.campaignType} />
                    </Table.Td>
                    <Table.Td>
                      <CampaignStatusBadge status={row.campaignStatus} />
                    </Table.Td>
                    <Table.Td>
                      <MemberStatusBadge status={row.memberStatus} />
                    </Table.Td>
                    <Table.Td>{formatDateTime(row.addedAt, timeZone)}</Table.Td>
                    <Table.Td>
                      <RowActions
                        editLabel=""
                        deleteLabel={t("campaigns:recordTab.remove", { name: row.campaignName })}
                        onDelete={canWriteCampaigns ? () => setRemoving(row) : undefined}
                      />
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}

      {adding && (
        <AddToCampaignDialog
          memberType={memberType}
          memberIds={[memberId]}
          onClose={() => setAdding(false)}
        />
      )}
      <ConfirmDialog
        opened={!!removing}
        title={t("campaigns:recordTab.removeTitle")}
        message={t("campaigns:recordTab.removeMessage", { name: removing?.campaignName })}
        confirmLabel={t("campaigns:members.remove")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmRemove()}
        onClose={() => setRemoving(null)}
      />
    </Stack>
  );
}
