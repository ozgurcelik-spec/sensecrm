/** Activities (görev, arama, toplantı, not) - `/activities`. */
import { apiClient } from "@/lib/api-client";
import type { Activity, ActivityInput, ActivitySummary, ListResult } from "@/types";
import { cleanParams, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface ActivityListQuery extends ListQuery {
  /** An ActivityType value (kept a string: it comes straight from the URL). */
  type?: string;
  status?: string;
  assignedUserId?: string;
  relatedType?: string;
  relatedId?: string;
  /** ISO date-time bounds of `dueAt`. */
  dueFrom?: string;
  dueTo?: string;
  overdue?: boolean;
}

export const activityKeys = {
  all: ["activities"] as const,
  lists: ["activities", "list"] as const,
  list: (query: ActivityListQuery) => ["activities", "list", query] as const,
  detail: (id: string) => ["activities", "detail", id] as const,
  summary: (assignedUserId?: string) => ["activities", "summary", assignedUserId ?? "me"] as const,
};

export const listActivities = (query: ActivityListQuery): Promise<ListResult<Activity>> =>
  getList<Activity>("/activities", query);

export const getActivity = (id: string): Promise<Activity> =>
  getOne<Activity>(`/activities/${seg(id)}`);

export async function createActivity(input: ActivityInput): Promise<Activity> {
  const { data } = await apiClient.post<Activity>("/activities", input);
  return data;
}

export async function updateActivity(id: string, input: ActivityInput): Promise<void> {
  await apiClient.put(`/activities/${seg(id)}`, input);
}

export async function deleteActivity(id: string): Promise<void> {
  await apiClient.delete(`/activities/${seg(id)}`);
}

export async function completeActivity(id: string): Promise<void> {
  await apiClient.post(`/activities/${seg(id)}/complete`);
}

export async function reopenActivity(id: string): Promise<void> {
  await apiClient.post(`/activities/${seg(id)}/reopen`);
}

/** Counts for `assignedUserId` (the caller when omitted). */
export async function getActivitySummary(assignedUserId?: string): Promise<ActivitySummary> {
  const { data } = await apiClient.get<ActivitySummary>("/activities/summary", {
    params: cleanParams({ assignedUserId }),
  });
  return data;
}
