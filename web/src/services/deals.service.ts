/** Deals (fırsatlar) - `/deals`, including the kanban board and stage moves. */
import { apiClient } from "@/lib/api-client";
import type { Deal, DealBoard, DealInput, ListResult } from "@/types";
import { cleanParams, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface DealListQuery extends ListQuery {
  pipelineId?: string;
  stageId?: string;
  /** A StageKind value (a string: it comes straight from the URL). */
  stageKind?: string;
  ownerUserId?: string;
  accountId?: string;
}

export type DealBoardQuery = {
  pipelineId?: string;
  ownerUserId?: string;
};

export const dealKeys = {
  all: ["deals"] as const,
  list: (query: DealListQuery) => ["deals", "list", query] as const,
  detail: (id: string) => ["deals", "detail", id] as const,
  board: (query: DealBoardQuery) => ["deals", "board", query] as const,
};

export const listDeals = (query: DealListQuery): Promise<ListResult<Deal>> =>
  getList<Deal>("/deals", query);

export const getDeal = (id: string): Promise<Deal> => getOne<Deal>(`/deals/${seg(id)}`);

export async function getDealBoard(query: DealBoardQuery): Promise<DealBoard> {
  const { data } = await apiClient.get<DealBoard>("/deals/board", { params: cleanParams(query) });
  return data;
}

export async function createDeal(input: DealInput): Promise<Deal> {
  const { data } = await apiClient.post<Deal>("/deals", input);
  return data;
}

export async function updateDeal(id: string, input: DealInput): Promise<void> {
  await apiClient.put(`/deals/${seg(id)}`, input);
}

export async function deleteDeal(id: string): Promise<void> {
  await apiClient.delete(`/deals/${seg(id)}`);
}

export async function moveDealStage(
  id: string,
  request: { stageId: string; lostReason?: string }
): Promise<void> {
  await apiClient.post(`/deals/${seg(id)}/stage`, request);
}
