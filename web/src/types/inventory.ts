/**
 * Milestone 9C (sales documents and inventory) API types: invoices, price books, vendors, purchase
 * orders and the shared address block. HTTP contract: docs/plan/m9c-envanter.md. Amounts are JSON
 * numbers (decimal), dates `YYYY-MM-DD`, timestamps ISO 8601 UTC. Exported names are prefixed
 * (`Invoice*`, `PriceBook*`, `Vendor*`, `PurchaseOrder*`) so they never clash with other modules.
 */
import type { DocumentLine, DocumentLineInput } from "./commerce";

/** Billing / shipping / vendor address block; every line is optional text (an empty block is absent). */
export interface DocumentAddress {
  street?: string;
  /** "Daire / Ev No / Bina / Apartman adı". */
  building?: string;
  city?: string;
  /** State / province. */
  state?: string;
  postalCode?: string;
  country?: string;
}

// ---- Invoices ----------------------------------------------------------------------------------

export const INVOICE_STATUSES = [
  "draft",
  "sent",
  "partiallyPaid",
  "paid",
  "overdue",
  "cancelled",
] as const;
/** Effective status: `partiallyPaid`, `paid` and `overdue` are derived by the server from a sent invoice. */
export type InvoiceStatus = (typeof INVOICE_STATUSES)[number];

export const PAYMENT_METHODS = ["cash", "bankTransfer", "card", "cheque", "other"] as const;
export type InvoicePaymentMethod = (typeof PAYMENT_METHODS)[number];

export interface InvoicePayment {
  id: string;
  amount: number;
  paidOn: string;
  method?: InvoicePaymentMethod;
  reference?: string;
  notes?: string;
  recordedByUserId: string;
  recordedAt: string;
}

export interface InvoiceSummary {
  id: string;
  number: string;
  subject: string;
  status: InvoiceStatus;
  accountId: string;
  accountName?: string;
  contactId?: string;
  contactName?: string;
  dealId?: string;
  dealName?: string;
  orderId?: string;
  orderNumber?: string;
  ownerUserId: string;
  ownerName?: string;
  currency: string;
  grandTotal: number;
  paidAmount: number;
  balanceAmount: number;
  invoiceDate: string;
  dueDate?: string;
  createdAt: string;
}

export interface Invoice extends InvoiceSummary {
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  adjustment: number;
  customerPoNumber?: string;
  exciseTax?: number;
  salesCommission?: number;
  carrier?: string;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  priceBookId?: string;
  priceBookName?: string;
  terms?: string;
  notes?: string;
  sentAt?: string;
  cancelledAt?: string;
  cancelReason?: string;
  updatedAt?: string;
  payments: InvoicePayment[];
  lines: DocumentLine[];
}

export interface InvoiceInput {
  subject: string;
  accountId: string;
  contactId?: string;
  dealId?: string;
  ownerUserId?: string;
  currency: string;
  invoiceDate?: string;
  dueDate?: string;
  customerPoNumber?: string;
  exciseTax?: number;
  salesCommission?: number;
  carrier?: string;
  adjustment?: number;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  priceBookId?: string;
  terms?: string;
  notes?: string;
  lines: DocumentLineInput[];
}

export interface InvoicePaymentInput {
  amount: number;
  paidOn?: string;
  method?: InvoicePaymentMethod;
  reference?: string;
  notes?: string;
}

export interface InvoiceConvertInput {
  invoiceDate?: string;
  dueDate?: string;
}

// ---- Price books -------------------------------------------------------------------------------

export const PRICING_MODELS = ["perProduct", "flat"] as const;
export type PriceBookPricingModel = (typeof PRICING_MODELS)[number];

export interface PriceBook {
  id: string;
  name: string;
  ownerUserId: string;
  ownerName?: string;
  isActive: boolean;
  pricingModel: PriceBookPricingModel;
  /** `flat` only: signed percent applied to the catalog price. */
  adjustmentPercent?: number;
  currency: string;
  validFrom?: string;
  validTo?: string;
  isEffective: boolean;
  description?: string;
  entryCount: number;
  createdAt: string;
  updatedAt?: string;
}

