import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Stack, Text, Tooltip } from "@mantine/core";
import { Check, PackageCheck, Pencil, Trash2, X } from "lucide-react";
import { ReasonDialog } from "@/components/commerce/action-dialogs";
import { CreateInvoiceButton } from "@/components/commerce/create-invoice-button";
import {
  DocumentAddresses,
  LinesTable,
  TermsAndNotes,
  TotalsCard,
} from "@/components/commerce/document-parts";
import { OrderStatusBadge } from "@/components/commerce/status-badges";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { useAttachmentsTab } from "@/hooks/use-attachments-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useDeleteOrder, useOrder, useOrderAction } from "@/hooks/use-orders";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { availableOrderActions } from "@/lib/commerce-actions";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

type Dialog = "cancel" | "delete" | null;

export default function OrderDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["commerce", "common", "crm", "invoices"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteOrders } = useCrmPermissions();
  const canReadAccounts = usePermission(PERMISSIONS.crmAccountsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const canReadDeals = usePermission(PERMISSIONS.crmDealsRead);
  const canReadQuotes = usePermission(PERMISSIONS.crmQuotesRead);
  const { data: order, isLoading, error, refetch } = useOrder(id);
  const action = useOrderAction();
  const remove = useDeleteOrder();
  const [dialog, setDialog] = useState<Dialog>(null);

  const actions = order ? availableOrderActions(order.status, canWriteOrders) : [];

  function run(key: "confirm" | "fulfill" | "cancel", reason?: string) {
    if (!order) return;
    action.mutate(
      { id: order.id, action: key, reason },
      {
        onSuccess: () => {
          setDialog(null);
          toast({ variant: "success", description: t(`commerce:orders.done.${key}`) });
        },
        onError: (err) => {
          setDialog(null);
          toastApiError(err);
        },
      }
    );
  }

  async function confirmDelete() {
    if (!order) return;
    try {
      await remove.mutateAsync(order.id);
      toast({ variant: "success", description: t("commerce:orders.deleted") });
      navigate("/app/orders", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDialog(null);
    }
  }

  const attachmentsTab = useAttachmentsTab("order", id);
  const tabs = order
    ? [
        {
          value: "general",
          label: t("crm:tabs.general"),
          content: (
            <Stack gap="md">
              <Card withBorder padding="md">
                <Text fw={600} mb="sm">
                  {t("commerce:lines.title")}
                </Text>
                <LinesTable lines={order.lines} currency={order.currency} />
              </Card>
              <TotalsCard totals={order} currency={order.currency} />
              <DocumentAddresses
                billingAddress={order.billingAddress}
                shippingAddress={order.shippingAddress}
                carrier={order.carrier}
              />
              <TermsAndNotes terms={order.terms} notes={order.notes} />
            </Stack>
          ),
        },
        ...attachmentsTab,
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="SalesOrder" entityId={order.id} />,
        },
      ]
    : [];

  return (
    <>
      <RecordDetailShell
        backTo="/app/orders"
        backLabel={t("commerce:orders.title")}
        title={order ? `${order.number} - ${order.subject}` : undefined}
        subtitle={order?.accountName}
        badges={order && <OrderStatusBadge status={order.status} />}
        actions={
          order && (
            <>
              {actions.includes("confirm") && (
                <Button
                  leftSection={<Check size={16} />}
                  loading={action.isPending && action.variables?.action === "confirm"}
                  disabled={action.isPending}
                  onClick={() => run("confirm")}
                >
                  {t("commerce:actions.confirm")}
                </Button>
              )}
              {actions.includes("fulfill") && (
                <Button
                  leftSection={<PackageCheck size={16} />}
                  loading={action.isPending && action.variables?.action === "fulfill"}
                  disabled={action.isPending}
                  onClick={() => run("fulfill")}
                >
                  {t("commerce:actions.fulfill")}
                </Button>
              )}
              {/* M9C: "Fatura oluştur" (confirmed / fulfilled, crm.invoices.write) or the active invoice's link. */}
              <CreateInvoiceButton order={order} />
              {actions.includes("edit") && (
                <Button
                  variant="default"
                  leftSection={<Pencil size={16} />}
                  onClick={() => navigate(`/app/orders/${order.id}/edit`)}
                >
                  {t("common:edit")}
                </Button>
              )}
              {actions.includes("cancel") && (
                <Tooltip label={t("invoices:hasActiveInvoice")} disabled={!order.invoiceId}>
                  {/* An order with an active invoice cannot be cancelled (order.has_active_invoice): cancel the invoice first. */}
                  <span title={order.invoiceId ? t("invoices:hasActiveInvoice") : undefined}>
                    <Button
                      variant="default"
                      color="red"
                      leftSection={<X size={16} />}
                      disabled={action.isPending || !!order.invoiceId}
                      onClick={() => setDialog("cancel")}
                    >
                      {t("commerce:actions.cancel")}
                    </Button>
                  </span>
                </Tooltip>
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
          order && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("commerce:fields.account"),
                  value: canReadAccounts ? (
                    <Anchor component={Link} to={`/app/accounts/${order.accountId}`} size="sm">
                      {order.accountName ?? order.accountId}
                    </Anchor>
                  ) : (
                    orDash(order.accountName)
                  ),
                },
                {
                  label: t("commerce:fields.contact"),
                  value:
                    order.contactId && canReadContacts ? (
                      <Anchor component={Link} to={`/app/contacts/${order.contactId}`} size="sm">
                        {order.contactName ?? order.contactId}
                      </Anchor>
                    ) : (
                      orDash(order.contactName)
                    ),
                },
                {
                  label: t("commerce:fields.deal"),
                  value:
                    order.dealId && canReadDeals ? (
                      <Anchor component={Link} to={`/app/deals/${order.dealId}`} size="sm">
                        {order.dealName ?? order.dealId}
                      </Anchor>
                    ) : (
                      orDash(order.dealName)
                    ),
                },
                ...(order.quoteId
                  ? [
                      {
                        label: t("commerce:fields.quote"),
                        value: canReadQuotes ? (
                          <Anchor component={Link} to={`/app/quotes/${order.quoteId}`} size="sm">
                            {order.quoteNumber ?? order.quoteId}
                          </Anchor>
                        ) : (
                          orDash(order.quoteNumber)
                        ),
                      },
                    ]
                  : []),
                { label: t("commerce:fields.orderDate"), value: formatCalendarDate(order.orderDate) },
                { label: t("commerce:fields.dueDate"), value: formatCalendarDate(order.dueDate) },
                { label: t("commerce:fields.customerPoNumber"), value: orDash(order.customerPoNumber) },
                {
                  label: t("commerce:fields.exciseTax"),
                  value: order.exciseTax === undefined ? "-" : formatMoney(order.exciseTax, order.currency),
                },
                {
                  label: t("commerce:fields.salesCommission"),
                  value:
                    order.salesCommission === undefined ? "-" : formatMoney(order.salesCommission, order.currency),
                },
                { label: t("commerce:fields.pending"), value: orDash(order.pending) },
                { label: t("commerce:fields.priceBook"), value: orDash(order.priceBookName) },
                { label: t("commerce:fields.currency"), value: order.currency },
                {
                  label: t("commerce:totals.grandTotal"),
                  value: formatMoney(order.grandTotal, order.currency),
                },
                { label: t("crm:owner"), value: orDash(order.ownerName) },
                ...(order.fulfilledAt
                  ? [
                      {
                        label: t("commerce:fields.fulfilledAt"),
                        value: formatDateTime(order.fulfilledAt, timeZone),
                      },
                    ]
                  : []),
                ...(order.cancelledAt
                  ? [
                      {
                        label: t("commerce:fields.cancelledAt"),
                        value: formatDateTime(order.cancelledAt, timeZone),
                      },
                      { label: t("commerce:fields.cancelReason"), value: orDash(order.cancelReason) },
                    ]
                  : []),
                { label: t("crm:createdAt"), value: formatDateTime(order.createdAt, timeZone) },
                ...(order.updatedAt
                  ? [{ label: t("crm:updatedAt"), value: formatDateTime(order.updatedAt, timeZone) }]
                  : []),
              ]}
            />
          )
        }
        tabs={tabs}
      />
      {dialog === "cancel" && order && (
        <ReasonDialog
          title={t("commerce:dialogs.cancelTitle")}
          label={t("commerce:dialogs.cancelReason")}
          confirmLabel={t("commerce:actions.cancel")}
          loading={action.isPending}
          onConfirm={(reason) => run("cancel", reason)}
          onClose={() => setDialog(null)}
        />
      )}
      <ConfirmDialog
        opened={dialog === "delete"}
        title={t("commerce:orders.deleteTitle")}
        message={t("commerce:orders.deleteMessage", { number: order?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDialog(null)}
      />
    </>
  );
}
