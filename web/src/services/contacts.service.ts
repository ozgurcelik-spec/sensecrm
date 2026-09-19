/** Contacts (kişiler) - `/contacts`. */
import { apiClient } from "@/lib/api-client";
import type { Contact, ContactInput, ListResult } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface ContactListQuery extends ListQuery {
  accountId?: string;
  ownerUserId?: string;
}

export const contactKeys = {
  all: ["contacts"] as const,
  list: (query: ContactListQuery) => ["contacts", "list", query] as const,
  detail: (id: string) => ["contacts", "detail", id] as const,
};

export const listContacts = (query: ContactListQuery): Promise<ListResult<Contact>> =>
  getList<Contact>("/contacts", query);

export const getContact = (id: string): Promise<Contact> => getOne<Contact>(`/contacts/${seg(id)}`);

export async function createContact(input: ContactInput): Promise<Contact> {
  const { data } = await apiClient.post<Contact>("/contacts", input);
  return data;
}

export async function updateContact(id: string, input: ContactInput): Promise<void> {
  await apiClient.put(`/contacts/${seg(id)}`, input);
}

export async function deleteContact(id: string): Promise<void> {
  await apiClient.delete(`/contacts/${seg(id)}`);
}