export interface PriceBookInput {
  name: string;
  ownerUserId?: string;
  isActive: boolean;
  pricingModel: PriceBookPricingModel;
  adjustmentPercent?: number;
  currency: string;
  validFrom?: string;
  validTo?: string;
  description?: string;
}

export interface PriceBookEntry {
  productId: string;
  productName: string;
  productCode?: string;
  catalogPrice: number;
  unitPrice: number;
  updatedAt: string;
}

export const PRICE_SOURCES = ["entry", "flat", "catalog"] as const;
export type PriceBookPriceSource = (typeof PRICE_SOURCES)[number];

export interface PriceBookResolvedPrice {
  productId: string;
  unitPrice: number;
  source: PriceBookPriceSource;
}

export interface PriceBookAccountDefault {
  priceBookId: string;
  priceBookName: string;
  isEffective: boolean;
}

// ---- Vendors -----------------------------------------------------------------------------------

export interface Vendor {
  id: string;
  name: string;
  ownerUserId: string;
  ownerName?: string;
  phone?: string;
  email?: string;
  website?: string;
  category?: string;
  glAccount?: string;
  address?: DocumentAddress;
  description?: string;
  emailOptOut: boolean;
  productCount: number;
  purchaseOrderCount: number;
  createdAt: string;
  updatedAt?: string;
}

export interface VendorInput {
  name: string;
  ownerUserId?: string;
  phone?: string;
  email?: string;
  website?: string;
  category?: string;
  glAccount?: string;
  address?: DocumentAddress;
  description?: string;
  emailOptOut?: boolean;
}

// ---- Purchase orders ---------------------------------------------------------------------------

export const PURCHASE_ORDER_STATUSES = ["draft", "confirmed", "received", "cancelled"] as const;
export type PurchaseOrderStatus = (typeof PURCHASE_ORDER_STATUSES)[number];

export interface PurchaseOrderSummary {
  id: string;
  number: string;
  subject: string;
  status: PurchaseOrderStatus;
  vendorId: string;
  vendorName?: string;
  contactId?: string;
  contactName?: string;
  ownerUserId: string;
  ownerName?: string;
  currency: string;
  grandTotal: number;
  poDate: string;
  dueDate?: string;
  createdAt: string;
}

export interface PurchaseOrder extends PurchaseOrderSummary {
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  adjustment: number;
  exciseTax?: number;
  salesCommission?: number;
  carrier?: string;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  terms?: string;
  notes?: string;
  confirmedAt?: string;
  receivedAt?: string;
  cancelledAt?: string;
  cancelReason?: string;
  updatedAt?: string;
  lines: DocumentLine[];
}

export interface PurchaseOrderInput {
  subject: string;
  vendorId: string;
  contactId?: string;
  ownerUserId?: string;
  currency: string;
  poDate?: string;
  dueDate?: string;
  carrier?: string;
  adjustment?: number;
  exciseTax?: number;
  salesCommission?: number;
  billingAddress?: DocumentAddress;
  shippingAddress?: DocumentAddress;
  terms?: string;
  notes?: string;
  lines: DocumentLineInput[];
}

// ---- Report sections (`GET /reports/commerce/summary`) -------------------------------------------

interface InventoryStatusRow<S extends string> {
  status: S;
  count: number;
  amount: number;
}

export interface InvoiceReportSection {
  totalCount: number;
  totalAmount: number;
  paidAmount: number;
  outstandingAmount: number;
  overdueCount: number;
  overdueAmount: number;
  byStatus: InventoryStatusRow<InvoiceStatus>[];
}

export interface PurchaseOrderReportSection {
  totalCount: number;
  totalAmount: number;
  byStatus: InventoryStatusRow<PurchaseOrderStatus>[];
}
