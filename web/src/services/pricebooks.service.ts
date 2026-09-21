/** Price books - `/pricebooks` (docs/plan/m9c-envanter.md). */
import { apiClient } from "@/lib/api-client";
import type {
  ListResult,
  PriceBook,
  PriceBookAccountDefault,
  PriceBookEntry,
  PriceBookInput,
  PriceBookResolvedPrice,
} from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface PriceBookListQuery extends ListQuery {
  isActive?: string | boolean;
  currency?: string;
  effective?: string | boolean;
  ownerUserId?: string;
}

export type PriceBookEntryQuery = ListQuery;

export const priceBookKeys = {
  all: ["pricebooks"] as const,
  list: (query: PriceBookListQuery) => ["pricebooks", "list", query] as const,
  detail: (id: string) => ["pricebooks", "detail", id] as const,
  entries: (id: string, query: PriceBookEntryQuery) => ["pricebooks", "entries", id, query] as const,
  accountDefault: (accountId: string) => ["pricebooks", "account-default", accountId] as const,
};

export const listPriceBooks = (query: PriceBookListQuery): Promise<ListResult<PriceBook>> =>
  getList<PriceBook>("/pricebooks", query);

export const getPriceBook = (id: string): Promise<PriceBook> =>
  getOne<PriceBook>(`/pricebooks/${seg(id)}`);

export async function createPriceBook(input: PriceBookInput): Promise<PriceBook> {
  const { data } = await apiClient.post<PriceBook>("/pricebooks", input);
  return data;
}

export async function updatePriceBook(id: string, input: PriceBookInput): Promise<void> {
  await apiClient.put(`/pricebooks/${seg(id)}`, input);
}

export async function deletePriceBook(id: string): Promise<void> {
  await apiClient.delete(`/pricebooks/${seg(id)}`);
}

export const listPriceBookEntries = (
  id: string,
  query: PriceBookEntryQuery
): Promise<ListResult<PriceBookEntry>> =>
  getList<PriceBookEntry>(`/pricebooks/${seg(id)}/entries`, query);

export async function upsertPriceBookEntry(
  id: string,
  productId: string,
  unitPrice: number
): Promise<void> {
  await apiClient.put(`/pricebooks/${seg(id)}/entries/${seg(productId)}`, { unitPrice });
}

export async function deletePriceBookEntry(id: string, productId: string): Promise<void> {
  await apiClient.delete(`/pricebooks/${seg(id)}/entries/${seg(productId)}`);
}

/** Server-side price resolution of 1-100 unique products; products that are not found are absent from the answer. */
export async function resolvePrices(
  id: string,
  productIds: readonly string[]
): Promise<PriceBookResolvedPrice[]> {
  const { data } = await apiClient.post<{ items: PriceBookResolvedPrice[] }>(
    `/pricebooks/${seg(id)}/resolve`,
    { productIds }
  );
  return data.items;
}

/** 200 with the default, or 204 (no default): `null`. */
export async function getAccountDefaultPriceBook(
  accountId: string
): Promise<PriceBookAccountDefault | null> {
  const response = await apiClient.get<PriceBookAccountDefault | "" | undefined>(
    `/pricebooks/accounts/${seg(accountId)}/default`
  );
  const data = response.data;
  return data && typeof data === "object" ? data : null;
}

export async function setAccountDefaultPriceBook(
  accountId: string,
  priceBookId: string
): Promise<void> {
  await apiClient.put(`/pricebooks/accounts/${seg(accountId)}/default`, { priceBookId });
}

export async function clearAccountDefaultPriceBook(accountId: string): Promise<void> {
  await apiClient.delete(`/pricebooks/accounts/${seg(accountId)}/default`);
}
