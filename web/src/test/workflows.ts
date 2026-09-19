/** Fixtures for the Milestone 4 (workflows and approvals) tests. */
import type { Approval, ExecutionDetail, WorkflowExecution, WorkflowRule } from "@/types";

export const ROLES = [
  { id: "role-sales", name: "Satış Temsilcisi", isSystem: false, permissions: [], memberCount: 3 },
  { id: "role-manager", name: "Satış Müdürü", isSystem: false, permissions: [], memberCount: 1 },
];

export function leadRule(overrides: Partial<WorkflowRule> = {}): WorkflowRule {
  return {
    id: "rule-1",
    name: "Web potansiyelleri",
    kind: "leadAssignment",
    isEnabled: true,
    params: { sources: ["web", "referral"], assigneeRoleId: "role-sales", followUpHours: 24 },
    createdAt: "2026-05-01T09:00:00Z",
    ...overrides,
  };
}

export function dealRule(overrides: Partial<WorkflowRule> = {}): WorkflowRule {
  return {
    id: "rule-2",
    name: "Büyük fırsat onayı",
    kind: "dealApproval",
    isEnabled: false,
    params: { minAmount: 100000, approverRoleId: "role-manager" },
    createdAt: "2026-05-02T09:00:00Z",
    ...overrides,
  };
}

export function execution(
  id: string,
  overrides: Partial<WorkflowExecution> = {}
): WorkflowExecution {
  return {
    id,
    ruleId: "rule-1",
    ruleName: "Web potansiyelleri",
    kind: "leadAssignment",
    status: "completed",
    startedAt: "2026-05-10T09:00:00Z",
    endedAt: "2026-05-10T09:00:05Z",
    subjectType: "lead",
    subjectId: `lead-${id}`,
    subjectName: `Potansiyel ${id}`,
    ...overrides,
  };
}

export function executionDetail(
  id: string,
  overrides: Partial<ExecutionDetail> = {}
): ExecutionDetail {
  return {
    ...execution(id),
    steps: [
      {
        name: "assign_lead",
        status: "COMPLETED",
        startedAt: "2026-05-10T09:00:01Z",
        endedAt: "2026-05-10T09:00:02Z",
        output: { ownerUserId: "user-2" },
      },
      { name: "create_follow_up", status: "IN_PROGRESS", startedAt: "2026-05-10T09:00:03Z" },
    ],
    approvals: [],
    ...overrides,
  };
}

export function approval(id: string, overrides: Partial<Approval> = {}): Approval {
  return {
    id,
    executionId: "ex-1",
    title: `Fırsat onayı: Anlaşma ${id}`,
    subjectType: "deal",
    subjectId: `deal-${id}`,
    subjectName: `Anlaşma ${id}`,
    amount: 250000,
    currency: "TRY",
    requestedAt: "2026-05-11T10:00:00Z",
    status: "pending",
    approverUserId: "user-1",
    approverName: "Ada Lovelace",
    ...overrides,
  };
}
