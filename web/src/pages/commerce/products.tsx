import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Badge, Select, Switch } from "@mantine/core";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { ProductFormDialog } from "@/components/commerce/product-form-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { useDeleteProduct, useProducts, useSetProductActive } from "@/hooks/use-products";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatMoney, formatNumber, orDash } from "@/lib/format";
import type { Product } from "@/types";

const FILTERS = ["isActive"] as const;

export default function ProductsPage() {
  const { t } = useTranslation(["commerce", "common"]);
  const { canWriteProducts } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useProducts(params.query);
  const remove = useDeleteProduct();
  const setActive = useSetProductActive();

  const [editing, setEditing] = useState<Product | "new" | null>(null);
  const [deleting, setDeleting] = useState<Product | null>(null);

  function toggleActive(product: Product) {
    // PUT is a full replacement: send the row's own values with the flipped flag.
    setActive.mutate(
      {
        id: product.id,
        name: product.name,
        code: product.code,
        description: product.description,
        unitPrice: product.unitPrice,
        currency: product.currency,
        taxRate: product.taxRate,
        unit: product.unit,
        isActive: !product.isActive,
      },
      {
        onSuccess: () =>
          toast({
            variant: "success",
            description: t(
              product.isActive ? "commerce:products.deactivated" : "commerce:products.activated",
              { name: product.name }
            ),
          }),
        onError: (err) => toastApiError(err),
      }
    );
  }

  const columns: Column<Product>[] = [
    {
      key: "name",
      header: t("commerce:products.fields.name"),
      sortField: "name",
      render: (p) => p.name,
    },
    {
      key: "code",
      header: t("commerce:products.fields.code"),
      sortField: "code",
      render: (p) => orDash(p.code),
    },
    {
      key: "unitPrice",
      header: t("commerce:products.fields.unitPrice"),
      sortField: "unitPrice",
      render: (p) => formatMoney(p.unitPrice, p.currency),
    },
    {
      key: "taxRate",
      header: t("commerce:products.fields.taxRate"),
      render: (p) => `${formatNumber(p.taxRate)}%`,
    },
    { key: "unit", header: t("commerce:products.fields.unit"), render: (p) => orDash(p.unit) },
    {
      key: "status",
      header: t("commerce:products.fields.isActive"),
      render: (p) =>
        canWriteProducts ? (
          <Switch
            checked={p.isActive}
            onChange={() => toggleActive(p)}
            disabled={setActive.isPending}
            aria-label={t("commerce:products.toggleActive", { name: p.name })}
            label={p.isActive ? t("commerce:products.active") : t("commerce:products.inactive")}
          />
        ) : (
          <Badge variant="light" color={p.isActive ? "green" : "gray"}>
            {p.isActive ? t("commerce:products.active") : t("commerce:products.inactive")}
          </Badge>
        ),
    },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (p) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWriteProducts ? () => setEditing(p) : undefined}
          onDelete={canWriteProducts ? () => setDeleting(p) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("commerce:products.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("commerce:products.title")}
        description={t("commerce:products.description")}
        createLabel={t("commerce:products.new")}
        onCreate={canWriteProducts ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("commerce:products.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <Select
            aria-label={t("commerce:products.fields.isActive")}
            placeholder={t("commerce:products.fields.isActive")}
            w={170}
            clearable
            data={[
              { value: "true", label: t("commerce:products.active") },
              { value: "false", label: t("commerce:products.inactive") },
            ]}
            value={params.filters.isActive || null}
            onChange={(value) => params.setFilter("isActive", value)}
          />
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(p) => p.id}
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
          minWidth={900}
        />
      </ListPageFrame>

      {editing && (
        <ProductFormDialog
          product={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("commerce:products.deleteTitle")}
        message={t("commerce:products.deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
