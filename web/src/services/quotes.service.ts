/** Quotes - `/quotes` (docs/plan/m6a-ticaret.md). */
import { apiClient } from "@/lib/api-client";
import type { ListResult, Quote, QuoteInput, QuoteSummary, SalesOrder } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface QuoteListQuery extends ListQuery {
  /** Effective status (`expired` included). */
  status?: string;
  accountId?: string;
  contactId?: string;
  dealId?: string;
  ownerUserId?: string;
  validFrom?: string;
  validTo?: string;
  /** "true" / "false". */
  converted?: string | boolean;
}

export const quoteKeys = {
  all: ["quotes"] as const,
  list: (query: QuoteListQuery) => ["quotes", "list", query] as const,
  detail: (id: string) => ["quotes", "detail", id] as const,
};

/** State changes; only `reject` (reason) and `extend` (validUntil) carry a body. */
export type QuoteAction = "send" | "negotiate" | "accept" | "revert" | "reject" | "extend";

export const listQuotes = (query: QuoteListQuery): Promise<ListResult<QuoteSummary>> =>
  getList<QuoteSummary>("/quotes", query);

export const getQuote = (id: string): Promise<Quote> => getOne<Quote>(`/quotes/${seg(id)}`);

export async function createQuote(input: QuoteInput): Promise<Quote> {
  const { data } = await apiClient.post<Quote>("/quotes", input);
  return data;
}

export async function updateQuote(id: string, input: QuoteInput): Promise<void> {
  await apiClient.put(`/quotes/${seg(id)}`, input);
}

export async function deleteQuote(id: string): Promise<void> {
  await apiClient.delete(`/quotes/${seg(id)}`);
}

export async function runQuoteAction(
  id: string,
  action: QuoteAction,
  body?: { reason?: string; validUntil?: string }
): Promise<void> {
  await apiClient.post(`/quotes/${seg(id)}/${action}`, body ?? {});
}

/** Creates a draft sales order from an accepted quote (one transaction on the server). */
export async function convertQuote(id: string): Promise<SalesOrder> {
  const { data } = await apiClient.post<SalesOrder>(`/quotes/${seg(id)}/convert`, {});
  return data;
}
