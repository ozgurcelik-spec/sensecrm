import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Text } from "@mantine/core";
import { AccountPicker } from "@/components/crm/account-picker";
import { ContactFormDialog } from "@/components/crm/contact-form-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useAccount } from "@/hooks/use-accounts";
import { useContacts, useDeleteContact } from "@/hooks/use-contacts";
import { BulkAddToCampaign } from "@/components/marketing/bulk-add-to-campaign";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { useRowSelection } from "@/hooks/use-row-selection";
import { toast, toastApiError } from "@/hooks/use-toast";
import { orDash } from "@/lib/format";
import type { Contact } from "@/types";

const FILTERS = ["ownerUserId", "accountId"] as const;

export default function ContactsPage() {
  const { t } = useTranslation(["crm", "common", "campaigns"]);
  const navigate = useNavigate();
  const { canWriteContacts, canWriteCampaigns } = useCrmPermissions();
  const params = useListParams(FILTERS);
  // The selection is bound to the current page / filters / sort: it reads as empty after a change.
  const selection = useRowSelection(JSON.stringify(params.query));
  const { data, isLoading, isFetching, error, refetch } = useContacts(params.query);
  const filterAccount = useAccount(params.filters.accountId || undefined);
  const remove = useDeleteContact();

  const [editing, setEditing] = useState<Contact | "new" | null>(null);
  const [deleting, setDeleting] = useState<Contact | null>(null);

  const columns: Column<Contact>[] = [
    {
      key: "name",
      header: t("crm:contacts.fields.name"),
      sortField: "lastName",
      render: (c) => (
        <Anchor component={Link} to={`/app/contacts/${c.id}`} size="sm" fw={500}>
          {c.fullName}
        </Anchor>
      ),
    },
    {
      key: "title",
      header: t("crm:contacts.fields.title"),
      render: (c) => <Text size="sm">{orDash(c.title)}</Text>,
    },
    {
      key: "account",
      header: t("crm:contacts.fields.account"),
      render: (c) =>
        c.accountId ? (
          <Anchor component={Link} to={`/app/accounts/${c.accountId}`} size="sm">
            {c.accountName ?? c.accountId}
          </Anchor>
        ) : (
          "-"
        ),
    },
    {
      key: "email",
      header: t("crm:contacts.fields.email"),
      sortField: "email",
      render: (c) => orDash(c.email),
    },
    {
      key: "phone",
      header: t("crm:contacts.fields.phone"),
      render: (c) => orDash(c.phone ?? c.mobile),
    },
    { key: "owner", header: t("crm:owner"), render: (c) => orDash(c.ownerName) },
    {
      key: "actions",
      header: "",
      width: 90,
      render: (c) => (
        <RowActions
          editLabel={t("common:edit")}
          deleteLabel={t("common:delete")}
          onEdit={canWriteContacts ? () => setEditing(c) : undefined}
          onDelete={canWriteContacts ? () => setDeleting(c) : undefined}
        />
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("crm:contacts.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("crm:contacts.title")}
        description={t("crm:contacts.description")}
        createLabel={t("crm:contacts.new")}
        onCreate={canWriteContacts ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("crm:contacts.search")}
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
            <AccountPicker
              label={undefined}
              aria-label={t("crm:contacts.fields.account")}
              placeholder={t("crm:contacts.fields.account")}
              w={220}
              clearable
              value={params.filters.accountId || null}
              selectedName={filterAccount.data?.name}
              onChange={(value) => params.setFilter("accountId", value)}
            />
          </>
        }
      >
        <BulkAddToCampaign
          memberType="contact"
          selectedIds={[...selection.selected]}
          onDone={selection.clear}
        />
        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(c) => c.id}
          selection={
            canWriteCampaigns
              ? {
                  selected: selection.selected,
                  onChange: selection.onChange,
                  rowLabel: (c) => t("campaigns:addToCampaign.selectRow", { name: c.fullName }),
                }
              : undefined
          }
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
        <ContactFormDialog
          contact={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(id) => editing === "new" && navigate(`/app/contacts/${id}`)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("crm:contacts.deleteTitle")}
        message={t("crm:contacts.deleteMessage", { name: deleting?.fullName })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
