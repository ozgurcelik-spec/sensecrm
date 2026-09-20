import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Stack, Text } from "@mantine/core";
import { Check, PackageCheck, Pencil, Trash2, X } from "lucide-react";
import { ReasonDialog } from "@/components/commerce/action-dialogs";
import {
  DocumentAddresses,
  LinesTable,
  TermsAndNotes,
  TotalsCard,
} from "@/components/commerce/document-parts";
import { PurchaseOrderStatusBadge } from "@/components/commerce/status-badges";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { InfoPanel, RecordDetailShell } from "@/components/crm/record-detail-shell";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { usePermission } from "@/hooks/use-permission";
import {
  useDeletePurchaseOrder,
  usePurchaseOrder,
  usePurchaseOrderAction,
} from "@/hooks/use-purchase-orders";
import { toast, toastApiError } from "@/hooks/use-toast";
import { availablePurchaseOrderActions } from "@/lib/commerce-actions";
import { formatDateTime } from "@/lib/dates";
import { formatCalendarDate, formatMoney, orDash } from "@/lib/format";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

type Dialog = "cancel" | "delete" | null;

export default function PurchaseOrderDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useTranslation(["inventory", "commerce", "common", "crm"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWritePurchaseOrders } = useCrmPermissions();
  const canReadVendors = usePermission(PERMISSIONS.crmVendorsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const { data: order, isLoading, error, refetch } = usePurchaseOrder(id);
  const action = usePurchaseOrderAction();
  const remove = useDeletePurchaseOrder();
  const [dialog, setDialog] = useState<Dialog>(null);

  const actions = order ? availablePurchaseOrderActions(order.status, canWritePurchaseOrders) : [];

  function run(key: "confirm" | "receive" | "cancel", reason?: string) {
    if (!order) return;
    action.mutate(
      { id: order.id, action: key, reason },
      {
        onSuccess: () => {
          setDialog(null);
          toast({ variant: "success", description: t(`inventory:purchaseOrders.done.${key}`) });
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
      toast({ variant: "success", description: t("inventory:purchaseOrders.deleted") });
      navigate("/app/purchase-orders", { replace: true });
    } catch (err) {
      toastApiError(err);
      setDialog(null);
    }
  }

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
        {
          value: "audit",
          label: t("crm:tabs.audit"),
          content: <RecordAuditTab entityType="PurchaseOrder" entityId={order.id} />,
        },
      ]
    : [];

  const busy = action.isPending;

  return (
    <>
      <RecordDetailShell
        backTo="/app/purchase-orders"
        backLabel={t("inventory:purchaseOrders.title")}
        title={order ? `${order.number} - ${order.subject}` : undefined}
        subtitle={order?.vendorName}
        badges={order && <PurchaseOrderStatusBadge status={order.status} />}
        actions={
          order && (
            <>
              {actions.includes("confirm") && (
                <Button
                  leftSection={<Check size={16} />}
                  loading={busy && action.variables?.action === "confirm"}
                  disabled={busy}
                  onClick={() => run("confirm")}
                >
                  {t("inventory:purchaseOrders.actions.confirm")}
                </Button>
              )}
              {actions.includes("receive") && (
                <Button
                  leftSection={<PackageCheck size={16} />}
                  loading={busy && action.variables?.action === "receive"}
                  disabled={busy}
                  onClick={() => run("receive")}
                >
                  {t("inventory:purchaseOrders.actions.receive")}
                </Button>
              )}
              {actions.includes("edit") && (
                <Button
                  variant="default"
                  leftSection={<Pencil size={16} />}
                  onClick={() => navigate(`/app/purchase-orders/${order.id}/edit`)}
                >
                  {t("common:edit")}
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
                  {t("inventory:purchaseOrders.actions.cancel")}
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
          order && (
            <InfoPanel
              title={t("crm:panel.details")}
              rows={[
                {
                  label: t("inventory:purchaseOrders.fields.vendor"),
                  value: canReadVendors ? (
                    <Anchor component={Link} to={`/app/vendors/${order.vendorId}`} size="sm">
                      {order.vendorName ?? order.vendorId}
                    </Anchor>
                  ) : (
                    orDash(order.vendorName)
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
                { label: t("inventory:purchaseOrders.fields.poDate"), value: formatCalendarDate(order.poDate) },
                { label: t("commerce:fields.dueDate"), value: formatCalendarDate(order.dueDate) },
                {
                  label: t("commerce:fields.exciseTax"),
                  value: order.exciseTax === undefined ? "-" : formatMoney(order.exciseTax, order.currency),
                },
                {
                  label: t("commerce:fields.salesCommission"),
                  value:
                    order.salesCommission === undefined ? "-" : formatMoney(order.salesCommission, order.currency),
                },
                { label: t("commerce:fields.currency"), value: order.currency },
                { label: t("commerce:totals.grandTotal"), value: formatMoney(order.grandTotal, order.currency) },
                { label: t("crm:owner"), value: orDash(order.ownerName) },
                ...(order.confirmedAt
                  ? [
                      {
                        label: t("inventory:purchaseOrders.fields.confirmedAt"),
                        value: formatDateTime(order.confirmedAt, timeZone),
                      },
                    ]
                  : []),
                ...(order.receivedAt
                  ? [
                      {
                        label: t("inventory:purchaseOrders.fields.receivedAt"),
                        value: formatDateTime(order.receivedAt, timeZone),
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
          title={t("inventory:purchaseOrders.dialogs.cancelTitle")}
          label={t("inventory:purchaseOrders.dialogs.cancelReason")}
          confirmLabel={t("inventory:purchaseOrders.actions.cancel")}
          loading={action.isPending}
          onConfirm={(reason) => run("cancel", reason)}
          onClose={() => setDialog(null)}
        />
      )}
      <ConfirmDialog
        opened={dialog === "delete"}
        title={t("inventory:purchaseOrders.deleteTitle")}
        message={t("inventory:purchaseOrders.deleteMessage", { number: order?.number })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDialog(null)}
      />
    </>
  );
}
