/** Pure helpers for the workflow screens: rule form values <-> API bodies, server error paths, date filters. */
import type { FieldValues, Path, UseFormSetError } from "react-hook-form";
import { getApiProblem } from "@/lib/api-error";
import { formatYmd, zonedInstant, type Ymd } from "@/lib/zoned-time";
import {
  PERMISSIONS,
  type ApprovalStatus,
  type DealApprovalParams,
  type ExecutionStatus,
  type LeadAssignmentParams,
  type LeadSource,
  type WorkflowKind,
  type WorkflowRule,
  type WorkflowRuleInput,
} from "@/types";

export const DEFAULT_FOLLOW_UP_HOURS = 24;
export const MIN_FOLLOW_UP_HOURS = 1;
export const MAX_FOLLOW_UP_HOURS = 720;

export const EXECUTION_STATUS_COLOR: Record<ExecutionStatus, string> = {
  running: "blue",
  completed: "green",
  failed: "red",
  terminated: "gray",
};

export const APPROVAL_STATUS_COLOR: Record<ApprovalStatus, string> = {
  pending: "yellow",
  approved: "green",
  rejected: "red",
  cancelled: "gray",
};

export const KIND_COLOR: Record<WorkflowKind, string> = {
  leadAssignment: "blue",
  dealApproval: "violet",
};

/** Flat form state: the fields of every kind live side by side, only the active kind's are submitted. */
export interface RuleFormValues {
  name: string;
  kind: WorkflowKind;
  sources: string[];
  assigneeRoleId: string;
  followUpHours: number | string;
  minAmount: number | string;
  approverRoleId: string;
}

export const RULE_FORM_FIELDS = [
  "name",
  "kind",
  "sources",
  "assigneeRoleId",
  "followUpHours",
  "minAmount",
  "approverRoleId",
] as const satisfies readonly (keyof RuleFormValues)[];

export function ruleToFormValues(rule?: WorkflowRule): RuleFormValues {
  const base: RuleFormValues = {
    name: rule?.name ?? "",
    kind: rule?.kind ?? "leadAssignment",
    sources: [],
    assigneeRoleId: "",
    followUpHours: DEFAULT_FOLLOW_UP_HOURS,
    minAmount: "",
    approverRoleId: "",
  };
  if (!rule) return base;
  if (rule.kind === "leadAssignment") {
    const params = rule.params as LeadAssignmentParams;
    return {
      ...base,
      sources: params.sources ?? [],
      assigneeRoleId: params.assigneeRoleId,
      followUpHours: params.followUpHours ?? DEFAULT_FOLLOW_UP_HOURS,
    };
  }
  const params = rule.params as DealApprovalParams;
  return { ...base, minAmount: params.minAmount, approverRoleId: params.approverRoleId };
}

/** The request body for the kind that is selected; an empty source list (= every source) is left out. */
export function buildRuleInput(values: RuleFormValues, isEnabled?: boolean): WorkflowRuleInput {
  const name = values.name.trim();
  const enabled = isEnabled === undefined ? {} : { isEnabled };
  if (values.kind === "leadAssignment") {
    return {
      name,
      kind: values.kind,
      ...enabled,
      params: {
        ...(values.sources.length > 0 ? { sources: values.sources as LeadSource[] } : {}),
        assigneeRoleId: values.assigneeRoleId,
        followUpHours: Number(values.followUpHours),
      },
    };
  }
  return {
    name,
    kind: values.kind,
    ...enabled,
    params: { minAmount: Number(values.minAmount), approverRoleId: values.approverRoleId },
  };
}

/** Form field that shows `workflow.role_not_found` for the given kind. */
export function roleFieldOf(kind: WorkflowKind): "assigneeRoleId" | "approverRoleId" {
  return kind === "leadAssignment" ? "assigneeRoleId" : "approverRoleId";
}

/**
 * Puts server `errors` on the form. Paths are lower-camel-cased per segment; `params.<field>`
 * (also `Params.Sources[1]`) lands on the flat form field `<field>`. Returns true when at least one
 * field matched, so the caller can skip the generic toast.
 */
export function applyRuleServerErrors<T extends FieldValues>(
  error: unknown,
  setError: UseFormSetError<T>,
  fields: readonly Path<T>[]
): boolean {
  const errors = getApiProblem(error)?.errors;
  if (!errors) return false;
  let matched = false;
  for (const [rawPath, messages] of Object.entries(errors)) {
    const path = rawPath
      .replace(/\[\d+\]/g, "")
      .split(".")
      .map((segment) => segment.charAt(0).toLowerCase() + segment.slice(1));
    const field = (path[0] === "params" ? path.slice(1) : path).join(".");
    const target = fields.find((f) => f === field);
    if (target && messages[0]) {
      setError(target, { type: "server", message: messages[0] });
      matched = true;
    }
  }
  return matched;
}

const YMD = /^(\d{4})-(\d{2})-(\d{2})$/;

function parseYmd(value: string): Ymd | null {
  const match = YMD.exec(value);
  if (!match) return null;
  return { year: Number(match[1]), month: Number(match[2]), day: Number(match[3]) };
}

/**
 * UTC ISO bounds for a `from`/`to` pair of calendar days (`YYYY-MM-DD`) of the organization's time
 * zone: `from` is the start of its day, `to` the last millisecond of its day. Invalid days are left out.
 */
export function dayRangeIso(
  from: string,
  to: string,
  timeZone: string | undefined
): { from?: string; to?: string } {
  const start = parseYmd(from);
  const end = parseYmd(to);
  const result: { from?: string; to?: string } = {};
  if (start) result.from = zonedInstant(start, 0, 0, timeZone).toISOString();
  if (end) {
    const next = new Date(Date.UTC(end.year, end.month - 1, end.day + 1));
    const nextStart = zonedInstant(
      { year: next.getUTCFullYear(), month: next.getUTCMonth() + 1, day: next.getUTCDate() },
      0,
      0,
      timeZone
    );
    result.to = new Date(nextStart.getTime() - 1).toISOString();
  }
  return result;
}

/** `YYYY-MM-DD` shown back in a date input (kept as is when valid). */
export const validYmd = (value: string): string => {
  const ymd = parseYmd(value);
  return ymd ? formatYmd(ymd) : "";
};

/** Detail page of the record an execution or approval is about. */
export function subjectPath(subjectType: string, subjectId: string): string | null {
  if (subjectType === "lead") return `/app/leads/${encodeURIComponent(subjectId)}`;
  if (subjectType === "deal") return `/app/deals/${encodeURIComponent(subjectId)}`;
  return null;
}

/** Permission needed to open the record page of `subjectType`. */
export function subjectReadPermission(subjectType: string): string | null {
  if (subjectType === "lead") return PERMISSIONS.crmLeadsRead;
  if (subjectType === "deal") return PERMISSIONS.crmDealsRead;
  return null;
}
