import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Checkbox, Select } from "@mantine/core";
import { AccountFilter } from "@/components/commerce/account-filter";
import { QuoteStatusBadge } from "@/components/commerce/status-badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { useDeleteQuote, useQuotes } from "@/hooks/use-quotes";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { QUOTE_STATUSES, type QuoteSummary } from "@/types";

const FILTERS = ["status", "accountId", "ownerUserId", "converted"] as const;

export default function QuotesPage() {
  const { t } = useTranslation(["commerce", "common", "crm"]);
  const navigate = useNavigate();
  const { canWriteQuotes } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useQuotes(params.query);
  const remove = useDeleteQuote();
  const [deleting, setDeleting] = useState<QuoteSummary | null>(null);

  // "Accepted, not yet converted": the follow-up list of the sales team.
  const pendingConversion = params.filters.status === "accepted" && params.filters.converted === "false";

  const columns: Column<QuoteSummary>[] = [
    {
      key: "number",
      header: t("commerce:fields.number"),
      sortField: "number",
      render: (q) => (
        <Anchor component={Link} to={`/app/quotes/${q.id}`} size="sm" fw={500}>
          {q.number}
        </Anchor>
      ),
    },
    {
      key: "subject",
      header: t("commerce:fields.subject"),
      sortField: "subject",
      render: (q) => q.subject,
    },
    { key: "account", header: t("commerce:fields.account"), render: (q) => orDash(q.accountName) },
    {
      key: "status",
      header: t("commerce:fields.status"),
      render: (q) => <QuoteStatusBadge status={q.status} />,
    },
    {
      key: "grandTotal",
      header: t("commerce:totals.grandTotal"),
      sortField: "grandTotal",
      render: (q) => formatMoney(q.grandTotal, q.currency),
    },
    {
      key: "validUntil",
      header: t("commerce:fields.validUntil"),
      sortField: "validUntil",
      render: (q) => formatCalendarDate(q.validUntil),
    },
    { key: "owner", header: t("crm:owner"), render: (q) => orDash(q.ownerName) },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (q) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          // Only drafts can be edited or deleted.
          onEdit={
            canWriteQuotes && q.status === "draft"
              ? () => navigate(`/app/quotes/${q.id}/edit`)
              : undefined
          }
          onDelete={canWriteQuotes && q.status === "draft" ? () => setDeleting(q) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("commerce:quotes.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("commerce:quotes.title")}
        description={t("commerce:quotes.description")}
        createLabel={t("commerce:quotes.new")}
        onCreate={canWriteQuotes ? () => navigate("/app/quotes/new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("commerce:quotes.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("commerce:fields.status")}
              placeholder={t("commerce:fields.status")}
              w={170}
              clearable
              data={QUOTE_STATUSES.map((s) => ({
                value: s,
                label: t(`commerce:quoteStatuses.${s}`),
              }))}
              value={params.filters.status || null}
              // Changing the status by hand ends the "pending conversion" shortcut.
              onChange={(value) => params.setFilters({ status: value, converted: null })}
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
              label={t("commerce:quotes.pendingConversion")}
              checked={pendingConversion}
              onChange={(event) =>
                params.setFilters(
                  event.currentTarget.checked
                    ? { status: "accepted", converted: "false" }
                    : { status: null, converted: null }
                )
              }
            />
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(q) => q.id}
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
        title={t("commerce:quotes.deleteTitle")}
        message={t("commerce:quotes.deleteMessage", { number: deleting?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
