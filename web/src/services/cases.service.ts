/** Service cases (talepler), comments, timeline and SLA policies - `/cases`, `/service/sla-policies`. */
import { apiClient } from "@/lib/api-client";
import type {
  CaseCommentInput,
  CaseComment,
  CaseCreateInput,
  CaseDetail,
  CaseListItem,
  CasePriority,
  CasesSummary,
  CaseStatusInput,
  CaseUpdateInput,
  ListResult,
  SlaPolicy,
  TimelineItem,
} from "@/types";
import { getArray, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface CaseListQuery extends ListQuery {
  /** Comma separated CaseStatus values (`new,open,pending`); kept a string: it comes from the URL. */
  status?: string;
  /** Comma separated CasePriority values. */
  priority?: string;
  channel?: string;
  assignedUserId?: string;
  unassigned?: boolean;
  accountId?: string;
  contactId?: string;
  /** A single SlaState value. */
  slaState?: string;
}

export const caseKeys = {
  all: ["cases"] as const,
  lists: ["cases", "list"] as const,
  list: (query: CaseListQuery) => ["cases", "list", query] as const,
  detail: (id: string) => ["cases", "detail", id] as const,
  timeline: (id: string) => ["cases", "timeline", id] as const,
  summary: ["cases", "summary"] as const,
  slaPolicies: ["service", "sla-policies"] as const,
};

export const listCases = (query: CaseListQuery): Promise<ListResult<CaseListItem>> =>
  getList<CaseListItem>("/cases", query);

export const getCase = (id: string): Promise<CaseDetail> => getOne<CaseDetail>(`/cases/${seg(id)}`);

export async function createCase(input: CaseCreateInput): Promise<CaseDetail> {
  const { data } = await apiClient.post<CaseDetail>("/cases", input);
  return data;
}

export async function updateCase(id: string, input: CaseUpdateInput): Promise<void> {
  await apiClient.put(`/cases/${seg(id)}`, input);
}

export async function deleteCase(id: string): Promise<void> {
  await apiClient.delete(`/cases/${seg(id)}`);
}

export async function changeCaseStatus(id: string, input: CaseStatusInput): Promise<void> {
  await apiClient.post(`/cases/${seg(id)}/status`, input);
}

export async function changeCasePriority(id: string, priority: CasePriority): Promise<void> {
  await apiClient.post(`/cases/${seg(id)}/priority`, { priority });
}

/** `null` removes the assignment. */
export async function assignCase(id: string, assignedUserId: string | null): Promise<void> {
  await apiClient.post(`/cases/${seg(id)}/assign`, { assignedUserId });
}

export async function addCaseComment(id: string, input: CaseCommentInput): Promise<CaseComment> {
  const { data } = await apiClient.post<CaseComment>(`/cases/${seg(id)}/comments`, input);
  return data;
}

export const getCaseTimeline = (
  id: string,
  page: number,
  pageSize: number
): Promise<ListResult<TimelineItem>> =>
  getList<TimelineItem>(`/cases/${seg(id)}/timeline`, { page, pageSize });

export const getCasesSummary = (): Promise<CasesSummary> =>
  getOne<CasesSummary>("/cases/summary");

export const getSlaPolicies = (): Promise<SlaPolicy[]> =>
  getArray<SlaPolicy>("/service/sla-policies");

export async function updateSlaPolicies(policies: SlaPolicy[]): Promise<void> {
  await apiClient.put("/service/sla-policies", { policies });
}
