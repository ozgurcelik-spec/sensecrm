/** Workflow rules and executions (Milestone 4) - `/workflows/rules`, `/workflows/executions`. */
import { apiClient } from "@/lib/api-client";
import type {
  ExecutionDetail,
  ListResult,
  WorkflowExecution,
  WorkflowRule,
  WorkflowRuleInput,
} from "@/types";
import { getList, getOne, seg, type ListQuery } from "./crm-http";

export interface ExecutionListQuery extends ListQuery {
  /** An ExecutionStatus value (kept a string: it comes straight from the URL). */
  status?: string;
  ruleId?: string;
  /** ISO date-time bounds of `startedAt`. */
  from?: string;
  to?: string;
}

export const workflowKeys = {
  all: ["workflows"] as const,
  rules: ["workflows", "rules"] as const,
  executions: ["workflows", "executions"] as const,
  executionList: (query: ExecutionListQuery) => ["workflows", "executions", "list", query] as const,
  execution: (id: string) => ["workflows", "executions", "detail", id] as const,
};

/** The rules endpoint returns the rules of the organization; a paged envelope is accepted as well. */
export async function listRules(): Promise<WorkflowRule[]> {
  const { data } = await apiClient.get<WorkflowRule[] | ListResult<WorkflowRule>>(
    "/workflows/rules"
  );
  return Array.isArray(data) ? data : (data.items ?? []);
}

export async function createRule(input: WorkflowRuleInput): Promise<WorkflowRule> {
  const { data } = await apiClient.post<WorkflowRule>("/workflows/rules", input);
  return data;
}

export async function updateRule(id: string, input: WorkflowRuleInput): Promise<void> {
  await apiClient.put(`/workflows/rules/${seg(id)}`, input);
}

export async function deleteRule(id: string): Promise<void> {
  await apiClient.delete(`/workflows/rules/${seg(id)}`);
}

export async function setRuleEnabled(id: string, enabled: boolean): Promise<void> {
  await apiClient.post(`/workflows/rules/${seg(id)}/${enabled ? "enable" : "disable"}`);
}

export const listExecutions = (query: ExecutionListQuery): Promise<ListResult<WorkflowExecution>> =>
  getList<WorkflowExecution>("/workflows/executions", query);

export const getExecution = (id: string): Promise<ExecutionDetail> =>
  getOne<ExecutionDetail>(`/workflows/executions/${seg(id)}`);

export async function terminateExecution(id: string): Promise<void> {
  await apiClient.post(`/workflows/executions/${seg(id)}/terminate`);
}

export async function retryExecution(id: string): Promise<void> {
  await apiClient.post(`/workflows/executions/${seg(id)}/retry`);
}
