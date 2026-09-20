import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Anchor, Card, Group, Stack, Text } from "@mantine/core";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { useAttachmentsTab } from "@/hooks/use-attachments-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { CaseActions } from "@/components/service/case-actions";
import { CasePriorityBadge, CaseStatusBadge, SlaBadge } from "@/components/service/case-badges";
import { CaseFormDialog } from "@/components/service/case-form-dialog";
import { CaseReplyBox } from "@/components/service/case-reply-box";
import { CaseTimeline } from "@/components/service/case-timeline";
import { useCase, useDeleteCase } from "@/hooks/use-cases";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useSlaLines } from "@/hooks/use-sla-lines";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { orDash } from "@/lib/format";
import { caseKeys } from "@/services/cases.service";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type CaseDetail } from "@/types";

/** The two SLA rows of the info panel: target, what happened and the time left or past. */
function SlaRows({ item }: { item: CaseDetail }) {
  const lines = useSlaLines(item);
  return (
    <Stack gap={4}>
      {lines.map((line) => {
        const color = line.breached
          ? "red"
          : line.pending && item.slaState === "atRisk"
            ? "yellow.8"
            : undefined;
        return (
          <Text
            key={line.key}
            size="sm"
            c={color}
            fw={line.breached ? 600 : undefined}
            data-testid={`sla-line-${line.key}`}
            data-breached={line.breached}
          >
            <Text component="span" size="sm" fw={500} c="inherit">
              {line.label}:
            </Text>{" "}
            {line.text}
          </Text>
        );
      })}
    </Stack>
  );
}

export default function CaseDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["service", "common", "crm"]);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteCases } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const { data: item, isLoading, error, refetch } = useCase(id);
  const remove = useDeleteCase();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const at = (iso: string) => formatDateTime(iso, timeZone);
  /** Our copy is out of date (a 409): reload the case, its timeline and lists. */
  const reload = () => void queryClient.invalidateQueries({ queryKey: caseKeys.all });

  async function confirmDelete() {
    if (!item) return;
    try {
      await remove.mutateAsync(item.id);
      toast({ variant: "success", description: t("service:deleted") });
      void navigate("/app/cases", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDeleting(false);
    }
  }

  const attachmentsTab = useAttachmentsTab("case", id);
  const tabs = item
    ? [
        {
          value: "general",
          label: t("service:tabs.general"),
          content: (
            <Stack gap="md">
              {item.description && (
                <Card withBorder padding="md">
                  <Text fw={600} mb="xs">
                    {t("service:fields.description")}
                  </Text>
                  <Text size="sm" style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
                    {item.description}
                  </Text>
                </Card>
              )}
              {canWriteCases && <CaseReplyBox caseId={item.id} status={item.status} onStale={reload} />}
              <CaseTimeline caseId={item.id} />
            </Stack>
          ),
        },
        ...attachmentsTab,
        {
          value: "audit",
          label: t("service:tabs.audit"),
          content: <RecordAuditTab entityType="Case" entityId={item.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/cases"
        backLabel={t("service:title")}
        title={item ? `${item.number} · ${item.subject}` : undefined}
        badges={
          item && (
            <Group gap="xs">
              <CaseStatusBadge status={item.status} />
              <CasePriorityBadge priority={item.priority} />
              <SlaBadge item={item} />
            </Group>
          )
        }
        actions={
          item && (
            <CaseActions
              item={item}
              onEdit={() => setEditing(true)}
              onDelete={() => setDeleting(true)}
              onStale={reload}
            />
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panelSide="right"
        panel={
          item && (
            <InfoPanel
              title={t("service:panel.details")}
              rows={[
                {
                  label: t("service:fields.account"),
                  value:
                    item.accountId && canReadAccounts ? (
                      <Anchor component={Link} to={`/app/accounts/${item.accountId}`} size="sm">
                        {item.accountName ?? item.accountId}
                      </Anchor>
                    ) : (
                      orDash(item.accountName)
                    ),
                },
                {
                  label: t("service:fields.contact"),
                  value:
                    item.contactId && canReadContacts ? (
                      <Anchor component={Link} to={`/app/contacts/${item.contactId}`} size="sm">
                        {item.contactName ?? item.contactId}
                      </Anchor>
                    ) : (
                      orDash(item.contactName)
                    ),
                },
                {
                  label: t("service:fields.channel"),
                  value: t(`service:channels.${item.channel}`, { defaultValue: item.channel }),
                },
                {
                  label: t("service:fields.assignee"),
                  value: item.assignedUserId
                    ? orDash(item.assignedUserName)
                    : t("service:unassigned"),
                },
                { label: t("service:fields.createdBy"), value: orDash(item.createdByName) },
                { label: t("service:fields.createdAt"), value: at(item.createdAt) },
                {
                  label: t("service:fields.firstResponse"),
                  value: item.firstResponseAt
                    ? at(item.firstResponseAt)
                    : t("service:panel.awaitingResponse", { date: at(item.firstResponseDueAt) }),
                },
                { label: t("service:fields.dueAt"), value: at(item.dueAt) },
                ...(item.resolvedAt
                  ? [{ label: t("service:fields.resolvedAt"), value: at(item.resolvedAt) }]
                  : []),
                ...(item.closedAt
                  ? [{ label: t("service:fields.closedAt"), value: at(item.closedAt) }]
                  : []),
                ...(item.resolutionNote
                  ? [
                      {
                        label: t("service:fields.resolutionNote"),
                        value: (
                          <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
                            {item.resolutionNote}
                          </Text>
                        ),
                      },
                    ]
                  : []),
                { label: t("service:fields.reopenCount"), value: String(item.reopenCount) },
                { label: t("service:panel.sla"), value: <SlaRows item={item} /> },
                ...(item.updatedAt
                  ? [{ label: t("service:fields.updatedAt"), value: at(item.updatedAt) }]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && item && (
        <CaseFormDialog item={item} onClose={() => setEditing(false)} />
      )}
      <ConfirmDialog
        opened={deleting}
        title={t("service:deleteTitle")}
        message={t("service:deleteMessage", { name: item?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
