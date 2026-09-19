import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toastApiError } from "@/hooks/use-toast";
import {
  activityKeys,
  completeActivity,
  createActivity,
  deleteActivity,
  getActivity,
  getActivitySummary,
  listActivities,
  reopenActivity,
  updateActivity,
  type ActivityListQuery,
} from "@/services/activities.service";
import type { Activity, ActivityInput, ListResult } from "@/types";

export function useActivities(query: ActivityListQuery, enabled = true) {
  return useQuery({
    queryKey: activityKeys.list(query),
    queryFn: () => listActivities(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useActivity(id: string | undefined) {
  return useQuery({
    queryKey: activityKeys.detail(id ?? ""),
    queryFn: () => getActivity(id as string),
    enabled: !!id,
  });
}

/** Counts of the caller (or of `assignedUserId`); refreshed whenever an activity changes. */
export function useActivitySummary(assignedUserId?: string, enabled = true) {
  return useQuery({
    queryKey: activityKeys.summary(assignedUserId),
    queryFn: () => getActivitySummary(assignedUserId),
    enabled,
  });
}

function useInvalidateActivities() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: activityKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      // The "activities by user" report changes with every activity.
      queryClient.invalidateQueries({ queryKey: ["reports", "activities-by-user"] }),
    ]);
}

export function useSaveActivity() {
  const invalidate = useInvalidateActivities();
  return useMutation({
    mutationFn: async ({ id, ...input }: ActivityInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateActivity(id, input);
        return id;
      }
      return (await createActivity(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteActivity() {
  const invalidate = useInvalidateActivities();
  return useMutation({
    mutationFn: (id: string) => deleteActivity(id),
    onSuccess: invalidate,
  });
}

export type ActivityStatusAction = "complete" | "reopen";

/** The activity as it looks after `complete` / `reopen` succeeds (used for the optimistic update). */
export function applyStatusAction(
  activity: Activity,
  action: ActivityStatusAction,
  now: Date = new Date()
): Activity {
  if (action === "complete") {
    return { ...activity, status: "completed", completedAt: now.toISOString(), isOverdue: false };
  }
  return {
    ...activity,
    status: "open",
    completedAt: undefined,
    isOverdue: !!activity.dueAt && new Date(activity.dueAt).getTime() < now.getTime(),
  };
}

type ListSnapshot = [readonly unknown[], ListResult<Activity> | undefined][];

/**
 * Complete / reopen with an optimistic update: every cached activity list shows the new status at
 * once and is restored (with an error toast) if the request fails. The lists and the summary are refetched afterwards.
 */
export function useSetActivityStatus() {
  const queryClient = useQueryClient();
  const invalidate = useInvalidateActivities();
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: ActivityStatusAction }) =>
      action === "complete" ? completeActivity(id) : reopenActivity(id),
    onMutate: async ({ id, action }) => {
      await queryClient.cancelQueries({ queryKey: activityKeys.lists });
      const snapshot: ListSnapshot = queryClient.getQueriesData<ListResult<Activity>>({
        queryKey: activityKeys.lists,
      });
      const now = new Date();
      queryClient.setQueriesData<ListResult<Activity>>({ queryKey: activityKeys.lists }, (old) =>
        old
          ? {
              ...old,
              items: old.items.map((a) => (a.id === id ? applyStatusAction(a, action, now) : a)),
            }
          : old
      );
      return { snapshot };
    },
    onError: (error, _variables, context) => {
      for (const [key, data] of context?.snapshot ?? []) queryClient.setQueryData(key, data);
      toastApiError(error);
    },
    onSettled: invalidate,
  });
}
