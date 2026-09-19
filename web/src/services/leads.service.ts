/** Leads (potansiyel müşteriler) - `/leads`. */
import { apiClient } from "@/lib/api-client";
import type { ConvertLeadInput, ConvertLeadResult, Lead, LeadInput, ListResult } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface LeadListQuery extends ListQuery {
  /** A LeadStatus value (kept a string: it comes straight from the URL). */
  status?: string;
  /** A LeadSource value. */
  source?: string;
  ownerUserId?: string;
}

export const leadKeys = {
  all: ["leads"] as const,
  list: (query: LeadListQuery) => ["leads", "list", query] as const,
  detail: (id: string) => ["leads", "detail", id] as const,
};

export const listLeads = (query: LeadListQuery): Promise<ListResult<Lead>> =>
  getList<Lead>("/leads", query);

export const getLead = (id: string): Promise<Lead> => getOne<Lead>(`/leads/${seg(id)}`);

export async function createLead(input: LeadInput): Promise<Lead> {
  const { data } = await apiClient.post<Lead>("/leads", input);
  return data;
}

export async function updateLead(id: string, input: LeadInput): Promise<void> {
  await apiClient.put(`/leads/${seg(id)}`, input);
}

export async function deleteLead(id: string): Promise<void> {
  await apiClient.delete(`/leads/${seg(id)}`);
}

export async function convertLead(id: string, input: ConvertLeadInput): Promise<ConvertLeadResult> {
  const { data } = await apiClient.post<ConvertLeadResult>(`/leads/${seg(id)}/convert`, input);
  return data;
}
