/**
 * Milestone 4 API types (workflow rules, executions and approvals) - see docs/plan/m4-workflow.md.
 * JSON is camelCase, enums are lowercase strings and null fields are absent from responses.
 */
import type { LeadSource } from "./crm";

export const WORKFLOW_KINDS = ["leadAssignment", "dealApproval"] as const;
export type WorkflowKind = (typeof WORKFLOW_KINDS)[number];

export interface LeadAssignmentParams {
  /** Empty or absent = every source. */
  sources?: LeadSource[];
  assigneeRoleId: string;
  /** 1-720, default 24. */
  followUpHours: number;
}

export interface DealApprovalParams {
  /** Greater than 0. */
  minAmount: number;
  approverRoleId: string;
}

export type WorkflowParams = LeadAssignmentParams | DealApprovalParams;

export interface WorkflowRule {
  id: string;
  name: string;
  kind: WorkflowKind;
  isEnabled: boolean;
  params: LeadAssignmentParams | DealApprovalParams;
  createdAt: string;
  updatedAt?: string;
}

/** Body of `POST /workflows/rules` and `PUT /workflows/rules/{id}`. */
export interface WorkflowRuleInput {
  name: string;
  kind: WorkflowKind;
  params: WorkflowParams;
  isEnabled?: boolean;
}

export const EXECUTION_STATUSES = ["running", "completed", "failed", "terminated"] as const;
export type ExecutionStatus = (typeof EXECUTION_STATUSES)[number];

export type WorkflowSubjectType = "lead" | "deal";

export interface WorkflowExecution {
  id: string;
  ruleId: string;
  ruleName: string;
  kind: WorkflowKind;
  status: ExecutionStatus;
  startedAt: string;
  endedAt?: string;
  subjectType: WorkflowSubjectType;
  subjectId: string;
  subjectName?: string;
  /** Machine-readable reason (for example `no_assignee`) or the engine's message. */
  error?: string;
}

export interface ExecutionStep {
  name: string;
  /** Engine task status (COMPLETED, IN_PROGRESS, FAILED, ...), shown as is when unknown. */
  status: string;
  startedAt?: string;
  endedAt?: string;
  output?: unknown;
}

export interface ExecutionApproval {
  id: string;
  approverName: string;
  status: ApprovalStatus;
  decidedAt?: string;
}

export interface ExecutionDetail extends WorkflowExecution {
  steps: ExecutionStep[];
  approvals?: ExecutionApproval[];
}

export const APPROVAL_STATUSES = ["pending", "approved", "rejected", "cancelled"] as const;
export type ApprovalStatus = (typeof APPROVAL_STATUSES)[number];

export const APPROVAL_DECISIONS = ["approve", "reject"] as const;
export type ApprovalDecision = (typeof APPROVAL_DECISIONS)[number];

export interface Approval {
  id: string;
  executionId: string;
  title: string;
  subjectType: string;
  subjectId: string;
  subjectName?: string;
  amount?: number;
  currency?: string;
  requestedAt: string;
  status: ApprovalStatus;
  approverUserId: string;
  approverName: string;
  decidedAt?: string;
  comment?: string;
}

export interface ApprovalSummary {
  pendingCount: number;
}
