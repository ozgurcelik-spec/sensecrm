import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Group, Select, Tabs, Text } from "@mantine/core";
import { DataTable, type Column } from "@/components/crm/data-table";
import { PageHeader } from "@/components/page-header";
import { ApprovalDecisionDialog } from "@/components/workflows/approval-decision-dialog";
import { ApprovalStatusBadge } from "@/components/workflows/badges";
import { useApprovals } from "@/hooks/use-approvals";
import { useListParams } from "@/hooks/use-list-params";
import { usePermission } from "@/hooks/use-permission";
import { formatDateTime } from "@/lib/dates";
import { formatMoney, orDash } from "@/lib/format";
import { subjectPath, subjectReadPermission } from "@/lib/workflow";
import { useAuthStore } from "@/store/auth.store";
import type { ApprovalListQuery } from "@/services/approvals.service";
import { APPROVAL_STATUSES, PERMISSIONS, type Approval, type ApprovalDecision } from "@/types";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["status"] as const;
const HISTORY_STATUSES = APPROVAL_STATUSES.filter((s) => s !== "pending");

type ApprovalsTab = "pending" | "history";

function SubjectCell({ approval }: { approval: Approval }) {
  const readPermission = subjectReadPermission(approval.subjectType);
  const canOpen = usePermission(readPermission ?? "");
  const path = subjectPath(approval.subjectType, approval.subjectId);
  const label = approval.subjectName ?? approval.subjectId;
  return path && readPermission && canOpen ? (
    <Anchor component={Link} to={path} size="sm">
      {label}
    </Anchor>
  ) : (
    <Text size="sm">{label}</Text>
  );
}

/**
 * "Onaylarım": the caller's approvals (`mine=true`). Pending ones can be approved or rejected with
 * `crm.approvals.decide`; the history lists everything with an optional status filter. Tab, status
 * and paging live in the URL.
 */
export default function ApprovalsPage() {
  const { t } = useTranslation(["workflows", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const canDecide = usePermission(PERMISSIONS.crmApprovalsDecide);
  const [searchParams, setSearchParams] = useSearchParams();
  const tab: ApprovalsTab = searchParams.get("tab") === "history" ? "history" : "pending";
  const params = useListParams(FILTERS);
  const [deciding, setDeciding] = useState<{
    approval: Approval;
    decision: ApprovalDecision;
  } | null>(null);

  const apiQuery = useMemo<ApprovalListQuery>(
    () => ({
      page: params.page,
      pageSize: params.pageSize,
      mine: true,
      // The contract takes a single status: the history shows everything unless one is picked.
      status: tab === "pending" ? "pending" : params.filters.status || undefined,
    }),
    [params.page, params.pageSize, params.filters.status, tab]
  );
  const { data, isLoading, isFetching, error, refetch } = useApprovals(apiQuery);

  const columns: Column<Approval>[] = [
    {
      key: "title",
      header: t("workflows:approvals.column.title"),
      render: (a) => (
        <Text size="sm" fw={500}>
          {a.title}
        </Text>
      ),
    },
    {
      key: "subject",
      header: t("workflows:approvals.column.subject"),
      render: (a) => <SubjectCell approval={a} />,
    },
    {
      key: "amount",
      header: t("workflows:approvals.column.amount"),
      render: (a) => (a.amount === undefined ? "-" : formatMoney(a.amount, a.currency)),
    },
    {
      key: "requestedAt",
      header: t("workflows:approvals.column.requestedAt"),
      render: (a) => formatDateTime(a.requestedAt, timeZone),
    },
    ...(tab === "history"
      ? ([
          {
            key: "status",
            header: t("workflows:approvals.column.status"),
            render: (a) => <ApprovalStatusBadge status={a.status} />,
          },
          {
            key: "decidedAt",
            header: t("workflows:approvals.column.decidedAt"),
            render: (a) => (a.decidedAt ? formatDateTime(a.decidedAt, timeZone) : "-"),
          },
          {
            key: "comment",
            header: t("workflows:approvals.column.comment"),
            render: (a) => (
              <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
                {orDash(a.comment)}
              </Text>
            ),
          },
        ] satisfies Column<Approval>[])
      : []),
    {
      key: "actions",
      header: "",
      width: 190,
      render: (a) =>
        canDecide && a.status === "pending" ? (
          <Group gap="xs" wrap="nowrap" justify="flex-end">
            <Button
              size="xs"
              color="green"
              variant="light"
              aria-label={t("workflows:approvals.approveNamed", { title: a.title })}
              onClick={() => setDeciding({ approval: a, decision: "approve" })}
            >
              {t("workflows:approvals.approve")}
            </Button>
            <Button
              size="xs"
              color="red"
              variant="light"
              aria-label={t("workflows:approvals.rejectNamed", { title: a.title })}
              onClick={() => setDeciding({ approval: a, decision: "reject" })}
            >
              {t("workflows:approvals.reject")}
            </Button>
          </Group>
        ) : null,
    },
  ];

  return (
    <>
      <PageHeader
        title={t("workflows:approvals.title")}
        description={t("workflows:approvals.description")}
      />
      <Tabs
        value={tab}
        // The lists have different filters: switching starts from a clean query string.
        onChange={(value) =>
          setSearchParams(value === "history" ? { tab: "history" } : {}, { replace: true })
        }
        keepMounted={false}
      >
        <Tabs.List mb="md" aria-label={t("workflows:approvals.tabs.label")}>
          <Tabs.Tab value="pending">{t("workflows:approvals.tabs.pending")}</Tabs.Tab>
          <Tabs.Tab value="history">{t("workflows:approvals.tabs.history")}</Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {tab === "history" && (
        <Group mb="md">
          <Select
            aria-label={t("workflows:approvals.statusFilter")}
            placeholder={t("workflows:approvals.allStatuses")}
            w={200}
            clearable
            data={HISTORY_STATUSES.map((s) => ({
              value: s,
              label: t(`workflows:approvals.statuses.${s}`),
            }))}
            value={params.filters.status || null}
            onChange={(value) => params.setFilter("status", value)}
          />
        </Group>
      )}

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(a) => a.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={() => void refetch()}
        sort={null}
        onSort={() => undefined}
        page={params.page}
        pageSize={params.pageSize}
        totalCount={data?.totalCount}
        onPageChange={params.setPage}
        onPageSizeChange={params.setPageSize}
        emptyMessage={
          tab === "pending"
            ? t("workflows:approvals.emptyPending")
            : t("workflows:approvals.emptyHistory")
        }
        minWidth={tab === "history" ? 1000 : 760}
      />

      {deciding && (
        <ApprovalDecisionDialog
          approval={deciding.approval}
          initialDecision={deciding.decision}
          onClose={() => setDeciding(null)}
        />
      )}
    </>
  );
}
