/** Sales orders - `/orders` (docs/plan/m6a-ticaret.md). */
import { apiClient } from "@/lib/api-client";
import type { ListResult, OrderInput, OrderSummary, SalesOrder } from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface OrderListQuery extends ListQuery {
  status?: string;
  accountId?: string;
  contactId?: string;
  dealId?: string;
  quoteId?: string;
  ownerUserId?: string;
  orderFrom?: string;
  orderTo?: string;
}

export const orderKeys = {
  all: ["orders"] as const,
  list: (query: OrderListQuery) => ["orders", "list", query] as const,
  detail: (id: string) => ["orders", "detail", id] as const,
};

export type OrderAction = "confirm" | "fulfill" | "cancel";

export const listOrders = (query: OrderListQuery): Promise<ListResult<OrderSummary>> =>
  getList<OrderSummary>("/orders", query);

export const getOrder = (id: string): Promise<SalesOrder> =>
  getOne<SalesOrder>(`/orders/${seg(id)}`);

export async function createOrder(input: OrderInput): Promise<SalesOrder> {
  const { data } = await apiClient.post<SalesOrder>("/orders", input);
  return data;
}

export async function updateOrder(id: string, input: OrderInput): Promise<void> {
  await apiClient.put(`/orders/${seg(id)}`, input);
}

export async function deleteOrder(id: string): Promise<void> {
  await apiClient.delete(`/orders/${seg(id)}`);
}

export async function runOrderAction(
  id: string,
  action: OrderAction,
  body?: { reason?: string }
): Promise<void> {
  await apiClient.post(`/orders/${seg(id)}/${action}`, body ?? {});
}
