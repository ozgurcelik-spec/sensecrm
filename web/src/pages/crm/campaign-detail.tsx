import { useState } from "react";
import { useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Button, Card, Stack, Text } from "@mantine/core";
import { Pencil, Trash2 } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { CampaignStatusBadge, CampaignTypeBadge } from "@/components/marketing/campaign-badges";
import { CampaignFormDialog } from "@/components/marketing/campaign-form-dialog";
import { CampaignMembersTab } from "@/components/marketing/campaign-members-tab";
import { CampaignMetricsCards } from "@/components/marketing/campaign-metrics-cards";
import { CampaignStatusMenu } from "@/components/marketing/campaign-status-menu";
import { useCampaign, useDeleteCampaign } from "@/hooks/use-campaigns";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCampaignDates } from "@/lib/campaign";
import { formatDateTime } from "@/lib/dates";
import { formatMoney, orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";

export default function CampaignDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["campaigns", "common", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteCampaigns } = useCrmPermissions();
  const { data: campaign, isLoading, error, refetch } = useCampaign(id);
  const remove = useDeleteCampaign();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);

  async function confirmDelete() {
    if (!campaign) return;
    try {
      await remove.mutateAsync(campaign.id);
      toast({ variant: "success", description: t("campaigns:deleted") });
      navigate("/app/campaigns", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDeleting(false);
    }
  }

  const tabs = campaign
    ? [
        {
          value: "general",
          label: t("campaigns:tabs.general"),
          content: (
            <Stack gap="md">
              <CampaignMetricsCards campaignId={campaign.id} />
              {campaign.description && (
                <Card withBorder padding="md">
                  <Text fw={600} mb="sm">
                    {t("campaigns:fields.description")}
                  </Text>
                  <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
                    {campaign.description}
                  </Text>
                </Card>
              )}
            </Stack>
          ),
        },
        {
          value: "members",
          label: t("campaigns:tabs.members"),
          content: <CampaignMembersTab campaign={campaign} />,
        },
        {
          value: "audit",
          label: t("campaigns:tabs.audit"),
          content: <RecordAuditTab entityType="Campaign" entityId={campaign.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/campaigns"
        backLabel={t("campaigns:title")}
        title={campaign?.name}
        subtitle={campaign && t(`campaigns:types.${campaign.type}`)}
        badges={campaign && <CampaignStatusBadge status={campaign.status} />}
        actions={
          campaign &&
          canWriteCampaigns && (
            <>
              <CampaignStatusMenu campaign={campaign} />
              <Button
                variant="default"
                leftSection={<Pencil size={16} />}
                onClick={() => setEditing(true)}
              >
                {t("common:edit")}
              </Button>
              <Button
                variant="default"
                color="red"
                leftSection={<Trash2 size={16} />}
                onClick={() => setDeleting(true)}
              >
                {t("common:delete")}
              </Button>
            </>
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          campaign && (
            <InfoPanel
              title={t("campaigns:panel.details")}
              rows={[
                { label: t("campaigns:fields.type"), value: <CampaignTypeBadge type={campaign.type} /> },
                { label: t("campaigns:fields.dates"), value: formatCampaignDates(campaign) },
                {
                  label: t("campaigns:fields.budget"),
                  value:
                    campaign.budget === undefined ? "-" : formatMoney(campaign.budget, campaign.currency),
                },
                {
                  label: t("campaigns:fields.expectedRevenue"),
                  value:
                    campaign.expectedRevenue === undefined
                      ? "-"
                      : formatMoney(campaign.expectedRevenue, campaign.currency),
                },
                {
                  label: t("campaigns:fields.actualCost"),
                  value:
                    campaign.actualCost === undefined
                      ? "-"
                      : formatMoney(campaign.actualCost, campaign.currency),
                },
                { label: t("campaigns:fields.owner"), value: orDash(campaign.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(campaign.createdAt, timeZone) },
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && campaign && <CampaignFormDialog campaign={campaign} onClose={() => setEditing(false)} />}
      <ConfirmDialog
        opened={deleting}
        title={t("campaigns:deleteTitle")}
        message={t("campaigns:deleteMessage", { name: campaign?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
