import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Anchor, Group, Select, Tooltip } from "@mantine/core";
import { Repeat } from "lucide-react";
import { LeadRatingBadge, LeadStatusBadge } from "@/components/crm/badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { LeadConvertDialog } from "@/components/crm/lead-convert-dialog";
import { LeadFormDialog } from "@/components/crm/lead-form-dialog";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDeleteLead, useLeads } from "@/hooks/use-leads";
import { useListParams } from "@/hooks/use-list-params";
import { toast, toastApiError } from "@/hooks/use-toast";
import { orDash } from "@/lib/format";
import { LEAD_SOURCES, LEAD_STATUSES, type Lead } from "@/types";

const FILTERS = ["status", "source", "ownerUserId"] as const;

export default function LeadsPage() {
  const { t } = useTranslation(["crm", "common"]);
  const navigate = useNavigate();
  const { canWriteLeads, canConvertLeads, canWriteDeals } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useLeads(params.query);
  const remove = useDeleteLead();

  const [editing, setEditing] = useState<Lead | "new" | null>(null);
  const [deleting, setDeleting] = useState<Lead | null>(null);
  const [converting, setConverting] = useState<Lead | null>(null);

  const columns: Column<Lead>[] = [
    {
      key: "name",
      header: t("crm:leads.fields.name"),
      sortField: "lastName",
      render: (l) => (
        <Anchor component={Link} to={`/app/leads/${l.id}`} size="sm" fw={500}>
          {l.fullName}
        </Anchor>
      ),
    },
    {
      key: "company",
      header: t("crm:leads.fields.company"),
      sortField: "company",
      render: (l) => l.company,
    },
    { key: "email", header: t("crm:leads.fields.email"), render: (l) => orDash(l.email) },
    {
      key: "source",
      header: t("crm:leads.fields.source"),
      render: (l) => t(`crm:leads.sources.${l.source}`, { defaultValue: l.source }),
    },
    {
      key: "status",
      header: t("crm:leads.fields.status"),
      sortField: "status",
      render: (l) => <LeadStatusBadge status={l.status} />,
    },
    {
      key: "rating",
      header: t("crm:leads.fields.rating"),
      render: (l) => <LeadRatingBadge rating={l.rating} />,
    },
    { key: "owner", header: t("crm:owner"), render: (l) => orDash(l.ownerName) },
    {
      key: "actions",
      header: "",
      width: 130,
      render: (l) => {
        const converted = l.status === "converted";
        return (
          <Group gap={4} wrap="nowrap" justify="flex-end">
            {canConvertLeads && !converted && (
              <Tooltip label={t("crm:leads.convert.action")}>
                <ActionIcon
                  variant="subtle"
                  aria-label={t("crm:leads.convert.action")}
                  onClick={() => setConverting(l)}
                >
                  <Repeat size={16} />
                </ActionIcon>
              </Tooltip>
            )}
            <RowActions
              editLabel={t("common:edit")}
              deleteLabel={t("common:delete")}
              // Converted leads are read-only.
              onEdit={canWriteLeads && !converted ? () => setEditing(l) : undefined}
              onDelete={canWriteLeads ? () => setDeleting(l) : undefined}
            />
          </Group>
        );
      },
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("crm:leads.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("crm:leads.title")}
        description={t("crm:leads.description")}
        createLabel={t("crm:leads.new")}
        onCreate={canWriteLeads ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("crm:leads.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("crm:leads.fields.status")}
              placeholder={t("crm:leads.fields.status")}
              w={170}
              clearable
              data={LEAD_STATUSES.map((s) => ({ value: s, label: t(`crm:leads.statuses.${s}`) }))}
              value={params.filters.status || null}
              onChange={(value) => params.setFilter("status", value)}
            />
            <Select
              aria-label={t("crm:leads.fields.source")}
              placeholder={t("crm:leads.fields.source")}
              w={170}
              clearable
              data={LEAD_SOURCES.map((s) => ({ value: s, label: t(`crm:leads.sources.${s}`) }))}
              value={params.filters.source || null}
              onChange={(value) => params.setFilter("source", value)}
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
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(l) => l.id}
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
        <LeadFormDialog
          lead={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(id) => editing === "new" && navigate(`/app/leads/${id}`)}
        />
      )}
      {converting && (
        <LeadConvertDialog
          lead={converting}
          canCreateDeal={canWriteDeals}
          onClose={() => setConverting(null)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("crm:leads.deleteTitle")}
        message={t("crm:leads.deleteMessage", { name: deleting?.fullName })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
