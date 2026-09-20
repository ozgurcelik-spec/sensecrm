import { lazy, Suspense, useState } from "react";
import { useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Button, Skeleton, Tooltip } from "@mantine/core";
import { Ban, Pencil, Play, Trash2, Undo2 } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { InfoPanel, RecordDetailShell, type DetailTab } from "@/components/crm/record-detail-shell";
import { DeletionPanel } from "@/components/platform/deletion-panel";
import { DeletionRequestDialog } from "@/components/platform/deletion-request-dialog";
import { OrganizationSummary } from "@/components/platform/organization-summary";
import { PlatformAuditTable } from "@/components/platform/platform-audit-table";
import { TenantStatusBadge } from "@/components/platform/status-badge";
import { StepUpDialog } from "@/components/platform/step-up-dialog";
import { SubscriptionEditorDialog } from "@/components/platform/subscription-editor-dialog";
import { SuspendDialog } from "@/components/platform/suspend-dialog";
import {
  useCancelPlatformDeletion,
  usePlatformOrganization,
  usePlatformPlans,
  useReactivatePlatformOrganization,
  useRetryPlatformDeletion,
} from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate } from "@/lib/format";
import { organizationActions } from "@/lib/platform";
import type { PlatformOverLimit } from "@/types";

// Charts (recharts) are their own chunk and only load with the Usage tab.
const UsageChart = lazy(() => import("@/components/platform/usage-chart"));

type Dialog =
  | "plan"
  | "suspend"
  | "deletion"
  | "reactivate"
  | "cancelDeletion"
  | "retryDeletion"
  | null;

function AuditTab({ tenantId }: { tenantId: string }) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  return (
    <PlatformAuditTable
      query={{ tenantId }}
      page={page}
      pageSize={pageSize}
      onPageChange={setPage}
      onPageSizeChange={(size) => {
        setPageSize(size);
        setPage(1);
      }}
      hideTenant
    />
  );
}

