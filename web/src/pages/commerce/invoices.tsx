import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Checkbox, Select, Text } from "@mantine/core";
import { AccountFilter } from "@/components/commerce/account-filter";
import { InvoiceStatusBadge } from "@/components/commerce/status-badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDeleteInvoice, useInvoices } from "@/hooks/use-invoices";
import { useListParams } from "@/hooks/use-list-params";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { INVOICE_STATUSES, type InvoiceSummary } from "@/types";

const FILTERS = ["status", "accountId", "ownerUserId"] as const;

export default function InvoicesPage() {
  const { t } = useTranslation(["invoices", "commerce", "common", "crm"]);
  const navigate = useNavigate();
  const { canWriteInvoices } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useInvoices(params.query);
  const remove = useDeleteInvoice();
  const [deleting, setDeleting] = useState<InvoiceSummary | null>(null);

  const columns: Column<InvoiceSummary>[] = [
    {
      key: "number",
      header: t("commerce:fields.number"),
      sortField: "number",
      render: (invoice) => (
        <Anchor component={Link} to={`/app/invoices/${invoice.id}`} size="sm" fw={500}>
          {invoice.number}
        </Anchor>
      ),
    },
    { key: "subject", header: t("commerce:fields.subject"), sortField: "subject", render: (i) => i.subject },
    { key: "account", header: t("commerce:fields.account"), render: (i) => orDash(i.accountName) },
    {
      key: "status",
      header: t("commerce:fields.status"),
      render: (i) => <InvoiceStatusBadge status={i.status} />,
    },
    {
      key: "grandTotal",
      header: t("commerce:totals.grandTotal"),
      sortField: "grandTotal",
      render: (i) => formatMoney(i.grandTotal, i.currency),
    },
    {
      key: "balance",
      header: t("invoices:fields.balance"),
      sortField: "balanceAmount",
      render: (i) => (
        <Text size="sm" fw={i.balanceAmount > 0 ? 600 : 400} c={i.status === "overdue" ? "red" : undefined}>
          {formatMoney(i.balanceAmount, i.currency)}
        </Text>
      ),
    },
    {
      key: "invoiceDate",
      header: t("invoices:fields.invoiceDate"),
      sortField: "invoiceDate",
      render: (i) => formatCalendarDate(i.invoiceDate),
    },
    {
      key: "dueDate",
      header: t("invoices:fields.dueDate"),
      sortField: "dueDate",
      render: (i) => formatCalendarDate(i.dueDate),
    },
    { key: "owner", header: t("crm:owner"), render: (i) => orDash(i.ownerName) },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (i) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          // Only drafts can be edited or deleted.
          onEdit={canWriteInvoices && i.status === "draft" ? () => navigate(`/app/invoices/${i.id}/edit`) : undefined}
          onDelete={canWriteInvoices && i.status === "draft" ? () => setDeleting(i) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("invoices:invoices.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("invoices:invoices.title")}
        description={t("invoices:invoices.description")}
        createLabel={t("invoices:invoices.new")}
        onCreate={canWriteInvoices ? () => navigate("/app/invoices/new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("invoices:invoices.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("commerce:fields.status")}
              placeholder={t("commerce:fields.status")}
              w={180}
              clearable
              data={INVOICE_STATUSES.map((s) => ({ value: s, label: t(`invoices:statuses.${s}`) }))}
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
            <Checkbox
              mb={6}
              label={t("invoices:invoices.overdueOnly")}
              checked={params.filters.status === "overdue"}
              onChange={(event) => params.setFilter("status", event.currentTarget.checked ? "overdue" : null)}
            />
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(i) => i.id}
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
          minWidth={1100}
        />
      </ListPageFrame>

      <ConfirmDialog
        opened={!!deleting}
        title={t("invoices:invoices.deleteTitle")}
        message={t("invoices:invoices.deleteMessage", { number: deleting?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
