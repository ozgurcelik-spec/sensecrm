/** Approvals (Milestone 4) - `/approvals`. */
import { apiClient } from "@/lib/api-client";
import type { Approval, ApprovalDecision, ApprovalSummary, ListResult } from "@/types";
import { cleanParams, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface ApprovalListQuery extends ListQuery {
  /** An ApprovalStatus value (kept a string: it comes straight from the URL). */
  status?: string;
  /** `true`: the caller's approvals (no extra permission); `false`: everyone's (`org.workflows.manage`). */
  mine?: boolean;
}

export const approvalKeys = {
  all: ["approvals"] as const,
  lists: ["approvals", "list"] as const,
  list: (query: ApprovalListQuery) => ["approvals", "list", query] as const,
  detail: (id: string) => ["approvals", "detail", id] as const,
  summary: ["approvals", "summary"] as const,
};

export const listApprovals = (query: ApprovalListQuery): Promise<ListResult<Approval>> =>
  getList<Approval>("/approvals", query);

export const getApproval = (id: string): Promise<Approval> =>
  getOne<Approval>(`/approvals/${seg(id)}`);

export async function decideApproval(
  id: string,
  decision: ApprovalDecision,
  comment?: string
): Promise<void> {
  await apiClient.post(`/approvals/${seg(id)}/decision`, {
    decision,
    ...cleanParams({ comment }),
  });
}

/** Pending approvals of the caller (top bar badge). */
export const getApprovalSummary = (): Promise<ApprovalSummary> =>
  getOne<ApprovalSummary>("/approvals/summary");
