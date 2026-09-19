import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Menu, Stack, Text } from "@mantine/core";
import { ChevronDown, Pencil, Trash2 } from "lucide-react";
import { StageBadge } from "@/components/crm/badges";
import { DealFormDialog } from "@/components/crm/deal-form-dialog";
import { LostReasonDialog } from "@/components/crm/lost-reason-dialog";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDeal, useDeleteDeal, useMoveDealStage } from "@/hooks/use-deals";
import { usePermission } from "@/hooks/use-permission";
import { usePipelines } from "@/hooks/use-pipelines";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

export default function DealDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["crm", "common"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteDeals } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const { data: deal, isLoading, error, refetch } = useDeal(id);
  const pipelines = usePipelines();
  const remove = useDeleteDeal();
  const move = useMoveDealStage();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [lostStageId, setLostStageId] = useState<string | null>(null);

  const stages = pipelines.data?.find((p) => p.id === deal?.pipelineId)?.stages ?? [];

  function changeStage(stageId: string) {
    const target = stages.find((s) => s.id === stageId);
    if (!deal || !target) return;
    if (target.kind === "lost") {
      setLostStageId(stageId);
      return;
    }
    move.mutate(
      { dealId: deal.id, stageId },
      {
        onSuccess: () =>
          toast({
            variant: "success",
            description: t("crm:deals.board.moved", { name: deal.name, stage: target.name }),
          }),
        onError: (err) => toastApiError(err),
      }
    );
  }

  function confirmLost(reason: string) {
    if (!deal || !lostStageId) return;
    move.mutate(
      { dealId: deal.id, stageId: lostStageId, lostReason: reason },
      {
        onSuccess: () => {
          setLostStageId(null);
          toast({ variant: "success", description: t("crm:deals.lost.done") });
        },
        onError: (err) => {
          setLostStageId(null);
          toastApiError(err);
        },
      }
    );
  }

  async function confirmDelete() {
    if (!deal) return;
    try {
      await remove.mutateAsync(deal.id);
      toast({ variant: "success", description: t("crm:deals.deleted") });
      navigate("/app/deals", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDeleting(false);
    }
  }

  const tabs = deal
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("crm:deals.fields.stage")}
                </Text>
                <StageBadge name={deal.stageName} kind={deal.stageKind} />
                <Text size="sm" c="dimmed" mt="xs">
                  {t("crm:deals.probabilityValue", { value: deal.probability })}
                </Text>
              </Card>
              {deal.stageKind === "lost" && (
                <Card withBorder padding="md">
                  <Text fw={600} mb="sm">
                    {t("crm:deals.fields.lostReason")}
                  </Text>
                  <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
                    {orDash(deal.lostReason)}
                  </Text>
                </Card>
              )}
            </Stack>
          ),
        },
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Deal" entityId={deal.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/deals"
        backLabel={t("crm:deals.title")}
        title={deal?.name}
        subtitle={deal?.accountName}
        badges={deal && <StageBadge name={deal.stageName} kind={deal.stageKind} />}
        actions={
          deal &&
          canWriteDeals && (
            <>
              <Menu position="bottom-end">
                <Menu.Target>
                  <Button rightSection={<ChevronDown size={14} />} loading={move.isPending}>
                    {t("crm:deals.changeStage")}
                  </Button>
                </Menu.Target>
                <Menu.Dropdown>
                  {stages
                    .filter((s) => s.id !== deal.stageId)
                    .map((s) => (
                      <Menu.Item key={s.id} onClick={() => changeStage(s.id)}>
                        {s.name}
                      </Menu.Item>
                    ))}
                </Menu.Dropdown>
              </Menu>
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
          deal && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("crm:deals.fields.account"),
                  value: canReadAccounts ? (
                    <Anchor component={Link} to={`/app/accounts/${deal.accountId}`} size="sm">
                      {deal.accountName}
                    </Anchor>
                  ) : (
                    deal.accountName
                  ),
                },
                {
                  label: t("crm:deals.fields.contact"),
                  value:
                    deal.contactId && canReadContacts ? (
                      <Anchor component={Link} to={`/app/contacts/${deal.contactId}`} size="sm">
                        {deal.contactName ?? deal.contactId}
                      </Anchor>
                    ) : (
                      orDash(deal.contactName)
                    ),
                },
                { label: t("crm:deals.fields.pipeline"), value: deal.pipelineName },
                {
                  label: t("crm:deals.fields.amount"),
                  value: formatMoney(deal.amount, deal.currency),
                },
                {
                  label: t("crm:deals.fields.closingDate"),
                  value: formatCalendarDate(deal.closingDate),
                },
                { label: t("crm:deals.fields.probability"), value: `${deal.probability}%` },
                { label: t("crm:owner"), value: orDash(deal.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(deal.createdAt, timeZone) },
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && deal && <DealFormDialog deal={deal} onClose={() => setEditing(false)} />}
      {lostStageId && deal && (
        <LostReasonDialog
          dealName={deal.name}
          loading={move.isPending}
          onConfirm={confirmLost}
          onClose={() => setLostStageId(null)}
        />
      )}
      <ConfirmDialog
        opened={deleting}
        title={t("crm:deals.deleteTitle")}
        message={t("crm:deals.deleteMessage", { name: deal?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
