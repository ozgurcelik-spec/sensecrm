/** Vendors - `/vendors` (docs/plan/m9c-envanter.md). */
import { apiClient } from "@/lib/api-client";
import type { ListResult, Vendor, VendorInput } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface VendorListQuery extends ListQuery {
  category?: string;
  ownerUserId?: string;
  emailOptOut?: string | boolean;
}

export const vendorKeys = {
  all: ["vendors"] as const,
  list: (query: VendorListQuery) => ["vendors", "list", query] as const,
  detail: (id: string) => ["vendors", "detail", id] as const,
};

export const listVendors = (query: VendorListQuery): Promise<ListResult<Vendor>> =>
  getList<Vendor>("/vendors", query);

export const getVendor = (id: string): Promise<Vendor> => getOne<Vendor>(`/vendors/${seg(id)}`);

export async function createVendor(input: VendorInput): Promise<Vendor> {
  const { data } = await apiClient.post<Vendor>("/vendors", input);
  return data;
}

export async function updateVendor(id: string, input: VendorInput): Promise<void> {
  await apiClient.put(`/vendors/${seg(id)}`, input);
}

export async function deleteVendor(id: string): Promise<void> {
  await apiClient.delete(`/vendors/${seg(id)}`);
}
