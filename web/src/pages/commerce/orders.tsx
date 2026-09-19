import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Select } from "@mantine/core";
import { AccountFilter } from "@/components/commerce/account-filter";
import { OrderStatusBadge } from "@/components/commerce/status-badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { useDeleteOrder, useOrders } from "@/hooks/use-orders";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { ORDER_STATUSES, PERMISSIONS, type OrderSummary } from "@/types";

const FILTERS = ["status", "accountId", "ownerUserId"] as const;

export default function OrdersPage() {
  const { t } = useTranslation(["commerce", "common", "crm"]);
  const navigate = useNavigate();
  const { canWriteOrders } = useCrmPermissions();
  const canReadQuotes = usePermission(PERMISSIONS.crmQuotesRead);
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useOrders(params.query);
  const remove = useDeleteOrder();
  const [deleting, setDeleting] = useState<OrderSummary | null>(null);

  const columns: Column<OrderSummary>[] = [
    {
      key: "number",
      header: t("commerce:fields.number"),
      sortField: "number",
      render: (o) => (
        <Anchor component={Link} to={`/app/orders/${o.id}`} size="sm" fw={500}>
          {o.number}
        </Anchor>
      ),
    },
    {
      key: "subject",
      header: t("commerce:fields.subject"),
      sortField: "subject",
      render: (o) => o.subject,
    },
    { key: "account", header: t("commerce:fields.account"), render: (o) => orDash(o.accountName) },
    {
      key: "status",
      header: t("commerce:fields.status"),
      render: (o) => <OrderStatusBadge status={o.status} />,
    },
    {
      key: "grandTotal",
      header: t("commerce:totals.grandTotal"),
      sortField: "grandTotal",
      render: (o) => formatMoney(o.grandTotal, o.currency),
    },
    {
      key: "orderDate",
      header: t("commerce:fields.orderDate"),
      sortField: "orderDate",
      render: (o) => formatCalendarDate(o.orderDate),
    },
    {
      key: "quote",
      header: t("commerce:fields.quote"),
      render: (o) =>
        o.quoteId && canReadQuotes ? (
          <Anchor component={Link} to={`/app/quotes/${o.quoteId}`} size="sm">
            {o.quoteNumber ?? o.quoteId}
          </Anchor>
        ) : (
          orDash(o.quoteNumber)
        ),
    },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (o) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          // Only drafts can be edited or deleted.
          onEdit={
            canWriteOrders && o.status === "draft"
              ? () => navigate(`/app/orders/${o.id}/edit`)
              : undefined
          }
          onDelete={canWriteOrders && o.status === "draft" ? () => setDeleting(o) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("commerce:orders.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("commerce:orders.title")}
        description={t("commerce:orders.description")}
        createLabel={t("commerce:orders.new")}
        onCreate={canWriteOrders ? () => navigate("/app/orders/new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("commerce:orders.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("commerce:fields.status")}
              placeholder={t("commerce:fields.status")}
              w={170}
              clearable
              data={ORDER_STATUSES.map((s) => ({
                value: s,
                label: t(`commerce:orderStatuses.${s}`),
              }))}
              value={params.filters.status || null}
              onChange={(value) => params.setFilter("status", value)}
            />
            <AccountFilter
              value={params.filters.accountId}
              onChange={(value) => params.setFilter("accountId", value)}
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
          rowKey={(o) => o.id}
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
        title={t("commerce:orders.deleteTitle")}
        message={t("commerce:orders.deleteMessage", { number: deleting?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
