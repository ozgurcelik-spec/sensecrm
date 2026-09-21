import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Anchor, Button, Card, Group, Stack, Table, Text, Tooltip } from "@mantine/core";
import { Banknote, Pencil, RotateCcw, Send, Trash2, X } from "lucide-react";
import { ReasonDialog } from "@/components/commerce/action-dialogs";
import {
  DocumentAddresses,
  LinesTable,
  TermsAndNotes,
  TotalsCard,
} from "@/components/commerce/document-parts";
import { PaymentDialog } from "@/components/commerce/payment-dialog";
import { InvoiceStatusBadge } from "@/components/commerce/status-badges";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import {
  useDeleteInvoice,
  useDeleteInvoicePayment,
  useInvoice,
  useInvoiceAction,
} from "@/hooks/use-invoices";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { availableInvoiceActions } from "@/lib/commerce-actions";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { formatYmd, ymdInZone } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type InvoicePayment } from "@/types";

type Dialog = "cancel" | "delete" | "payment" | null;

export default function InvoiceDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["invoices", "commerce", "common", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteInvoices } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const canReadOrders = usePermission(PERMISSIONS.crmOrdersRead);
  const { data: invoice, isLoading, error, refetch } = useInvoice(id);
  const action = useInvoiceAction();
  const remove = useDeleteInvoice();
  const removePayment = useDeleteInvoicePayment();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [deletingPayment, setDeletingPayment] = useState<InvoicePayment | null>(null);
  const today = formatYmd(ymdInZone(new Date(), timeZone));

  const actions = invoice
    ? availableInvoiceActions(invoice.status, { canWriteInvoices, paidAmount: invoice.paidAmount })
    : [];

  function run(key: "send" | "revert" | "cancel", reason?: string) {
    if (!invoice) return;
    action.mutate(
      { id: invoice.id, action: key, reason },
      {
        onSuccess: () => {
          setDialog(null);
          toast({ variant: "success", description: t(`invoices:done.${key}`) });
        },
        onError: (err) => {
          setDialog(null);
          // invoice.no_lines / invoice.has_payments / invoice.invalid_transition / commerce.concurrent_update ...
          toastApiError(err);
        },
      }
    );
  }

  async function confirmDelete() {
    if (!invoice) return;
    try {
      await remove.mutateAsync(invoice.id);
      toast({ variant: "success", description: t("invoices:invoices.deleted") });
      navigate("/app/invoices", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDialog(null);
    }
  }

  async function confirmDeletePayment() {
    if (!invoice || !deletingPayment) return;
    try {
      await removePayment.mutateAsync({ id: invoice.id, paymentId: deletingPayment.id });
      toast({ variant: "success", description: t("invoices:payment.deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeletingPayment(null);
  }

  const canEditPayments = canWriteInvoices && !!invoice && invoice.status !== "cancelled" && invoice.status !== "draft";

  const tabs = invoice
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <Card withBorder padding="md" data-testid="payments-card">
                <Group justify="space-between" mb="sm">
                  <Text fw={600}>{t("invoices:payment.ledger")}</Text>
                  <Text size="sm" c="dimmed">
                    {t("invoices:payment.summary", {
                      paid: formatMoney(invoice.paidAmount, invoice.currency),
                      balance: formatMoney(invoice.balanceAmount, invoice.currency),
                    })}
                  </Text>
                </Group>
                {invoice.payments.length === 0 ? (
                  <Text size="sm" c="dimmed" ta="center" py="sm">
                    {t("invoices:payment.empty")}
                  </Text>
                ) : (
                  <Table.ScrollContainer minWidth={560}>
                    <Table verticalSpacing="xs">
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>{t("invoices:payment.paidOn")}</Table.Th>
                          <Table.Th ta="right">{t("invoices:payment.amount")}</Table.Th>
                          <Table.Th>{t("invoices:payment.method")}</Table.Th>
                          <Table.Th>{t("invoices:payment.reference")}</Table.Th>
                          <Table.Th>{t("invoices:payment.notes")}</Table.Th>
                          <Table.Th w={50} />
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {invoice.payments.map((payment) => (
                          <Table.Tr key={payment.id} data-testid="payment-row">
                            <Table.Td>{formatCalendarDate(payment.paidOn)}</Table.Td>
                            <Table.Td ta="right">{formatMoney(payment.amount, invoice.currency)}</Table.Td>
                            <Table.Td>
                              {payment.method ? t(`invoices:payment.methods.${payment.method}`) : "-"}
                            </Table.Td>
                            <Table.Td>{orDash(payment.reference)}</Table.Td>
                            <Table.Td>{orDash(payment.notes)}</Table.Td>
                            <Table.Td>
                              {canEditPayments && (
                                <Tooltip label={t("invoices:payment.delete")}>
                                  <ActionIcon
                                    variant="subtle"
                                    color="red"
                                    aria-label={t("invoices:payment.deleteFor", {
                                      amount: formatMoney(payment.amount, invoice.currency),
                                    })}
                                    onClick={() => setDeletingPayment(payment)}
                                  >
                                    <Trash2 size={16} />
                                  </ActionIcon>
                                </Tooltip>
                              )}
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Table.ScrollContainer>
                )}
              </Card>
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("commerce:lines.title")}
                </Text>
                <LinesTable lines={invoice.lines} currency={invoice.currency} />
              </Card>
              <TotalsCard totals={invoice} currency={invoice.currency} />
              <DocumentAddresses
                billingAddress={invoice.billingAddress}
                shippingAddress={invoice.shippingAddress}
                carrier={invoice.carrier}
              />
              <TermsAndNotes terms={invoice.terms} notes={invoice.notes} />
            </Stack>
          ),
        },
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="Invoice" entityId={invoice.id} />,
        },
      ]
    : [];

  const busy = action.isPending;

  return (
    <>
      <RecordDetailShell
        backTo="/app/invoices"
        backLabel={t("invoices:invoices.title")}
        title={invoice ? `${invoice.number} - ${invoice.subject}` : undefined}
        subtitle={invoice?.accountName}
        badges={invoice && <InvoiceStatusBadge status={invoice.status} />}
        actions={
          invoice && (
            <>
              {actions.includes("pay") && (
                <Button leftSection={<Banknote size={16} />} disabled={busy} onClick={() => setDialog("payment")}>
                  {t("invoices:actions.pay")}
                </Button>
              )}
              {actions.includes("send") && (
                <Button
                  leftSection={<Send size={16} />}
                  loading={busy && action.variables?.action === "send"}
                  disabled={busy}
                  onClick={() => run("send")}
                >
                  {t("invoices:actions.send")}
                </Button>
              )}
              {actions.includes("edit") && (
                <Button
                  variant="default"
                  leftSection={<Pencil size={16} />}
                  onClick={() => navigate(`/app/invoices/${invoice.id}/edit`)}
                >
                  {t("common:edit")}
                </Button>
              )}
              {actions.includes("revert") && (
                <Button
                  variant="default"
                  leftSection={<RotateCcw size={16} />}
                  loading={busy && action.variables?.action === "revert"}
                  disabled={busy}
                  onClick={() => run("revert")}
                >
                  {t("invoices:actions.revert")}
                </Button>
              )}
              {actions.includes("cancel") && (
                <Button
                  variant="default"
                  color="red"
                  leftSection={<X size={16} />}
                  disabled={busy}
                  onClick={() => setDialog("cancel")}
                >
                  {t("invoices:actions.cancel")}
                </Button>
              )}
              {actions.includes("delete") && (
                <Button
                  variant="default"
                  color="red"
                  leftSection={<Trash2 size={16} />}
                  onClick={() => setDialog("delete")}
                >
                  {t("common:delete")}
                </Button>
              )}
            </>
          )
        }
        isLoading={isLoading}
        error={error}
        onRetry={() => void refetch()}
        panel={
          invoice && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("commerce:fields.account"),
                  value: canReadAccounts ? (
                    <Anchor component={Link} to={`/app/accounts/${invoice.accountId}`} size="sm">
                      {invoice.accountName ?? invoice.accountId}
                    </Anchor>
                  ) : (
                    orDash(invoice.accountName)
                  ),
                },
                {
                  label: t("commerce:fields.contact"),
                  value:
                    invoice.contactId && canReadContacts ? (
                      <Anchor component={Link} to={`/app/contacts/${invoice.contactId}`} size="sm">
                        {invoice.contactName ?? invoice.contactId}
                      </Anchor>
                    ) : (
                      orDash(invoice.contactName)
                    ),
                },
                {
                  label: t("commerce:fields.deal"),
                  value:
                    invoice.dealId && canReadDeals ? (
                      <Anchor component={Link} to={`/app/deals/${invoice.dealId}`} size="sm">
                        {invoice.dealName ?? invoice.dealId}
                      </Anchor>
                    ) : (
                      orDash(invoice.dealName)
                    ),
                },
                ...(invoice.orderId
                  ? [
                      {
                        label: t("commerce:fields.order"),
                        value: canReadOrders ? (
                          <Anchor component={Link} to={`/app/orders/${invoice.orderId}`} size="sm">
                            {invoice.orderNumber ?? invoice.orderId}
                          </Anchor>
                        ) : (
                          orDash(invoice.orderNumber)
                        ),
                      },
                    ]
                  : []),
                { label: t("invoices:fields.invoiceDate"), value: formatCalendarDate(invoice.invoiceDate) },
                { label: t("invoices:fields.dueDate"), value: formatCalendarDate(invoice.dueDate) },
                { label: t("commerce:fields.customerPoNumber"), value: orDash(invoice.customerPoNumber) },
                {
                  label: t("commerce:fields.exciseTax"),
                  value: invoice.exciseTax === undefined ? "-" : formatMoney(invoice.exciseTax, invoice.currency),
                },
                {
                  label: t("commerce:fields.salesCommission"),
                  value:
                    invoice.salesCommission === undefined
                      ? "-"
                      : formatMoney(invoice.salesCommission, invoice.currency),
                },
                { label: t("commerce:fields.priceBook"), value: orDash(invoice.priceBookName) },
                { label: t("commerce:fields.currency"), value: invoice.currency },
                {
                  label: t("commerce:totals.grandTotal"),
                  value: formatMoney(invoice.grandTotal, invoice.currency),
                },
                { label: t("invoices:fields.paidAmount"), value: formatMoney(invoice.paidAmount, invoice.currency) },
                { label: t("invoices:fields.balance"), value: formatMoney(invoice.balanceAmount, invoice.currency) },
                { label: t("crm:owner"), value: orDash(invoice.ownerName) },
                ...(invoice.sentAt
                  ? [{ label: t("commerce:fields.sentAt"), value: formatDateTime(invoice.sentAt, timeZone) }]
                  : []),
                ...(invoice.cancelledAt
                  ? [
                      {
                        label: t("commerce:fields.cancelledAt"),
                        value: formatDateTime(invoice.cancelledAt, timeZone),
                      },
                      { label: t("commerce:fields.cancelReason"), value: orDash(invoice.cancelReason) },
                    ]
                  : []),
                { label: t("crm:createdAt"), value: formatDateTime(invoice.createdAt, timeZone) },
                ...(invoice.updatedAt
                  ? [{ label: t("crm:updatedAt"), value: formatDateTime(invoice.updatedAt, timeZone) }]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {dialog === "payment" && invoice && (
        <PaymentDialog invoice={invoice} today={today} onClose={() => setDialog(null)} />
      )}
      {dialog === "cancel" && invoice && (
        <ReasonDialog
          title={t("invoices:dialogs.cancelTitle")}
          label={t("invoices:dialogs.cancelReason")}
          confirmLabel={t("invoices:actions.cancel")}
          loading={action.isPending}
          onConfirm={(reason) => run("cancel", reason)}
          onClose={() => setDialog(null)}
        />
      )}
      <ConfirmDialog
        opened={dialog === "delete"}
        title={t("invoices:invoices.deleteTitle")}
        message={t("invoices:invoices.deleteMessage", { number: invoice?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDialog(null)}
      />
      <ConfirmDialog
        opened={!!deletingPayment}
        title={t("invoices:payment.deleteTitle")}
        message={t("invoices:payment.deleteMessage", {
          amount: deletingPayment ? formatMoney(deletingPayment.amount, invoice?.currency) : "",
        })}
        confirmLabel={t("common:delete")}
        destructive
        loading={removePayment.isPending}
        onConfirm={() => void confirmDeletePayment()}
        onClose={() => setDeletingPayment(null)}
      />
    </>
  );
}