/** Organization detail: lifecycle actions valid for its state, plan card, usage history, audit and deletion request. */
export default function OrganizationDetailPage() {
  const { t } = useTranslation(["platform", "common"]);
  const { tenantId } = useParams();
  const { data: org, isLoading, error, refetch } = usePlatformOrganization(tenantId);
  const plans = usePlatformPlans();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [overLimit, setOverLimit] = useState<PlatformOverLimit[]>([]);

  const reactivate = useReactivatePlatformOrganization(tenantId ?? "");
  const cancelDeletion = useCancelPlatformDeletion(tenantId ?? "");
  const retryDeletion = useRetryPlatformDeletion(tenantId ?? "");
  const actions = org ? organizationActions(org) : null;

  async function confirmReactivate() {
    try {
      await reactivate.mutateAsync();
      toast({ variant: "success", description: t("platform:reactivate.done") });
    } catch (err) {
      toastApiError(err);
    }
    setDialog(null);
  }

  async function confirmCancelDeletion() {
    try {
      await cancelDeletion.mutateAsync();
      toast({ variant: "success", description: t("platform:deletion.cancelled") });
    } catch (err) {
      toastApiError(err);
    }
    setDialog(null);
  }

  function actionButtons(allowed: NonNullable<typeof actions>) {
    if (allowed.systemProtected) {
      // The operating organization cannot be suspended, deleted or re-planned: every action is disabled, with the reason.
      return (
        <Tooltip label={t("platform:system.protected")} multiline w={260}>
          <span data-testid="system-actions" style={{ display: "inline-flex", gap: 8 }}>
            <Button variant="default" leftSection={<Pencil size={16} />} disabled>
              {t("platform:actions.editPlan")}
            </Button>
            <Button variant="default" leftSection={<Ban size={16} />} disabled>
              {t("platform:actions.suspend")}
            </Button>
            <Button color="red" variant="light" leftSection={<Trash2 size={16} />} disabled>
              {t("platform:actions.requestDeletion")}
            </Button>
          </span>
        </Tooltip>
      );
    }
    return (
      <>
        {allowed.editPlan && (
          <Button variant="default" leftSection={<Pencil size={16} />} onClick={() => setDialog("plan")}>
            {t("platform:actions.editPlan")}
          </Button>
        )}
        {allowed.suspend && (
          <Button variant="default" leftSection={<Ban size={16} />} onClick={() => setDialog("suspend")}>
            {t("platform:actions.suspend")}
          </Button>
        )}
        {allowed.reactivate && (
          <Button variant="default" leftSection={<Play size={16} />} onClick={() => setDialog("reactivate")}>
            {t("platform:actions.reactivate")}
          </Button>
        )}
        {allowed.requestDeletion && (
          <Button
            color="red"
            variant="light"
            leftSection={<Trash2 size={16} />}
            onClick={() => setDialog("deletion")}
          >
            {t("platform:actions.requestDeletion")}
          </Button>
        )}
        {allowed.cancelDeletion && (
          <Button
            color="orange"
            variant="light"
            leftSection={<Undo2 size={16} />}
            onClick={() => setDialog("cancelDeletion")}
          >
            {t("platform:actions.cancelDeletion")}
          </Button>
        )}
      </>
    );
  }

  const tabs: DetailTab[] = org
    ? [
        {
          value: "summary",
          label: t("platform:tabs.summary"),
          content: (
            <OrganizationSummary
              org={org}
              overLimit={overLimit}
              onDismissOverLimit={() => setOverLimit([])}
            />
          ),
        },
        {
          value: "usage",
          label: t("platform:tabs.usage"),
          content: (
            <Suspense fallback={<Skeleton h={300} />}>
              <UsageChart tenantId={org.tenantId} />
            </Suspense>
          ),
        },
        {
          value: "audit",
          label: t("platform:tabs.audit"),
          content: <AuditTab tenantId={org.tenantId} />,
        },
        ...(org.deletion
          ? [
              {
                value: "deletion",
                label: t("platform:tabs.deletion"),
                content: (
                  <DeletionPanel
                    deletion={org.deletion}
                    onCancel={actions?.cancelDeletion ? () => setDialog("cancelDeletion") : undefined}
                    onRetry={
                      org.deletion.status === "failed" && !org.isSystem
                        ? () => setDialog("retryDeletion")
                        : undefined
                    }
                  />
                ),
              },
            ]
          : []),
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/platform/organizations"
        backLabel={t("platform:organizations.title")}
        title={org?.name}
        subtitle={org?.slug}
        badges={
          org && (
            <>
              <TenantStatusBadge status={org.status} />
              {org.isSystem && (
                <Badge variant="outline" color="gray">
                  {t("platform:system.badge")}
                </Badge>
              )}
            </>
          )
        }
        actions={org && actions && actionButtons(actions)}
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          org && (
            <InfoPanel
              title={t("platform:info.title")}
              rows={[
                { label: t("platform:info.plan"), value: org.planName },
                { label: t("platform:info.status"), value: t(`platform:status.${org.status}`) },
                {
                  label: t("platform:info.access"),
                  value: t(`platform:accessLevel.${org.accessLevel}`, { defaultValue: org.accessLevel }),
                },
                { label: t("platform:info.trialEnds"), value: formatCalendarDate(org.trialEndsOn) },
                {
                  label: t("platform:info.source"),
                  value: t(`platform:sources.${org.source}`, { defaultValue: org.source }),
                },
                { label: t("platform:info.createdAt"), value: formatDateTime(org.createdAt) },
                { label: t("platform:info.tenantId"), value: org.tenantId },
              ]}
            />
          )
        }
        tabs={tabs}
      />

      {org && dialog === "plan" && (
        <SubscriptionEditorDialog
          org={org}
          plans={plans.data ?? []}
          onClose={() => setDialog(null)}
          onSaved={(result) => {
            setOverLimit(result.overLimit);
            setDialog(null);
          }}
        />
      )}
      {org && dialog === "suspend" && (
        <SuspendDialog tenantId={org.tenantId} organizationName={org.name} onClose={() => setDialog(null)} />
      )}
      {org && dialog === "deletion" && (
        <DeletionRequestDialog
          tenantId={org.tenantId}
          organizationName={org.name}
          onClose={() => setDialog(null)}
        />
      )}
      {org && dialog === "retryDeletion" && (
        <StepUpDialog
          title={t("platform:deletion.retryTitle")}
          message={t("platform:deletion.retryMessage", { name: org.name })}
          confirmLabel={t("platform:deletion.retry")}
          isPending={retryDeletion.isPending}
          onConfirm={async (currentPassword) => {
            await retryDeletion.mutateAsync({ currentPassword });
            toast({ variant: "success", description: t("platform:deletion.retried") });
            setDialog(null);
          }}
          onClose={() => setDialog(null)}
        />
      )}
      <ConfirmDialog
        opened={dialog === "reactivate"}
        title={t("platform:reactivate.title")}
        message={t("platform:reactivate.message", { name: org?.name })}
        confirmLabel={t("platform:actions.reactivate")}
        loading={reactivate.isPending}
        onConfirm={() => void confirmReactivate()}
        onClose={() => setDialog(null)}
      />
      <ConfirmDialog
        opened={dialog === "cancelDeletion"}
        title={t("platform:deletion.cancelTitle")}
        message={t("platform:deletion.cancelMessage", { name: org?.name })}
        confirmLabel={t("platform:actions.cancelDeletion")}
        loading={cancelDeletion.isPending}
        onConfirm={() => void confirmCancelDeletion()}
        onClose={() => setDialog(null)}
      />
      {org?.status === "deleted" && (
        <Alert color="gray" variant="light" mt="md">
          {t("platform:deletion.erased")}
        </Alert>
      )}
    </>
  );
}
