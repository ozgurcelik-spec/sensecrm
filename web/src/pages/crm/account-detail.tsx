import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Badge, Button, Card, Group, Skeleton, Stack, Table, Text } from "@mantine/core";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { StageBadge } from "@/components/crm/badges";
import { ContactFormDialog } from "@/components/crm/contact-form-dialog";
import { AccountFormDialog } from "@/components/crm/account-form-dialog";
import { DealFormDialog } from "@/components/crm/deal-form-dialog";
import { RecordActivitiesTab } from "@/components/activities/record-activities-tab";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import {
  useAccount,
  useAccountContacts,
  useAccountDeals,
  useDeleteAccount,
} from "@/hooks/use-accounts";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { formatAddress, formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type Account } from "@/types";

function RelatedContacts({ account, canWrite }: { account: Account; canWrite: boolean }) {
  const { t } = useTranslation(["crm"]);
  const contacts = useAccountContacts(account.id);
  const canRead = usePermission(PERMISSIONS.crmContactsRead);
  const [creating, setCreating] = useState(false);

  return (
    <Card withBorder padding="md">
      <Group justify="space-between" mb="sm">
        <Text fw={600}>{t("crm:accounts.related.contacts")}</Text>
        {canWrite && (
          <Button
            size="xs"
            variant="light"
            leftSection={<Plus size={14} />}
            onClick={() => setCreating(true)}
          >
            {t("crm:contacts.new")}
          </Button>
        )}
      </Group>
      {contacts.error ? (
        <LoadError error={contacts.error} onRetry={() => void contacts.refetch()} />
      ) : contacts.isLoading ? (
        <Skeleton h={48} />
      ) : (contacts.data ?? []).length === 0 ? (
        <Text size="sm" c="dimmed">
          {t("crm:accounts.related.noContacts")}
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={480}>
          <Table verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("crm:contacts.fields.name")}</Table.Th>
                <Table.Th>{t("crm:contacts.fields.title")}</Table.Th>
                <Table.Th>{t("crm:contacts.fields.email")}</Table.Th>
                <Table.Th>{t("crm:contacts.fields.phone")}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(contacts.data ?? []).map((c) => (
                <Table.Tr key={c.id}>
                  <Table.Td>
                    {canRead ? (
                      <Anchor component={Link} to={`/app/contacts/${c.id}`} size="sm">
                        {c.fullName}
                      </Anchor>
                    ) : (
                      c.fullName
                    )}
                  </Table.Td>
                  <Table.Td>{orDash(c.title)}</Table.Td>
                  <Table.Td>{orDash(c.email)}</Table.Td>
                  <Table.Td>{orDash(c.phone ?? c.mobile)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
      {creating && (
        <ContactFormDialog
          defaultAccount={{ id: account.id, name: account.name }}
          onClose={() => setCreating(false)}
        />
      )}
    </Card>
  );
}

function RelatedDeals({ account, canWrite }: { account: Account; canWrite: boolean }) {
  const { t } = useTranslation(["crm"]);
  const deals = useAccountDeals(account.id);
  const canRead = usePermission(PERMISSIONS.crmDealsRead);
  const [creating, setCreating] = useState(false);

  return (
    <Card withBorder padding="md">
      <Group justify="space-between" mb="sm">
        <Text fw={600}>{t("crm:accounts.related.deals")}</Text>
        {canWrite && (
          <Button
            size="xs"
            variant="light"
            leftSection={<Plus size={14} />}
            onClick={() => setCreating(true)}
          >
            {t("crm:deals.new")}
          </Button>
        )}
      </Group>
      {deals.error ? (
        <LoadError error={deals.error} onRetry={() => void deals.refetch()} />
      ) : deals.isLoading ? (
        <Skeleton h={48} />
      ) : (deals.data ?? []).length === 0 ? (
        <Text size="sm" c="dimmed">
          {t("crm:accounts.related.noDeals")}
        </Text>
      ) : (
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
              {(deals.data ?? []).map((d) => (
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
      )}
      {creating && (
        <DealFormDialog
          defaultAccount={{ id: account.id, name: account.name }}
          onClose={() => setCreating(false)}
        />
      )}
    </Card>
  );
}

export default function AccountDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["crm", "common", "activities"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteAccounts, canWriteContacts, canWriteDeals } = useCrmPermissions();
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const { data: account, isLoading, error, refetch } = useAccount(id);
  const remove = useDeleteAccount();
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);

  async function confirmDelete() {
    if (!account) return;
    try {
      await remove.mutateAsync(account.id);
      toast({ variant: "success", description: t("crm:accounts.deleted") });
      navigate("/app/accounts", { replace: true });
    } catch (err) {
      // account.has_dependents: contacts or deals still reference the account.
      toastApiError(err);
      setDeleting(false);
    }
  }

  const canReadActivities = usePermission(PERMISSIONS.crmActivitiesRead);
  const tabs = account
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("crm:accounts.fields.billingAddress")}
                </Text>
                <Text size="sm">{formatAddress(account.billingAddress)}</Text>
              </Card>
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("crm:accounts.fields.description")}
                </Text>
                <Text size="sm" style={{ whiteSpace: "pre-wrap" }}>
                  {orDash(account.description)}
                </Text>
              </Card>
            </Stack>
          ),
        },
        {
          value: "related",
          label: t("crm:tabs.related"),
          content: (
            <Stack gap="md">
              {canReadContacts && <RelatedContacts account={account} canWrite={canWriteContacts} />}
              {canReadDeals && <RelatedDeals account={account} canWrite={canWriteDeals} />}
            </Stack>
          ),
        },
        ...(canReadActivities
          ? [
              {
                value: "activities",
                label: t("activities:tab.title"),
                content: (
                  <RecordActivitiesTab
                    related={{ type: "account", id: account.id, name: account.name }}
                  />
                ),
              },
            ]
          : []),
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Account" entityId={account.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/accounts"
        backLabel={t("crm:accounts.title")}
        title={account?.name}
        subtitle={account?.industry}
        badges={
          account && (
            <>
              <Badge variant="light" color="gray">
                {t("crm:accounts.related.contactCount", { count: account.contactCount ?? 0 })}
              </Badge>
              <Badge variant="light" color="gray">
                {t("crm:accounts.related.dealCount", { count: account.dealCount ?? 0 })}
              </Badge>
            </>
          )
        }
        actions={
          account &&
          canWriteAccounts && (
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
          account && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                { label: t("crm:accounts.fields.industry"), value: orDash(account.industry) },
                {
                  label: t("crm:accounts.fields.website"),
                  value: account.website ? (
                    <Anchor
                      href={
                        account.website.startsWith("http")
                          ? account.website
                          : `https://${account.website}`
                      }
                      target="_blank"
                      rel="noopener noreferrer"
                      size="sm"
                    >
                      {account.website}
                    </Anchor>
                  ) : (
                    "-"
                  ),
                },
                { label: t("crm:accounts.fields.phone"), value: orDash(account.phone) },
                { label: t("crm:accounts.fields.email"), value: orDash(account.email) },
                { label: t("crm:owner"), value: orDash(account.ownerName) },
                { label: t("crm:createdAt"), value: formatDateTime(account.createdAt, timeZone) },
                ...(account.updatedAt
                  ? [
                      {
                        label: t("crm:updatedAt"),
                        value: formatDateTime(account.updatedAt, timeZone),
                      },
                    ]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {editing && account && (
        <AccountFormDialog account={account} onClose={() => setEditing(false)} />
      )}
      <ConfirmDialog
        opened={deleting}
        title={t("crm:accounts.deleteTitle")}
        message={t("crm:accounts.deleteMessage", { name: account?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(false)}
      />
    </>
  );
}
