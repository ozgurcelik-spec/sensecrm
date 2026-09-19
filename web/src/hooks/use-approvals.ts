import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  approvalKeys,
  decideApproval,
  getApproval,
  getApprovalSummary,
  listApprovals,
  type ApprovalListQuery,
} from "@/services/approvals.service";
import { activityKeys } from "@/services/activities.service";
import { workflowKeys } from "@/services/workflows.service";
import type { ApprovalDecision } from "@/types";

/** How often the pending count is polled (only while the tab is visible). */
export const APPROVAL_POLL_INTERVAL_MS = 60_000;

export function useApprovals(query: ApprovalListQuery, enabled = true) {
  return useQuery({
    queryKey: approvalKeys.list(query),
    queryFn: () => listApprovals(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useApproval(id: string | undefined) {
  return useQuery({
    queryKey: approvalKeys.detail(id ?? ""),
    queryFn: () => getApproval(id as string),
    enabled: !!id,
  });
}

/**
 * The caller's pending approvals (top bar badge, sidebar entry). Polled every minute while the tab
 * is visible (React Query pauses the interval in a hidden tab) and refetched whenever the tab
 * becomes visible again and after every decision.
 */
export function usePendingApprovalCount(enabled = true) {
  return useQuery({
    queryKey: approvalKeys.summary,
    queryFn: getApprovalSummary,
    enabled,
    select: (summary) => summary.pendingCount,
    refetchInterval: APPROVAL_POLL_INTERVAL_MS,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
    staleTime: 0,
  });
}

export function useDecideApproval() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      decision,
      comment,
    }: {
      id: string;
      decision: ApprovalDecision;
      comment?: string;
    }) => decideApproval(id, decision, comment),
    // Also after a failure: "already decided" means the lists on screen are stale.
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: approvalKeys.all }),
        queryClient.invalidateQueries({ queryKey: workflowKeys.executions }),
        // The decision is recorded as a note on the deal (Activities).
        queryClient.invalidateQueries({ queryKey: activityKeys.all }),
      ]),
  });
}
