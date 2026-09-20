/** Purchase orders - `/purchase-orders` (docs/plan/m9c-envanter.md). */
import { apiClient } from "@/lib/api-client";
import type { ListResult, PurchaseOrder, PurchaseOrderInput, PurchaseOrderSummary } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface PurchaseOrderListQuery extends ListQuery {
  status?: string;
  vendorId?: string;
  contactId?: string;
  ownerUserId?: string;
  poFrom?: string;
  poTo?: string;
}

export const purchaseOrderKeys = {
  all: ["purchase-orders"] as const,
  list: (query: PurchaseOrderListQuery) => ["purchase-orders", "list", query] as const,
  detail: (id: string) => ["purchase-orders", "detail", id] as const,
};

/** State changes; only `cancel` carries a body (`reason`). */
export type PurchaseOrderAction = "confirm" | "receive" | "cancel";

export const listPurchaseOrders = (
  query: PurchaseOrderListQuery
): Promise<ListResult<PurchaseOrderSummary>> =>
  getList<PurchaseOrderSummary>("/purchase-orders", query);

export const getPurchaseOrder = (id: string): Promise<PurchaseOrder> =>
  getOne<PurchaseOrder>(`/purchase-orders/${seg(id)}`);

export async function createPurchaseOrder(input: PurchaseOrderInput): Promise<PurchaseOrder> {
  const { data } = await apiClient.post<PurchaseOrder>("/purchase-orders", input);
  return data;
}

export async function updatePurchaseOrder(id: string, input: PurchaseOrderInput): Promise<void> {
  await apiClient.put(`/purchase-orders/${seg(id)}`, input);
}

export async function deletePurchaseOrder(id: string): Promise<void> {
  await apiClient.delete(`/purchase-orders/${seg(id)}`);
}

export async function runPurchaseOrderAction(
  id: string,
  action: PurchaseOrderAction,
  body?: { reason?: string }
): Promise<void> {
  await apiClient.post(`/purchase-orders/${seg(id)}/${action}`, body ?? {});
}
