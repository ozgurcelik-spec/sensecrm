/**
 * Small helpers shared by the Milestone 2 services.
 */
import { apiClient } from "@/lib/api-client";
import type { ListResult } from "@/types";

/** Query values that are undefined/null/empty are dropped so they never reach the URL. */
export type QueryParams = Record<string, string | number | boolean | undefined | null>;

export function cleanParams(params: QueryParams): Record<string, string | number | boolean> {
  const out: Record<string, string | number | boolean> = {};
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== "") out[key] = value;
  }
  return out;
}

export function seg(id: string): string {
  return encodeURIComponent(id);
}

/** Server-side paging / sort / search / filters of a list endpoint (`page`, `pageSize`, `q`, `sort`, ...). */
export interface ListQuery extends QueryParams {
  page?: number;
  pageSize?: number;
  q?: string;
  sort?: string;
}

export async function getList<T>(path: string, params: ListQuery): Promise<ListResult<T>> {
  const { data } = await apiClient.get<ListResult<T>>(path, { params: cleanParams(params) });
  return data;
}

export async function getOne<T>(path: string): Promise<T> {
  const { data } = await apiClient.get<T>(path);
  return data;
}

export async function getArray<T>(path: string): Promise<T[]> {
  const { data } = await apiClient.get<T[]>(path);
  return data;
}
