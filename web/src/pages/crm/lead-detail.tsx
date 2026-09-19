import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Anchor, Button, Card, Group, Stack, Text } from "@mantine/core";
import { Pencil, Repeat, Trash2 } from "lucide-react";
import { LeadRatingBadge, LeadStatusBadge } from "@/components/crm/badges";
import { LeadConvertDialog } from "@/components/crm/lead-convert-dialog";
import { LeadFormDialog } from "@/components/crm/lead-form-dialog";
import { WorkflowStatusStrip } from "@/components/workflows/workflow-status-strip";
import { RecordActivitiesTab } from "@/components/activities/record-activities-tab";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDeleteLead, useLead } from "@/hooks/use-leads";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type Lead } from "@/types";

function ConvertedLinks({ lead }: { lead: Lead }) {
  const { t } = useTranslation(["crm"]);
  const canAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canDeals = usePermission(PERMISSIONS.crmDealsRead);
  const links = [
    {
      id: lead.convertedAccountId,
      path: "accounts",
      label: t("crm:leads.converted.account"),
      allowed: canAccounts,
    },
    {
      id: lead.convertedContactId,
      path: "contacts",
      label: t("crm:leads.converted.contact"),
      allowed: canContacts,
    },
    {
      id: lead.convertedDealId,
      path: "deals",
      label: t("crm:leads.converted.deal"),
      allowed: canDeals,
    },
  ].filter((l) => l.id && l.allowed);

  return (
    <Alert color="violet" variant="light" title={t("crm:leads.converted.title")}>
      <Group gap="md">
        {links.map((l) => (
          <Anchor key={l.path} component={Link} to={`/app/${l.path}/${l.id}`} size="sm">
            {l.label}
          </Anchor>
        ))}
      </Group>
    </Alert>
  );
}

export default function LeadDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["crm", "common", "activities"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteLeads, canConvertLeads, canWriteDeals } = useCrmPermissions();
  const { data: lead, isLoading, error, refetch } = useLead(id);
  const remove = useDeleteLead();
  const [editing, setEditing] = useState(false);
  const [converting, setConverting] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const converted = lead?.status === "converted";

  async function confirmDelete() {
    if (!lead) return;
    try {
      await remove.mutateAsync(lead.id);
      toast({ variant: "success", description: t("crm:leads.deleted") });
      navigate("/app/leads", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDeleting(false);
    }
  }

  const canReadActivities = usePermission(PERMISSIONS.crmActivitiesRead);
  const tabs = lead
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <WorkflowStatusStrip subjectType="lead" subjectId={lead.id} />
              {converted && <ConvertedLinks lead={lead} />}
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("crm:leads.fields.company")}
                </Text>
                <Text size="sm">{lead.company}</Text>
              </Card>
            </Stack>
          ),
        },
        ...(canReadActivities
          ? [
              {
                value: "activities",
                label: t("activities:tab.title"),
                content: (
                  <RecordActivitiesTab
                    related={{ type: "lead", id: lead.id, name: lead.fullName }}
                  />
                ),
              },
            ]
          : []),
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Lead" entityId={lead.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/leads"
        backLabel={t("crm:leads.title")}
        title={lead?.fullName}
        subtitle={lead?.company}
        badges={lead && <LeadStatusBadge status={lead.status} />}
        actions={
          lead && (
            <>
              {canConvertLeads && !converted && (
                <Button leftSection={<Repeat size={16} />} onClick={() => setConverting(true)}>
                  {t("crm:leads.convert.action")}
                </Button>
              )}
              {canWriteLeads && !converted && (
                <Button
                  variant="default"
                  leftSection={<Pencil size={16} />}
                  onClick={() => setEditing(true)}
                >
                  {t("common:edit")}
                </Button>
              )}
              {canWriteLeads && (
                <Button
                  variant="default"
                  color="red"
                  leftSection={<Trash2 size={16} />}
                  onClick={() => setDeleting(true)}
                >
                  {t("common:delete")}
                </Button>
              )}
            </>
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          lead && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                { label: t("crm:leads.fields.company"), value: lead.company },
                { label: t("crm:leads.fields.email"), value: orDash(lead.email) },
                { label: t("crm:leads.fields.phone"), value: orDash(lead.phone) },
                {
                  label: t("crm:leads.fields.source"),
                  value: t(`crm:leads.sources.${lead.source}`, { defaultValue: lead.source }),
                },
                {
                  label: t("crm:leads.fields.status"),
                  value: <LeadStatusBadge status={lead.status} />,
                },
                {
                  label: t("crm:leads.fields.rating"),
                  value: <LeadRatingBadge rating={lead.rating} />,
                },
                { label: t("crm:owner"), value: orDash(lead.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(lead.createdAt, timeZone) },
                ...(lead.convertedAt
                  ? [
                      {
                        label: t("crm:leads.converted.at"),
                        value: formatDateTime(lead.convertedAt, timeZone),
                      },
                    ]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && lead && <LeadFormDialog lead={lead} onClose={() => setEditing(false)} />}
      {converting && lead && (
        <LeadConvertDialog
          lead={lead}
          canCreateDeal={canWriteDeals}
          onClose={() => setConverting(false)}
        />
      )}
      <ConfirmDialog
        opened={deleting}
        title={t("crm:leads.deleteTitle")}
        message={t("crm:leads.deleteMessage", { name: lead?.fullName })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
