import { useMemo, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Group, MultiSelect, Select, Text } from "@mantine/core";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { CasePriorityBadge, CaseStatusBadge, SlaBadge } from "@/components/service/case-badges";
import { CaseFormDialog } from "@/components/service/case-form-dialog";
import { useCases } from "@/hooks/use-cases";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams, type SortState } from "@/hooks/use-list-params";
import { usePermission } from "@/hooks/use-permission";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { CaseListQuery } from "@/services/cases.service";
import {
  CASE_CHANNELS,
  CASE_PRIORITIES,
  CASE_STATUSES,
  PERMISSIONS,
  SLA_STATES,
  type CaseListItem,
} from "@/types";

const FILTERS = [
  "status",
  "priority",
  "channel",
  "assignedUserId",
  "unassigned",
  "slaState",
  "accountId",
  "contactId",
] as const;

/** Statuses of a case that still needs work: what the "Open" quick filter selects. */
const OPEN_STATUSES = "new,open,pending";
const DEFAULT_SORT: SortState = { field: "createdAt", descending: true };

const csv = (value: string): string[] => (value ? value.split(",").filter(Boolean) : []);

type QuickKey = "all" | "open" | "mine" | "unassigned" | "breached";

export default function CasesPage() {
  const { t } = useTranslation(["service", "common"]);
  const { canWriteCases } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const me = useAuthStore((state) => state.me);
  const timeZone = me?.organization.timeZone;
  const params = useListParams(FILTERS);
  const [creating, setCreating] = useState(false);

  const apiQuery = useMemo<CaseListQuery>(() => {
    const { unassigned, ...rest } = params.query;
    const query: CaseListQuery = { ...rest, sort: params.query.sort ?? "-createdAt" };
    if (unassigned === "true") query.unassigned = true;
    return query;
  }, [params.query]);
  const { data, isLoading, isFetching, error, refetch } = useCases(apiQuery);

  const { status, assignedUserId, unassigned, slaState, priority } = params.filters;
  const isOpenScope = status === OPEN_STATUSES;
  const quick: Record<QuickKey, boolean> = {
    open: isOpenScope && !assignedUserId && !unassigned && !slaState,
    mine: isOpenScope && !!me && assignedUserId === me.user.id && !unassigned && !slaState,
    unassigned: isOpenScope && unassigned === "true" && !assignedUserId && !slaState,
    breached: isOpenScope && slaState === "breached" && !assignedUserId && !unassigned,
    all: !status && !assignedUserId && !unassigned && !slaState,
  };

  function setQuick(key: QuickKey) {
    const none = { assignedUserId: null, unassigned: null, slaState: null };
    if (key === "all") params.setFilters({ status: null, ...none });
    else if (key === "open") params.setFilters({ status: OPEN_STATUSES, ...none });
    else if (key === "mine")
      params.setFilters({ status: OPEN_STATUSES, ...none, assignedUserId: me?.user.id ?? null });
    else if (key === "unassigned")
      params.setFilters({ status: OPEN_STATUSES, ...none, unassigned: "true" });
    else params.setFilters({ status: OPEN_STATUSES, ...none, slaState: "breached" });
  }

  const columns: Column<CaseListItem>[] = [
    {
      key: "number",
      header: t("service:fields.number"),
      sortField: "number",
      render: (c) => (
        <Anchor component={Link} to={`/app/cases/${c.id}`} size="sm" fw={600}>
          {c.number}
        </Anchor>
      ),
    },
    {
      key: "subject",
      header: t("service:fields.subject"),
      sortField: "subject",
      render: (c) => (
        <Text size="sm" fw={500} lineClamp={2}>
          {c.subject}
        </Text>
      ),
    },
    {
      key: "account",
      header: t("service:fields.account"),
      render: (c) =>
        c.accountId && canReadAccounts && c.accountName ? (
          <Anchor component={Link} to={`/app/accounts/${c.accountId}`} size="sm">
            {c.accountName}
          </Anchor>
        ) : (
          (c.accountName ?? "-")
        ),
    },
    {
      key: "status",
      header: t("service:fields.status"),
      sortField: "status",
      render: (c) => <CaseStatusBadge status={c.status} />,
    },
    {
      key: "priority",
      header: t("service:fields.priority"),
      sortField: "priority",
      render: (c) => <CasePriorityBadge priority={c.priority} />,
    },
    {
      key: "assignee",
      header: t("service:fields.assignee"),
      render: (c) =>
        c.assignedUserId ? (
          (c.assignedUserName ?? "-")
        ) : (
          <Text size="sm" c="dimmed">
            {t("service:unassigned")}
          </Text>
        ),
    },
    { key: "sla", header: t("service:fields.sla"), render: (c) => <SlaBadge item={c} /> },
    {
      key: "createdAt",
      header: t("service:fields.createdAt"),
      sortField: "createdAt",
      render: (c) => formatDateTime(c.createdAt, timeZone),
    },
    {
      key: "dueAt",
      header: t("service:fields.dueAt"),
      sortField: "dueAt",
      render: (c) => formatDateTime(c.dueAt, timeZone),
    },
  ];

  const hasRecordFilter = !!params.filters.accountId || !!params.filters.contactId;

  return (
    <>
      <ListPageFrame
        title={t("service:title")}
        description={t("service:description")}
        createLabel={t("service:new")}
        onCreate={canWriteCases ? () => setCreating(true) : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("service:search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <MultiSelect
              aria-label={t("service:fields.status")}
              placeholder={csv(status).length ? undefined : t("service:fields.status")}
              w={220}
              clearable
              data={CASE_STATUSES.map((s) => ({ value: s, label: t(`service:statuses.${s}`) }))}
              value={csv(status)}
              onChange={(value) => params.setFilter("status", value.join(",") || null)}
            />
            <MultiSelect
              aria-label={t("service:fields.priority")}
              placeholder={csv(priority).length ? undefined : t("service:fields.priority")}
              w={200}
              clearable
              data={CASE_PRIORITIES.map((p) => ({ value: p, label: t(`service:priorities.${p}`) }))}
              value={csv(priority)}
              onChange={(value) => params.setFilter("priority", value.join(",") || null)}
            />
            <Select
              aria-label={t("service:fields.channel")}
              placeholder={t("service:fields.channel")}
              w={140}
              clearable
              data={CASE_CHANNELS.map((c) => ({ value: c, label: t(`service:channels.${c}`) }))}
              value={params.filters.channel || null}
              onChange={(value) => params.setFilter("channel", value)}
            />
            <OwnerSelect
              label={undefined}
              aria-label={t("service:fields.assignee")}
              placeholder={t("service:fields.assignee")}
              w={190}
              clearable
              value={assignedUserId || null}
              // The server rejects "assignee" together with "unassigned".
              onChange={(value) => params.setFilters({ assignedUserId: value, unassigned: null })}
            />
            <Select
              aria-label={t("service:fields.sla")}
              placeholder={t("service:fields.sla")}
              w={150}
              clearable
              data={SLA_STATES.map((s) => ({ value: s, label: t(`service:sla.states.${s}`) }))}
              value={slaState || null}
              onChange={(value) => params.setFilter("slaState", value)}
            />
          </>
        }
      >
        <Group gap="xs" role="group" aria-label={t("service:quick.label")}>
          {(["open", "mine", "unassigned", "breached", "all"] as const).map((key) => (
            <Button
              key={key}
              size="xs"
              radius="xl"
              variant={quick[key] ? "filled" : "default"}
              aria-pressed={quick[key]}
              onClick={() => setQuick(key)}
            >
              {t(`service:quick.${key}`)}
            </Button>
          ))}
        </Group>
        {hasRecordFilter && (
          <Text size="sm" c="dimmed" role="status">
            {t("service:recordFilter")}
          </Text>
        )}

        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(c) => c.id}
          isLoading={isLoading}
          isFetching={isFetching}
          error={error}
          onRetry={() => void refetch()}
          sort={params.sort ?? DEFAULT_SORT}
          onSort={params.toggleSort}
          page={params.page}
          pageSize={params.pageSize}
          totalCount={data?.totalCount}
          onPageChange={params.setPage}
          onPageSizeChange={params.setPageSize}
          emptyMessage={t("service:empty")}
          minWidth={1100}
        />
      </ListPageFrame>

      {creating && <CaseFormDialog onClose={() => setCreating(false)} />}
    </>
  );
}
