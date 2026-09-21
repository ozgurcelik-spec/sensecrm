import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Badge, Select } from "@mantine/core";
import { PriceBookFormDialog } from "@/components/commerce/pricebook-form-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { useDeletePriceBook, usePriceBooks } from "@/hooks/use-pricebooks";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, orDash } from "@/lib/format";
import { CURRENCIES, type PriceBook } from "@/types";

const FILTERS = ["isActive", "currency"] as const;

export default function PriceBooksPage() {
  const { t } = useTranslation(["inventory", "common", "crm"]);
  const { canWritePriceBooks } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = usePriceBooks(params.query);
  const remove = useDeletePriceBook();
  const [editing, setEditing] = useState<PriceBook | "new" | null>(null);
  const [deleting, setDeleting] = useState<PriceBook | null>(null);

  const columns: Column<PriceBook>[] = [
    {
      key: "name",
      header: t("inventory:priceBooks.fields.name"),
      sortField: "name",
      render: (b) => (
        <Anchor component={Link} to={`/app/pricebooks/${b.id}`} size="sm" fw={500}>
          {b.name}
        </Anchor>
      ),
    },
    {
      key: "model",
      header: t("inventory:priceBooks.fields.pricingModel"),
      render: (b) =>
        b.pricingModel === "flat" && b.adjustmentPercent !== undefined
          ? `${t("inventory:priceBooks.models.flat")} (${b.adjustmentPercent > 0 ? "+" : ""}${b.adjustmentPercent}%)`
          : t(`inventory:priceBooks.models.${b.pricingModel}`),
    },
    { key: "currency", header: t("inventory:priceBooks.fields.currency"), render: (b) => b.currency },
    {
      key: "state",
      header: t("inventory:priceBooks.fields.isActive"),
      render: (b) => (
        <Badge variant="light" color={b.isEffective ? "green" : b.isActive ? "orange" : "gray"}>
          {b.isEffective
            ? t("inventory:priceBooks.state.effective")
            : b.isActive
              ? t("inventory:priceBooks.state.outOfRange")
              : t("inventory:priceBooks.state.inactive")}
        </Badge>
      ),
    },
    {
      key: "validity",
      header: t("inventory:priceBooks.fields.validity"),
      sortField: "validTo",
      render: (b) =>
        b.validFrom || b.validTo
          ? `${formatCalendarDate(b.validFrom)} - ${formatCalendarDate(b.validTo)}`
          : t("inventory:priceBooks.noValidity"),
    },
    { key: "entryCount", header: t("inventory:priceBooks.fields.entryCount"), render: (b) => b.entryCount },
    { key: "owner", header: t("crm:owner"), render: (b) => orDash(b.ownerName) },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (b) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWritePriceBooks ? () => setEditing(b) : undefined}
          onDelete={canWritePriceBooks ? () => setDeleting(b) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("inventory:priceBooks.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("inventory:priceBooks.title")}
        description={t("inventory:priceBooks.description")}
        createLabel={t("inventory:priceBooks.new")}
        onCreate={canWritePriceBooks ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("inventory:priceBooks.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("inventory:priceBooks.fields.isActive")}
              placeholder={t("inventory:priceBooks.fields.isActive")}
              w={170}
              clearable
              data={[
                { value: "true", label: t("inventory:priceBooks.state.activeOption") },
                { value: "false", label: t("inventory:priceBooks.state.inactive") },
              ]}
              value={params.filters.isActive || null}
              onChange={(value) => params.setFilter("isActive", value)}
            />
            <Select
              aria-label={t("inventory:priceBooks.fields.currency")}
              placeholder={t("inventory:priceBooks.fields.currency")}
              w={130}
              clearable
              data={[...CURRENCIES]}
              value={params.filters.currency || null}
              onChange={(value) => params.setFilter("currency", value)}
            />
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(b) => b.id}
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
          minWidth={950}
        />
      </ListPageFrame>

      {editing && (
        <PriceBookFormDialog
          priceBook={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("inventory:priceBooks.deleteTitle")}
        message={t("inventory:priceBooks.deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
