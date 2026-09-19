import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, MultiSelect } from "@mantine/core";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { CampaignFormDialog } from "@/components/marketing/campaign-form-dialog";
import { CampaignStatusBadge, CampaignTypeBadge } from "@/components/marketing/campaign-badges";
import { useCampaigns, useDeleteCampaign } from "@/hooks/use-campaigns";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCampaignDates, splitList } from "@/lib/campaign";
import { formatMoney, formatNumber, orDash } from "@/lib/format";
import { CAMPAIGN_STATUSES, CAMPAIGN_TYPES, type Campaign } from "@/types";

const FILTERS = ["type", "status", "ownerUserId"] as const;

export default function CampaignsPage() {
  const { t } = useTranslation(["campaigns", "common", "crm"]);
  const navigate = useNavigate();
  const { canWriteCampaigns } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useCampaigns(params.query);
  const remove = useDeleteCampaign();

  const [editing, setEditing] = useState<Campaign | "new" | null>(null);
  const [deleting, setDeleting] = useState<Campaign | null>(null);

  const columns: Column<Campaign>[] = [
    {
      key: "name",
      header: t("campaigns:fields.name"),
      sortField: "name",
      render: (c) => (
        <Anchor component={Link} to={`/app/campaigns/${c.id}`} size="sm" fw={500}>
          {c.name}
        </Anchor>
      ),
    },
    {
      key: "type",
      header: t("campaigns:fields.type"),
      sortField: "type",
      render: (c) => <CampaignTypeBadge type={c.type} />,
    },
    {
      key: "status",
      header: t("campaigns:fields.status"),
      sortField: "status",
      render: (c) => <CampaignStatusBadge status={c.status} />,
    },
    {
      key: "dates",
      header: t("campaigns:fields.dates"),
      sortField: "startDate",
      render: (c) => formatCampaignDates(c),
    },
    {
      key: "budget",
      header: t("campaigns:fields.budget"),
      sortField: "budget",
      render: (c) => (c.budget === undefined ? "-" : formatMoney(c.budget, c.currency)),
    },
    {
      key: "members",
      header: t("campaigns:fields.memberCount"),
      render: (c) => formatNumber(c.memberCount),
    },
    { key: "owner", header: t("campaigns:fields.owner"), render: (c) => orDash(c.ownerName) },
    {
      key: "actions",
      header: "",
      width: 90,
      render: (c) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWriteCampaigns ? () => setEditing(c) : undefined}
          onDelete={canWriteCampaigns ? () => setDeleting(c) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("campaigns:deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("campaigns:title")}
        description={t("campaigns:description")}
        createLabel={t("campaigns:new")}
        onCreate={canWriteCampaigns ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("campaigns:search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <MultiSelect
              aria-label={t("campaigns:filters.type")}
              placeholder={t("campaigns:filters.type")}
              w={220}
              clearable
              data={CAMPAIGN_TYPES.map((v) => ({ value: v, label: t(`campaigns:types.${v}`) }))}
              value={splitList(params.filters.type)}
              onChange={(values) => params.setFilter("type", values.join(",") || null)}
            />
            <MultiSelect
              aria-label={t("campaigns:filters.status")}
              placeholder={t("campaigns:filters.status")}
              w={220}
              clearable
              data={CAMPAIGN_STATUSES.map((v) => ({
                value: v,
                label: t(`campaigns:statuses.${v}`),
              }))}
              value={splitList(params.filters.status)}
              onChange={(values) => params.setFilter("status", values.join(",") || null)}
            />
            <OwnerSelect
              label={undefined}
              aria-label={t("campaigns:filters.owner")}
              placeholder={t("campaigns:filters.owner")}
              w={200}
              clearable
              value={params.filters.ownerUserId || null}
              onChange={(value) => params.setFilter("ownerUserId", value)}
            />
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(c) => c.id}
          isLoading={isLoading}
          isFetching={isFetching}
          error={error}
          onRetry={() => void refetch()}
          sort={params.sort}
          onSort={params.toggleSort}
          page={params.page}
          pageSize={params.pageSize}
          totalCount={data?.totalCount}
          onPageChange={params.setPage}
          onPageSizeChange={params.setPageSize}
          minWidth={980}
        />
      </ListPageFrame>

      {editing && (
        <CampaignFormDialog
          campaign={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(id) => editing === "new" && navigate(`/app/campaigns/${id}`)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("campaigns:deleteTitle")}
        message={t("campaigns:deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
