import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import type { InvoiceStatus, OrderStatus, PurchaseOrderStatus, QuoteStatus } from "@/types";

const QUOTE_COLOR: Record<QuoteStatus, string> = {
  draft: "gray",
  sent: "blue",
  // M9C: "Müzakere" - in talks, still open.
  negotiation: "violet",
  accepted: "green",
  rejected: "red",
  // Own colour: an expired quote needs attention, it is neither open nor lost.
  expired: "orange",
};

const ORDER_COLOR: Record<OrderStatus, string> = {
  draft: "gray",
  confirmed: "blue",
  fulfilled: "green",
  cancelled: "red",
};

export function QuoteStatusBadge({ status }: { status: QuoteStatus }) {
  const { t } = useTranslation(["commerce"]);
  return (
    <Badge variant="light" color={QUOTE_COLOR[status] ?? "gray"}>
      {t(`commerce:quoteStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  const { t } = useTranslation(["commerce"]);
  return (
    <Badge variant="light" color={ORDER_COLOR[status] ?? "gray"}>
      {t(`commerce:orderStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** Invoice colours: overdue red, partially paid orange, paid green (docs/plan/m9c-envanter.md). */
const INVOICE_COLOR: Record<InvoiceStatus, string> = {
  draft: "gray",
  sent: "blue",
  partiallyPaid: "orange",
  paid: "green",
  overdue: "red",
  cancelled: "dark",
};

const PURCHASE_ORDER_COLOR: Record<PurchaseOrderStatus, string> = {
  draft: "gray",
  confirmed: "blue",
  received: "green",
  cancelled: "red",
};

export function InvoiceStatusBadge({ status }: { status: InvoiceStatus }) {
  const { t } = useTranslation(["invoices"]);
  return (
    <Badge variant="light" color={INVOICE_COLOR[status] ?? "gray"} data-status={status}>
      {t(`invoices:statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function PurchaseOrderStatusBadge({ status }: { status: PurchaseOrderStatus }) {
  const { t } = useTranslation(["inventory"]);
  return (
    <Badge variant="light" color={PURCHASE_ORDER_COLOR[status] ?? "gray"} data-status={status}>
      {t(`inventory:purchaseOrders.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}
