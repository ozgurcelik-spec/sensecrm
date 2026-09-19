import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Skeleton, Table, Text } from "@mantine/core";
import { Pencil, Trash2 } from "lucide-react";
import { StageBadge } from "@/components/crm/badges";
import { ContactFormDialog } from "@/components/crm/contact-form-dialog";
import { RecordActivitiesTab } from "@/components/activities/record-activities-tab";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { useAccountDeals } from "@/hooks/use-accounts";
import { useContact, useDeleteContact } from "@/hooks/use-contacts";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { formatAddress, formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type Contact } from "@/types";

/** Deals of the contact's account where this contact is the named contact person. */
function ContactDeals({ contact }: { contact: Contact }) {
  const { t } = useTranslation(["crm"]);
  const canRead = usePermission(PERMISSIONS.crmDealsRead);
  const deals = useAccountDeals(contact.accountId);

  if (!contact.accountId) {
    return (
      <Text size="sm" c="dimmed">
        {t("crm:contacts.related.noAccount")}
      </Text>
    );
  }
  if (deals.error) return <LoadError error={deals.error} onRetry={() => void deals.refetch()} />;
  if (deals.isLoading) return <Skeleton h={48} />;
  const rows = (deals.data ?? []).filter((d) => d.contactId === contact.id);
  if (rows.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        {t("crm:contacts.related.noDeals")}
      </Text>
    );
  }
  return (
    <Table.ScrollContainer minWidth={480}>
      <Table verticalSpacing="xs">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t("crm:deals.fields.name")}</Table.Th>
            <Table.Th>{t("crm:deals.fields.stage")}</Table.Th>
            <Table.Th>{t("crm:deals.fields.amount")}</Table.Th>
            <Table.Th>{t("crm:deals.fields.closingDate")}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((d) => (
            <Table.Tr key={d.id}>
              <Table.Td>
                {canRead ? (
                  <Anchor component={Link} to={`/app/deals/${d.id}`} size="sm">
                    {d.name}
                  </Anchor>
                ) : (
                  d.name
                )}
              </Table.Td>
              <Table.Td>
                <StageBadge name={d.stageName} kind={d.stageKind} />
              </Table.Td>
              <Table.Td>{formatMoney(d.amount, d.currency)}</Table.Td>
              <Table.Td>{formatCalendarDate(d.closingDate)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

export default function ContactDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["crm", "common", "activities"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteContacts } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const { data: contact, isLoading, error, refetch } = useContact(id);
  const remove = useDeleteContact();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);

  async function confirmDelete() {
    if (!contact) return;
    try {
      await remove.mutateAsync(contact.id);
      toast({ variant: "success", description: t("crm:contacts.deleted") });
      navigate("/app/contacts", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDeleting(false);
    }
  }

  const canReadActivities = usePermission(PERMISSIONS.crmActivitiesRead);
  const tabs = contact
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Card withBorder padding="md">
              <Text fw={600} mb="sm">
                {t("crm:contacts.fields.mailingAddress")}
              </Text>
              <Text size="sm">{formatAddress(contact.mailingAddress)}</Text>
            </Card>
          ),
        },
        ...(canReadDeals
          ? [
              {
                value: "related",
                label: t("crm:tabs.related"),
                content: (
                  <Card withBorder padding="md">
                    <Text fw={600} mb="sm">
                      {t("crm:contacts.related.deals")}
                    </Text>
                    <ContactDeals contact={contact} />
                  </Card>
                ),
              },
            ]
          : []),
        ...(canReadActivities
          ? [
              {
                value: "activities",
                label: t("activities:tab.title"),
                content: (
                  <RecordActivitiesTab
                    related={{ type: "contact", id: contact.id, name: contact.fullName }}
                  />
                ),
              },
            ]
          : []),
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Contact" entityId={contact.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/contacts"
        backLabel={t("crm:contacts.title")}
        title={contact?.fullName}
        subtitle={contact?.title}
        actions={
          contact &&
          canWriteContacts && (
            <>
              <Button
                variant="default"
                leftSection={<Pencil size={16} />}
                onClick={() => setEditing(true)}
              >
                {t("common:edit")}
              </Button>
              <Button
                variant="default"
                color="red"
                leftSection={<Trash2 size={16} />}
                onClick={() => setDeleting(true)}
              >
                {t("common:delete")}
              </Button>
            </>
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          contact && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("crm:contacts.fields.account"),
                  value:
                    contact.accountId && canReadAccounts ? (
                      <Anchor component={Link} to={`/app/accounts/${contact.accountId}`} size="sm">
                        {contact.accountName ?? contact.accountId}
                      </Anchor>
                    ) : (
                      orDash(contact.accountName)
                    ),
                },
                { label: t("crm:contacts.fields.title"), value: orDash(contact.title) },
                { label: t("crm:contacts.fields.email"), value: orDash(contact.email) },
                { label: t("crm:contacts.fields.phone"), value: orDash(contact.phone) },
                { label: t("crm:contacts.fields.mobile"), value: orDash(contact.mobile) },
                { label: t("crm:owner"), value: orDash(contact.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(contact.createdAt, timeZone) },
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && contact && (
        <ContactFormDialog contact={contact} onClose={() => setEditing(false)} />
      )}
      <ConfirmDialog
        opened={deleting}
        title={t("crm:contacts.deleteTitle")}
        message={t("crm:contacts.deleteMessage", { name: contact?.fullName })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
