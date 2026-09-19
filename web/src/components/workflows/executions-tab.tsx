import { useMemo } from "react";
import { Link, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Anchor, Button, Group, Select, Text, TextInput, Tooltip } from "@mantine/core";
import { Eye } from "lucide-react";
import { DataTable, type Column } from "@/components/crm/data-table";
import { usePermission } from "@/hooks/use-permission";
import { useListParams } from "@/hooks/use-list-params";
import { useExecutions, useWorkflowRules } from "@/hooks/use-workflows";
import { formatDateTime } from "@/lib/dates";
import { dayRangeIso, subjectPath, subjectReadPermission, validYmd } from "@/lib/workflow";
import { useAuthStore } from "@/store/auth.store";
import type { ExecutionListQuery } from "@/services/workflows.service";
import { EXECUTION_STATUSES, type WorkflowExecution } from "@/types";
import { ExecutionStatusBadge, KindBadge } from "./badges";
import { ExecutionDrawer } from "./execution-drawer";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["status", "ruleId", "from", "to"] as const;

/** Where the drawer's execution id is kept, so an execution can be linked and survives a reload. */
export const EXECUTION_PARAM = "execution";

function SubjectCell({ execution }: { execution: WorkflowExecution }) {
  const { t } = useTranslation(["workflows"]);
  const readPermission = subjectReadPermission(execution.subjectType);
  const canOpen = usePermission(readPermission ?? "");
  const path = subjectPath(execution.subjectType, execution.subjectId);
  const label = execution.subjectName ?? execution.subjectId;
  return (
    <Text size="sm" component="div">
      <Text span c="dimmed" size="xs">
        {t(`workflows:executions.subjectTypes.${execution.subjectType}`, {
          defaultValue: execution.subjectType,
        })}
        {": "}
      </Text>
      {path && readPermission && canOpen ? (
        <Anchor component={Link} to={path} size="sm">
          {label}
        </Anchor>
      ) : (
        label
      )}
    </Text>
  );
}

/** The "Yürütmeler" tab: filtered list (status, rule, date range) kept in the URL and a detail drawer. */
export function ExecutionsTab() {
  const { t } = useTranslation(["workflows", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const params = useListParams(FILTERS);
  const [searchParams, setSearchParams] = useSearchParams();
  const openId = searchParams.get(EXECUTION_PARAM);
  const rules = useWorkflowRules();

  const apiQuery = useMemo<ExecutionListQuery>(() => {
    const { status, ruleId, from, to } = params.filters;
    return {
      page: params.page,
      pageSize: params.pageSize,
      status: status || undefined,
      ruleId: ruleId || undefined,
      ...dayRangeIso(from, to, timeZone),
    };
  }, [params.page, params.pageSize, params.filters, timeZone]);

  const { data, isLoading, isFetching, error, refetch } = useExecutions(apiQuery);

  function setOpen(id: string | null) {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        if (id) next.set(EXECUTION_PARAM, id);
        else next.delete(EXECUTION_PARAM);
        return next;
      },
      { replace: true }
    );
  }

  const columns: Column<WorkflowExecution>[] = [
    {
      key: "rule",
      header: t("workflows:executions.column.rule"),
      render: (e) => (
        <Group gap="xs" wrap="nowrap">
          <Text size="sm" fw={500}>
            {e.ruleName}
          </Text>
          <KindBadge kind={e.kind} />
        </Group>
      ),
    },
    {
      key: "subject",
      header: t("workflows:executions.column.subject"),
      render: (e) => <SubjectCell execution={e} />,
    },
    {
      key: "status",
      header: t("workflows:executions.column.status"),
      render: (e) => <ExecutionStatusBadge status={e.status} />,
    },
    {
      key: "startedAt",
      header: t("workflows:executions.column.startedAt"),
      render: (e) => formatDateTime(e.startedAt, timeZone),
    },
    {
      key: "endedAt",
      header: t("workflows:executions.column.endedAt"),
      render: (e) => (e.endedAt ? formatDateTime(e.endedAt, timeZone) : "-"),
    },
    {
      key: "actions",
      header: "",
      width: 60,
      render: (e) => (
        <Tooltip label={t("workflows:executions.open", { name: e.ruleName })}>
          <ActionIcon
            variant="subtle"
            aria-label={t("workflows:executions.open", { name: e.ruleName })}
            onClick={() => setOpen(e.id)}
          >
            <Eye size={16} />
          </ActionIcon>
        </Tooltip>
      ),
    },
  ];

  return (
    <>
      <Group gap="sm" align="flex-end" wrap="wrap" mb="md">
        <Select
          aria-label={t("workflows:executions.filters.status")}
          placeholder={t("workflows:executions.filters.status")}
          w={170}
          clearable
          data={EXECUTION_STATUSES.map((s) => ({
            value: s,
            label: t(`workflows:executions.statuses.${s}`),
          }))}
          value={params.filters.status || null}
          onChange={(value) => params.setFilter("status", value)}
        />
        <Select
          aria-label={t("workflows:executions.filters.rule")}
          placeholder={t("workflows:executions.filters.rule")}
          w={220}
          clearable
          searchable
          data={(rules.data ?? []).map((r) => ({ value: r.id, label: r.name }))}
          value={params.filters.ruleId || null}
          onChange={(value) => params.setFilter("ruleId", value)}
        />
        <TextInput
          type="date"
          label={t("workflows:executions.filters.from")}
          value={validYmd(params.filters.from)}
          onChange={(event) => params.setFilter("from", event.currentTarget.value || null)}
        />
        <TextInput
          type="date"
          label={t("workflows:executions.filters.to")}
          value={validYmd(params.filters.to)}
          onChange={(event) => params.setFilter("to", event.currentTarget.value || null)}
        />
        {params.hasActiveFilters && (
          <Button variant="subtle" size="sm" onClick={params.clearFilters}>
            {t("common:clearFilters")}
          </Button>
        )}
      </Group>

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(e) => e.id}
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
        emptyMessage={t("workflows:executions.empty")}
        minWidth={900}
      />

      {openId && <ExecutionDrawer executionId={openId} onClose={() => setOpen(null)} />}
    </>
  );
}
