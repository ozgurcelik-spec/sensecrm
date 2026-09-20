/**
 * Which state actions a detail page offers: the status machine of docs/plan/m6a-ticaret.md crossed
 * with the user's permissions. The server enforces both again; the UI only hides what cannot work.
 */
import type { InvoiceStatus, OrderStatus, PurchaseOrderStatus, QuoteStatus } from "@/types";

export type QuoteActionKey =
  | "edit"
  | "send"
  | "negotiate"
  | "delete"
  | "accept"
  | "reject"
  | "extend"
  | "revert"
  | "convert";

export interface QuoteActionContext {
  canWriteQuotes: boolean;
  canWriteOrders: boolean;
  /** The quote already has a (not deleted) sales order. */
  converted: boolean;
}

export function availableQuoteActions(
  status: QuoteStatus,
  { canWriteQuotes, canWriteOrders, converted }: QuoteActionContext
): QuoteActionKey[] {
  // Converting needs crm.orders.write (and crm.quotes.read, which opening the page implies).
  if (status === "accepted") return canWriteOrders && !converted ? ["convert"] : [];
  if (!canWriteQuotes) return [];
  switch (status) {
    case "draft":
      return ["edit", "send", "delete"];
    case "sent":
      // M9C: "Müzakere" (negotiation) sits between sent and the decision.
      return ["negotiate", "accept", "reject", "extend", "revert"];
    case "negotiation":
      return ["accept", "reject", "extend", "revert"];
    case "expired":
      return ["extend", "revert", "reject"];
    case "rejected":
      return ["revert"];
    default:
      return [];
  }
}

export type OrderActionKey = "edit" | "confirm" | "cancel" | "delete" | "fulfill";

export function availableOrderActions(status: OrderStatus, canWriteOrders: boolean): OrderActionKey[] {
  if (!canWriteOrders) return [];
  switch (status) {
    case "draft":
      return ["edit", "confirm", "cancel", "delete"];
    case "confirmed":
      return ["fulfill", "cancel"];
    default:
      return [];
  }
}

export type OrderInvoiceAction = "create" | "link" | null;

/**
 * "Fatura oluştur" on an order (M9C): only a confirmed / fulfilled order without an active invoice and
 * with `crm.invoices.write`; an order that already has an active invoice shows the invoice link
 * instead (`crm.invoices.read`, otherwise nothing).
 */
export function orderInvoiceAction(
  order: { status: OrderStatus; invoiceId?: string },
  { canWriteInvoices, canReadInvoices }: { canWriteInvoices: boolean; canReadInvoices: boolean }
): OrderInvoiceAction {
  if (order.invoiceId) return canReadInvoices ? "link" : null;
  const invoiceable = order.status === "confirmed" || order.status === "fulfilled";
  return invoiceable && canWriteInvoices ? "create" : null;
}

export type InvoiceActionKey = "edit" | "send" | "cancel" | "delete" | "pay" | "revert";

/**
 * Invoice detail actions: status x permission (docs/plan/m9c-envanter.md, "Faturalar sayfaları").
 * draft: edit, send, cancel, delete; sent / partiallyPaid / overdue: record payment, back to draft
 * and cancel (both only without payments); paid and cancelled: view only.
 */
export function availableInvoiceActions(
  status: InvoiceStatus,
  { canWriteInvoices, paidAmount }: { canWriteInvoices: boolean; paidAmount: number }
): InvoiceActionKey[] {
  if (!canWriteInvoices) return [];
  switch (status) {
    case "draft":
      return ["edit", "send", "cancel", "delete"];
    case "sent":
    case "partiallyPaid":
    case "overdue":
      return paidAmount > 0 ? ["pay"] : ["pay", "revert", "cancel"];
    default:
      return [];
  }
}

export type PurchaseOrderActionKey = "edit" | "confirm" | "receive" | "cancel" | "delete";

export function availablePurchaseOrderActions(
  status: PurchaseOrderStatus,
  canWritePurchaseOrders: boolean
): PurchaseOrderActionKey[] {
  if (!canWritePurchaseOrders) return [];
  switch (status) {
    case "draft":
      return ["edit", "confirm", "cancel", "delete"];
    case "confirmed":
      return ["receive", "cancel"];
    default:
      return [];
  }
}
