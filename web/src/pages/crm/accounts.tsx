import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Text } from "@mantine/core";
import { AccountFormDialog } from "@/components/crm/account-form-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { SearchInput } from "@/components/crm/search-input";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useAccounts, useDeleteAccount } from "@/hooks/use-accounts";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDate } from "@/lib/dates";
import { orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import type { Account } from "@/types";

const FILTERS = ["ownerUserId", "industry"] as const;

export default function AccountsPage() {
  const { t } = useTranslation(["crm", "common"]);
  const navigate = useNavigate();
  const { canWriteAccounts } = useCrmPermissions();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useAccounts(params.query);
  const remove = useDeleteAccount();

  const [editing, setEditing] = useState<Account | "new" | null>(null);
  const [deleting, setDeleting] = useState<Account | null>(null);

  const columns: Column<Account>[] = [
    {
      key: "name",
      header: t("crm:accounts.fields.name"),
      sortField: "name",
      render: (a) => (
        <Anchor component={Link} to={`/app/accounts/${a.id}`} size="sm" fw={500}>
          {a.name}
        </Anchor>
      ),
    },
    {
      key: "industry",
      header: t("crm:accounts.fields.industry"),
      sortField: "industry",
      render: (a) => <Text size="sm">{orDash(a.industry)}</Text>,
    },
    { key: "phone", header: t("crm:accounts.fields.phone"), render: (a) => orDash(a.phone) },
    { key: "email", header: t("crm:accounts.fields.email"), render: (a) => orDash(a.email) },
    { key: "owner", header: t("crm:owner"), render: (a) => orDash(a.ownerName) },
    {
      key: "createdAt",
      header: t("crm:createdAt"),
      sortField: "createdAt",
      render: (a) => formatDate(a.createdAt, timeZone),
    },
    {
      key: "actions",
      header: "",
      width: 90,
      render: (a) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWriteAccounts ? () => setEditing(a) : undefined}
          onDelete={canWriteAccounts ? () => setDeleting(a) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("crm:accounts.deleted") });
      setDeleting(null);
    } catch (err) {
      toastApiError(err);
      setDeleting(null);
    }
  }

  return (
    <>
      <ListPageFrame
        title={t("crm:accounts.title")}
        description={t("crm:accounts.description")}
        createLabel={t("crm:accounts.new")}
        onCreate={canWriteAccounts ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("crm:accounts.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <OwnerSelect
              label={undefined}
              aria-label={t("crm:owner")}
              placeholder={t("crm:owner")}
              w={200}
              clearable
              value={params.filters.ownerUserId || null}
              onChange={(value) => params.setFilter("ownerUserId", value)}
            />
            <SearchInput
              value={params.filters.industry}
              onSearch={(value) => params.setFilter("industry", value)}
              placeholder={t("crm:accounts.fields.industry")}
            />
          </>
        }
      >
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(a) => a.id}
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
        />
      </ListPageFrame>

      {editing && (
        <AccountFormDialog
          account={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(id) => editing === "new" && navigate(`/app/accounts/${id}`)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("crm:accounts.deleteTitle")}
        message={t("crm:accounts.deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
