/**
 * Which state actions a detail page offers: the status machine of docs/plan/m6a-ticaret.md crossed
 * with the user's permissions. The server enforces both again; the UI only hides what cannot work.
 */
import type { OrderStatus, QuoteStatus } from "@/types";

export type QuoteActionKey =
  | "edit"
  | "send"
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
