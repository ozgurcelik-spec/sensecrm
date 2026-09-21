/** Invoices - `/invoices` and the order conversion (docs/plan/m9c-envanter.md). */
import { apiClient } from "@/lib/api-client";
import type {
  Invoice,
  InvoiceConvertInput,
  InvoiceInput,
  InvoicePayment,
  InvoicePaymentInput,
  InvoiceSummary,
  ListResult,
} from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface InvoiceListQuery extends ListQuery {
  /** Effective status (`partiallyPaid`, `paid`, `overdue` are derived). */
  status?: string;
  accountId?: string;
  contactId?: string;
  dealId?: string;
  orderId?: string;
  ownerUserId?: string;
  invoiceFrom?: string;
  invoiceTo?: string;
  dueFrom?: string;
  dueTo?: string;
}

export const invoiceKeys = {
  all: ["invoices"] as const,
  list: (query: InvoiceListQuery) => ["invoices", "list", query] as const,
  detail: (id: string) => ["invoices", "detail", id] as const,
};

/** State changes; only `cancel` carries a body (`reason`). */
export type InvoiceAction = "send" | "revert" | "cancel";

export const listInvoices = (query: InvoiceListQuery): Promise<ListResult<InvoiceSummary>> =>
  getList<InvoiceSummary>("/invoices", query);

export const getInvoice = (id: string): Promise<Invoice> => getOne<Invoice>(`/invoices/${seg(id)}`);

export async function createInvoice(input: InvoiceInput): Promise<Invoice> {
  const { data } = await apiClient.post<Invoice>("/invoices", input);
  return data;
}

export async function updateInvoice(id: string, input: InvoiceInput): Promise<void> {
  await apiClient.put(`/invoices/${seg(id)}`, input);
}

export async function deleteInvoice(id: string): Promise<void> {
  await apiClient.delete(`/invoices/${seg(id)}`);
}

export async function runInvoiceAction(
  id: string,
  action: InvoiceAction,
  body?: { reason?: string }
): Promise<void> {
  await apiClient.post(`/invoices/${seg(id)}/${action}`, body ?? {});
}

export async function recordInvoicePayment(
  id: string,
  input: InvoicePaymentInput
): Promise<InvoicePayment> {
  const { data } = await apiClient.post<InvoicePayment>(`/invoices/${seg(id)}/payments`, input);
  return data;
}

export async function deleteInvoicePayment(id: string, paymentId: string): Promise<void> {
  await apiClient.delete(`/invoices/${seg(id)}/payments/${seg(paymentId)}`);
}

/** Creates a draft invoice from a confirmed / fulfilled order (one transaction on the server). */
export async function convertOrderToInvoice(
  orderId: string,
  input: InvoiceConvertInput = {}
): Promise<Invoice> {
  const { data } = await apiClient.post<Invoice>(`/orders/${seg(orderId)}/invoice`, input);
  return data;
}
