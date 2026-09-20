import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, TextInput } from "@mantine/core";
import { VendorFormDialog } from "@/components/commerce/vendor-form-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useDeleteVendor, useVendors } from "@/hooks/use-vendors";
import { orDash } from "@/lib/format";
import type { Vendor } from "@/types";

const FILTERS = ["category", "ownerUserId"] as const;

export default function VendorsPage() {
  const { t } = useTranslation(["inventory", "common", "crm"]);
  const { canWriteVendors } = useCrmPermissions();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useVendors(params.query);
  const remove = useDeleteVendor();
  const [editing, setEditing] = useState<Vendor | "new" | null>(null);
  const [deleting, setDeleting] = useState<Vendor | null>(null);
  // The category filter is free text: typed values reach the URL when the field is left.
  const [category, setCategory] = useState(params.filters.category);

  const columns: Column<Vendor>[] = [
    {
      key: "name",
      header: t("inventory:vendors.fields.name"),
      sortField: "name",
      render: (v) => (
        <Anchor component={Link} to={`/app/vendors/${v.id}`} size="sm" fw={500}>
          {v.name}
        </Anchor>
      ),
    },
    {
      key: "category",
      header: t("inventory:vendors.fields.category"),
      sortField: "category",
      render: (v) => orDash(v.category),
    },
    { key: "phone", header: t("inventory:vendors.fields.phone"), render: (v) => orDash(v.phone) },
    { key: "email", header: t("inventory:vendors.fields.email"), render: (v) => orDash(v.email) },
    { key: "owner", header: t("crm:owner"), render: (v) => orDash(v.ownerName) },
    {
      key: "actions",
      header: "",
      width: 100,
      render: (v) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWriteVendors ? () => setEditing(v) : undefined}
          onDelete={canWriteVendors ? () => setDeleting(v) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("inventory:vendors.deleted") });
    } catch (err) {
      // vendor.in_use: a purchase order still points to the vendor.
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("inventory:vendors.title")}
        description={t("inventory:vendors.description")}
        createLabel={t("inventory:vendors.new")}
        onCreate={canWriteVendors ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("inventory:vendors.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={() => {
          setCategory("");
          params.clearFilters();
        }}
        filters={
          <>
            <TextInput
              aria-label={t("inventory:vendors.fields.category")}
              placeholder={t("inventory:vendors.fields.category")}
              w={180}
              value={category}
              onChange={(event) => setCategory(event.currentTarget.value)}
              onBlur={() => params.setFilter("category", category.trim() || null)}
              onKeyDown={(event) => {
                if (event.key === "Enter") params.setFilter("category", category.trim() || null);
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
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(v) => v.id}
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
        <VendorFormDialog vendor={editing === "new" ? undefined : editing} onClose={() => setEditing(null)} />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("inventory:vendors.deleteTitle")}
        message={t("inventory:vendors.deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
