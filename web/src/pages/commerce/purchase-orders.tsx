import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Select } from "@mantine/core";
import { PurchaseOrderStatusBadge } from "@/components/commerce/status-badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { useDeletePurchaseOrder, usePurchaseOrders } from "@/hooks/use-purchase-orders";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { PURCHASE_ORDER_STATUSES, type PurchaseOrderSummary } from "@/types";

const FILTERS = ["status", "vendorId", "ownerUserId"] as const;

export default function PurchaseOrdersPage() {
  const { t } = useTranslation(["inventory", "commerce", "common", "crm"]);
  const navigate = useNavigate();
  const { canWritePurchaseOrders } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = usePurchaseOrders(params.query);
  const remove = useDeletePurchaseOrder();
  const [deleting, setDeleting] = useState<PurchaseOrderSummary | null>(null);

  const columns: Column<PurchaseOrderSummary>[] = [
    {
      key: "number",
      header: t("commerce:fields.number"),
      sortField: "number",
      render: (po) => (
        <Anchor component={Link} to={`/app/purchase-orders/${po.id}`} size="sm" fw={500}>
          {po.number}
        </Anchor>
      ),
    },
    { key: "subject", header: t("commerce:fields.subject"), sortField: "subject", render: (po) => po.subject },
    { key: "vendor", header: t("inventory:purchaseOrders.fields.vendor"), render: (po) => orDash(po.vendorName) },
    {
      key: "status",
      header: t("commerce:fields.status"),
      render: (po) => <PurchaseOrderStatusBadge status={po.status} />,
    },
    {
      key: "grandTotal",
      header: t("commerce:totals.grandTotal"),
      sortField: "grandTotal",
      render: (po) => formatMoney(po.grandTotal, po.currency),
    },
    {
      key: "poDate",
      header: t("inventory:purchaseOrders.fields.poDate"),
      sortField: "poDate",
      render: (po) => formatCalendarDate(po.poDate),
    },
    {
      key: "dueDate",
      header: t("commerce:fields.dueDate"),
      sortField: "dueDate",
      render: (po) => formatCalendarDate(po.dueDate),
    },
    { key: "owner", header: t("crm:owner"), render: (po) => orDash(po.ownerName) },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (po) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={
            canWritePurchaseOrders && po.status === "draft"
              ? () => navigate(`/app/purchase-orders/${po.id}/edit`)
              : undefined
          }
          onDelete={canWritePurchaseOrders && po.status === "draft" ? () => setDeleting(po) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("inventory:purchaseOrders.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("inventory:purchaseOrders.title")}
        description={t("inventory:purchaseOrders.description")}
        createLabel={t("inventory:purchaseOrders.new")}
        onCreate={canWritePurchaseOrders ? () => navigate("/app/purchase-orders/new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("inventory:purchaseOrders.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("commerce:fields.status")}
              placeholder={t("commerce:fields.status")}
              w={180}
              clearable
              data={PURCHASE_ORDER_STATUSES.map((s) => ({
                value: s,
                label: t(`inventory:purchaseOrders.statuses.${s}`),
              }))}
              value={params.filters.status || null}
              onChange={(value) => params.setFilter("status", value)}
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
          rowKey={(po) => po.id}
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
          minWidth={1000}
        />
      </ListPageFrame>

      <ConfirmDialog
        opened={!!deleting}
        title={t("inventory:purchaseOrders.deleteTitle")}
        message={t("inventory:purchaseOrders.deleteMessage", { number: deleting?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
