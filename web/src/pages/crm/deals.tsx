import { useMemo, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Card, SegmentedControl, Select, Skeleton, Text } from "@mantine/core";
import { LayoutGrid, List } from "lucide-react";
import { StageBadge } from "@/components/crm/badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { DealBoardView } from "@/components/crm/deal-board";
import { DealFormDialog } from "@/components/crm/deal-form-dialog";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDealBoard, useDeals, useDeleteDeal } from "@/hooks/use-deals";
import { useListParams } from "@/hooks/use-list-params";
import { usePipelines } from "@/hooks/use-pipelines";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { STAGE_KINDS, type Deal } from "@/types";

const FILTERS = ["pipelineId", "stageId", "stageKind", "ownerUserId"] as const;

type View = "board" | "list";

export default function DealsPage() {
  const { t } = useTranslation(["crm", "common"]);
  const navigate = useNavigate();
  const { canWriteDeals } = useCrmPermissions();
  const [searchParams, setSearchParams] = useSearchParams();
  const view: View = searchParams.get("view") === "list" ? "list" : "board";
  const params = useListParams(FILTERS);
  const pipelines = usePipelines();

  const pipelineId =
    params.filters.pipelineId ||
    pipelines.data?.find((p) => p.isDefault)?.id ||
    pipelines.data?.[0]?.id ||
    "";
  const pipeline = pipelines.data?.find((p) => p.id === pipelineId);

  const boardQuery = useMemo(
    () => ({ pipelineId, ownerUserId: params.filters.ownerUserId || undefined }),
    [pipelineId, params.filters.ownerUserId]
  );
  const board = useDealBoard(boardQuery, view === "board" && !!pipelineId);
  // Wait for the pipeline list so the first request already carries the pipeline filter.
  const list = useDeals(
    { ...params.query, pipelineId: pipelineId || undefined },
    view === "list" && (!!pipelineId || pipelines.isError)
  );
  const remove = useDeleteDeal();

  const [editing, setEditing] = useState<Deal | "new" | null>(null);
  const [deleting, setDeleting] = useState<Deal | null>(null);

  function setView(next: View) {
    setSearchParams(
      (prev) => {
        const p = new URLSearchParams(prev);
        if (next === "board") p.delete("view");
        else p.set("view", next);
        // Paging / sort / search only exist in the list view.
        if (next === "board") {
          p.delete("page");
          p.delete("pageSize");
          p.delete("sort");
          p.delete("q");
        }
        return p;
      },
      { replace: true }
    );
  }

  const columns: Column<Deal>[] = [
    {
      key: "name",
      header: t("crm:deals.fields.name"),
      sortField: "name",
      render: (d) => (
        <Anchor component={Link} to={`/app/deals/${d.id}`} size="sm" fw={500}>
          {d.name}
        </Anchor>
      ),
    },
    {
      key: "account",
      header: t("crm:deals.fields.account"),
      render: (d) => (
        <Anchor component={Link} to={`/app/accounts/${d.accountId}`} size="sm">
          {d.accountName}
        </Anchor>
      ),
    },
    {
      key: "stage",
      header: t("crm:deals.fields.stage"),
      render: (d) => <StageBadge name={d.stageName} kind={d.stageKind} />,
    },
    {
      key: "amount",
      header: t("crm:deals.fields.amount"),
      sortField: "amount",
      render: (d) => formatMoney(d.amount, d.currency),
    },
    {
      key: "closingDate",
      header: t("crm:deals.fields.closingDate"),
      sortField: "closingDate",
      render: (d) => formatCalendarDate(d.closingDate),
    },
    { key: "owner", header: t("crm:owner"), render: (d) => orDash(d.ownerName) },
    {
      key: "actions",
      header: "",
      width: 90,
      render: (d) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWriteDeals ? () => setEditing(d) : undefined}
          onDelete={canWriteDeals ? () => setDeleting(d) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("crm:deals.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  const stageOptions = (pipeline?.stages ?? []).map((s) => ({ value: s.id, label: s.name }));

  const filters = (
    <>
      <SegmentedControl
        aria-label={t("crm:deals.view.label")}
        value={view}
        onChange={(value) => setView(value as View)}
        data={[
          {
            value: "board",
            label: (
              <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
                <LayoutGrid size={14} aria-hidden="true" />
                {t("crm:deals.view.board")}
              </span>
            ),
          },
          {
            value: "list",
            label: (
              <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
                <List size={14} aria-hidden="true" />
                {t("crm:deals.view.list")}
              </span>
            ),
          },
        ]}
      />
      <Select
        aria-label={t("crm:deals.fields.pipeline")}
        placeholder={t("crm:deals.fields.pipeline")}
        w={200}
        allowDeselect={false}
        data={(pipelines.data ?? []).map((p) => ({ value: p.id, label: p.name }))}
        value={pipelineId || null}
        onChange={(value) => {
          params.setFilter("pipelineId", value);
          // A stage of another pipeline no longer applies.
          if (params.filters.stageId) params.setFilter("stageId", null);
        }}
      />
      <OwnerSelect
        label={undefined}
        aria-label={t("crm:owner")}
        placeholder={t("crm:owner")}
        w={200}
        clearable
        value={params.filters.ownerUserId || null}
        onChange={(value) => params.setFilter("ownerUserId", value)}
      />
      {view === "list" && (
        <>
          <Select
            aria-label={t("crm:deals.fields.stage")}
            placeholder={t("crm:deals.fields.stage")}
            w={180}
            clearable
            data={stageOptions}
            value={params.filters.stageId || null}
            onChange={(value) => params.setFilter("stageId", value)}
          />
          <Select
            aria-label={t("crm:deals.fields.stageKind")}
            placeholder={t("crm:deals.fields.stageKind")}
            w={160}
            clearable
            data={STAGE_KINDS.map((k) => ({ value: k, label: t(`crm:deals.kinds.${k}`) }))}
            value={params.filters.stageKind || null}
            onChange={(value) => params.setFilter("stageKind", value)}
          />
        </>
      )}
    </>
  );

  return (
    <>
      <ListPageFrame
        title={t("crm:deals.title")}
        description={t("crm:deals.description")}
        createLabel={t("crm:deals.new")}
        onCreate={canWriteDeals ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("crm:deals.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={filters}
        hideSearch={view === "board"}
      >
        {view === "list" ? (
          <DataTable
            columns={columns}
            rows={list.data?.items}
            rowKey={(d) => d.id}
            isLoading={list.isLoading || pipelines.isLoading}
            isFetching={list.isFetching}
            error={list.error}
            onRetry={() => void list.refetch()}
            sort={params.sort}
            onSort={params.toggleSort}
            page={params.page}
            pageSize={params.pageSize}
            totalCount={list.data?.totalCount}
            onPageChange={params.setPage}
            onPageSizeChange={params.setPageSize}
          />
        ) : board.error ? (
          <LoadError error={board.error} onRetry={() => void board.refetch()} />
        ) : board.data ? (
          <DealBoardView board={board.data} boardQuery={boardQuery} canMove={canWriteDeals} />
        ) : (
          <Card withBorder padding="md" aria-busy="true">
            <Skeleton h={220} />
            <Text size="xs" c="dimmed" mt="xs">
              {t("common:loading")}
            </Text>
          </Card>
        )}
      </ListPageFrame>

      {editing && (
        <DealFormDialog
          deal={editing === "new" ? undefined : editing}
          defaultPipelineId={pipelineId || undefined}
          onClose={() => setEditing(null)}
          onSaved={(id) => editing === "new" && navigate(`/app/deals/${id}`)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("crm:deals.deleteTitle")}
        message={t("crm:deals.deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
