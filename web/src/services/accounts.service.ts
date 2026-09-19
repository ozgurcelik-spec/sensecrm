/** Accounts (firmalar) - `/accounts`. */
import { apiClient } from "@/lib/api-client";
import type { Account, AccountInput, Contact, Deal, ListResult } from "@/types";
import { getArray, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface AccountListQuery extends ListQuery {
  ownerUserId?: string;
  industry?: string;
}

export const accountKeys = {
  all: ["accounts"] as const,
  list: (query: AccountListQuery) => ["accounts", "list", query] as const,
  detail: (id: string) => ["accounts", "detail", id] as const,
  contacts: (id: string) => ["accounts", "detail", id, "contacts"] as const,
  deals: (id: string) => ["accounts", "detail", id, "deals"] as const,
};

export const listAccounts = (query: AccountListQuery): Promise<ListResult<Account>> =>
  getList<Account>("/accounts", query);

export const getAccount = (id: string): Promise<Account> => getOne<Account>(`/accounts/${seg(id)}`);

export async function createAccount(input: AccountInput): Promise<Account> {
  const { data } = await apiClient.post<Account>("/accounts", input);
  return data;
}

export async function updateAccount(id: string, input: AccountInput): Promise<void> {
  await apiClient.put(`/accounts/${seg(id)}`, input);
}

export async function deleteAccount(id: string): Promise<void> {
  await apiClient.delete(`/accounts/${seg(id)}`);
}

export const listAccountContacts = (id: string): Promise<Contact[]> =>
  getArray<Contact>(`/accounts/${seg(id)}/contacts`);

export const listAccountDeals = (id: string): Promise<Deal[]> =>
  getArray<Deal>(`/accounts/${seg(id)}/deals`);
