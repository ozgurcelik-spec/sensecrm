/**
 * One document editor for quotes, orders, invoices and purchase orders (docs/plan/m9c-envanter.md,
 * "Belge editörü"): which header fields each kind shows comes from this single constant, so a new
 * field or kind never means a copied editor.
 */

export type DocumentKind = "quote" | "order" | "invoice" | "purchaseOrder";

export const DOCUMENT_FIELDS = [
  "subject",
  "account",
  "vendor",
  "contact",
  "deal",
  "validUntil",
  "orderDate",
  "invoiceDate",
  "poDate",
  "dueDate",
  "customerPoNumber",
  "exciseTax",
  "salesCommission",
  "pending",
  "priceBook",
  "carrier",
  "owner",
  "currency",
] as const;
export type DocumentField = (typeof DOCUMENT_FIELDS)[number];

/** Header fields per kind, in display order. */
export const DOCUMENT_FIELD_LAYOUT: Record<DocumentKind, readonly DocumentField[]> = {
  quote: ["subject", "account", "contact", "deal", "validUntil", "priceBook", "carrier", "owner", "currency"],
  order: [
    "subject",
    "account",
    "contact",
    "deal",
    "orderDate",
    "dueDate",
    "customerPoNumber",
    "exciseTax",
    "salesCommission",
    "pending",
    "priceBook",
    "carrier",
    "owner",
    "currency",
  ],
  invoice: [
    "subject",
    "account",
    "contact",
    "deal",
    "invoiceDate",
    "dueDate",
    "customerPoNumber",
    "exciseTax",
    "salesCommission",
    "priceBook",
    "carrier",
    "owner",
    "currency",
  ],
  purchaseOrder: [
    "subject",
    "vendor",
    "contact",
    "poDate",
    "dueDate",
    "exciseTax",
    "salesCommission",
    "carrier",
    "owner",
    "currency",
  ],
};

export interface DocumentKindConfig {
  /** Namespace and section key of the kind's texts (`<ns>:<section>.newTitle` ...). */
  ns: "commerce" | "invoices" | "inventory";
  section: "quotes" | "orders" | "invoices" | "purchaseOrders";
  listPath: string;
  /** The document date field, when the kind has one (its `dueDate` may not precede it). */
  dateField?: "orderDate" | "invoiceDate" | "poDate";
}

export const DOCUMENT_KINDS: Record<DocumentKind, DocumentKindConfig> = {
  quote: { ns: "commerce", section: "quotes", listPath: "/app/quotes" },
  order: { ns: "commerce", section: "orders", listPath: "/app/orders", dateField: "orderDate" },
  invoice: { ns: "invoices", section: "invoices", listPath: "/app/invoices", dateField: "invoiceDate" },
  purchaseOrder: {
    ns: "inventory",
    section: "purchaseOrders",
    listPath: "/app/purchase-orders",
    dateField: "poDate",
  },
};

/** Cash suffix of the amount fields ("TL" for the lira, the ISO code otherwise). */
export function currencySuffix(currency: string): string {
  return currency === "TRY" ? "TL" : currency;
}

/** Sales documents carry a price book; purchase orders do not (vendor side prices). */
export const hasPriceBook = (kind: DocumentKind): boolean => DOCUMENT_FIELD_LAYOUT[kind].includes("priceBook");
