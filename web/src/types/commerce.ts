/**
 * Milestone 6A (commerce) API types: products, quotes and sales orders.
 * HTTP contract: docs/plan/m6a-ticaret.md. Amounts are JSON numbers (decimal), dates `YYYY-MM-DD`.
 */

import type {
  DocumentAddress,
  InvoiceReportSection,
  PurchaseOrderReportSection,
} from "./inventory";

export const QUOTE_STATUSES = ["draft", "sent", "negotiation", "accepted", "rejected", "expired"] as const;
/** Effective status: `expired` is derived by the server from a `sent` / `negotiation` quote past its `validUntil`. */
export type QuoteStatus = (typeof QUOTE_STATUSES)[number];

export const ORDER_STATUSES = ["draft", "confirmed", "fulfilled", "cancelled"] as const;
export type OrderStatus = (typeof ORDER_STATUSES)[number];

export const CURRENCIES = ["TRY", "USD", "EUR", "GBP"] as const;

export interface Product {
  id: string;
  name: string;
  code?: string;
  description?: string;
  unitPrice: number;
  currency: string;
  taxRate: number;
  unit?: string;
  isActive: boolean;
  /** M9C: primary vendor and its purchase price (product currency). */
  vendorId?: string;
  vendorName?: string;
  purchasePrice?: number;
  createdAt: string;
  updatedAt?: string;
}

export interface ProductInput {
  name: string;
  code?: string;
  description?: string;
  unitPrice: number;
  currency: string;
  taxRate: number;
  unit?: string;
  isActive?: boolean;
  vendorId?: string;
  purchasePrice?: number;
}

/** A line as sent to the server. The computed fields are never sent (the server ignores them). */
export interface DocumentLineInput {
  productId?: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  taxRate: number;
}

/** A line as returned by the server (with the computed amounts). */
export interface DocumentLine extends DocumentLineInput {
  id: string;
  position: number;
  lineSubtotal: number;
  lineDiscount: number;
  lineTax: number;
  lineTotal: number;
}

/** Header fields shared by quotes and orders. */
interface DocumentBase {
  id: string;
  number: string;
  subject: string;
  accountId: string;
  accountName?: string;
  contactId?: string;
  contactName?: string;
  dealId?: string;
  dealName?: string;
  ownerUserId: string;
  ownerName?: string;
  currency: string;
  grandTotal: number;
  createdAt: string;
}

interface DocumentDetailFields {
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  /** M9C rounding: signed, added after tax (`grandTotal = sum of line totals + adjustment`). */
  adjustment: number;
  carrier?: string;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  priceBookId?: string;
  priceBookName?: string;
  terms?: string;
  notes?: string;
  updatedAt?: string;
  lines: DocumentLine[];
}

export interface QuoteSummary extends DocumentBase {
  status: QuoteStatus;
  validUntil?: string;
  convertedOrderId?: string;
}

export interface Quote extends QuoteSummary, DocumentDetailFields {
  sentAt?: string;
  acceptedAt?: string;
  rejectedAt?: string;
  rejectionReason?: string;
  convertedOrderNumber?: string;
}

export interface OrderSummary extends DocumentBase {
  status: OrderStatus;
  quoteId?: string;
  quoteNumber?: string;
  invoiceId?: string;
  orderDate: string;
}

export interface SalesOrder extends OrderSummary, DocumentDetailFields {
  invoiceNumber?: string;
  dueDate?: string;
  customerPoNumber?: string;
  exciseTax?: number;
  salesCommission?: number;
  pending?: string;
  fulfilledAt?: string;
  cancelledAt?: string;
  cancelReason?: string;
}

interface DocumentInputBase {
  subject: string;
  accountId: string;
  contactId?: string;
  dealId?: string;
  ownerUserId?: string;
  currency: string;
  terms?: string;
  notes?: string;
  carrier?: string;
  /** Always sent by the editors (0 when unused): PUT replaces the document. */
  adjustment?: number;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  priceBookId?: string;
  lines: DocumentLineInput[];
}

export interface QuoteInput extends DocumentInputBase {
  validUntil?: string;
}

export interface OrderInput extends DocumentInputBase {
  orderDate?: string;
  dueDate?: string;
  customerPoNumber?: string;
  exciseTax?: number;
  salesCommission?: number;
  pending?: string;
}

export interface CommerceStatusRow<S extends string> {
  status: S;
  count: number;
  amount: number;
}

/** `GET /reports/commerce/summary`. */
export interface CommerceSummaryReport {
  currencies: string[];
  quotes: {
    totalCount: number;
    totalAmount: number;
    byStatus: CommerceStatusRow<QuoteStatus>[];
  };
  orders: {
    totalCount: number;
    totalAmount: number;
    byStatus: CommerceStatusRow<OrderStatus>[];
  };
  /** 0-1; absent when there is no non-draft quote in the range. */
  conversionRate?: number;
  /** M9C sections (absent on an older server). */
  invoices?: InvoiceReportSection;
  purchaseOrders?: PurchaseOrderReportSection;
}
