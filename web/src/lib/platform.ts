/** Pure helpers of the platform console: statuses, allowed actions, overrides, dates, server errors. */
import { getApiProblem } from "@/lib/api-error";
import {
  GATED_MODULES,
  RECORD_MODULES,
  type GatedModule,
  type PlatformOrganizationDetail,
  type PlatformOverrides,
  type PlatformTenantStatus,
} from "@/types";

/** Badge colors by effective status (plan: trial blue, active green, expired / suspended red, deletion grey-red). */
export const STATUS_COLOR: Record<PlatformTenantStatus, string> = {
  trial: "blue",
  active: "green",
  trial_expired: "red",
  suspended: "red",
  pending_deletion: "pink",
  deleted: "gray",
};

/** Audit actions the platform writes (`platform_audit_entries.action`). */
export const PLATFORM_AUDIT_ACTIONS = [
  "organization.created",
  "subscription.changed",
  "organization.suspended",
  "organization.reactivated",
  "deletion.requested",
  "deletion.cancelled",
  "deletion.completed",
  "deletion.failed",
  "usage.refreshed",
  "usage.exported",
] as const;

export const SUSPEND_REASON_MAX = 500;
export const MIN_RETENTION_DAYS = 7;
export const MAX_RETENTION_DAYS = 90;
export const DEFAULT_RETENTION_DAYS = 30;
/** The usage export / series never spans more than this many days. */
export const MAX_USAGE_RANGE_DAYS = 400;

const LIVE_DELETION = ["scheduled", "running", "failed"] as const;

export interface OrganizationActions {
  /** Every action is off for the operating (system) organization. */
  systemProtected: boolean;
  editPlan: boolean;
  suspend: boolean;
  reactivate: boolean;
  requestDeletion: boolean;
  cancelDeletion: boolean;
}

/**
 * Which lifecycle actions the detail page offers, from the effective status and the latest
 * deletion request (the server rejects the rest with `platform.invalid_transition`).
 */
export function organizationActions(
  org: Pick<PlatformOrganizationDetail, "status" | "isSystem" | "deletion">,
  now: number = Date.now()
): OrganizationActions {
  const active = org.status === "trial" || org.status === "active" || org.status === "trial_expired";
  const suspended = org.status === "suspended";
  const hasLiveDeletion = !!org.deletion && (LIVE_DELETION as readonly string[]).includes(org.deletion.status);
  const cancellable =
    org.status === "pending_deletion" &&
    org.deletion?.status === "scheduled" &&
    new Date(org.deletion.scheduledFor).getTime() > now;
  const off = org.isSystem;
  return {
    systemProtected: org.isSystem,
    editPlan: !off && (active || suspended),
    suspend: !off && active,
    reactivate: !off && suspended,
    requestDeletion: !off && (active || suspended) && !hasLiveDeletion,
    cancelDeletion: !off && cancellable,
  };
}

// ---- Overrides <-> form state ------------------------------------------------------------------------

/** How one limit is set: follow the plan, explicitly unlimited, or a number. */
export type LimitMode = "plan" | "unlimited" | "custom";
export type ModuleMode = "plan" | "on" | "off";

export interface LimitDraft {
  mode: LimitMode;
  value: number | "";
}

export interface OverridesDraft {
  maxUsers: LimitDraft;
  /** Storage quota in MB (M8C). */
  maxStorageMb: LimitDraft;
  maxRecords: Record<string, LimitDraft>;
  modules: Record<GatedModule, ModuleMode>;
}

function limitDraft(present: boolean, value: number | null | undefined): LimitDraft {
  if (!present) return { mode: "plan", value: "" };
  return value === null || value === undefined
    ? { mode: "unlimited", value: "" }
    : { mode: "custom", value };
}

/** Case-insensitive property lookup: the server stores the overrides JSON exactly as it was sent. */
function pick(source: object | undefined, name: string): { present: boolean; value: unknown } {
  if (!source) return { present: false, value: undefined };
  const key = Object.keys(source).find((k) => k.toLowerCase() === name.toLowerCase());
  return key === undefined
    ? { present: false, value: undefined }
    : { present: true, value: (source as Record<string, unknown>)[key] };
}

export function toOverridesDraft(overrides: PlatformOverrides | undefined): OverridesDraft {
  const maxUsers = pick(overrides, "maxUsers");
  const maxStorageMb = pick(overrides, "maxStorageMb");
  const records = pick(overrides, "maxRecords").value as Record<string, number | null> | undefined;
  const modules = pick(overrides, "modules").value as Record<string, boolean> | undefined;
  return {
    maxUsers: limitDraft(maxUsers.present, maxUsers.value as number | null | undefined),
    maxStorageMb: limitDraft(
      maxStorageMb.present,
      maxStorageMb.value as number | null | undefined
    ),
    maxRecords: Object.fromEntries(
      RECORD_MODULES.map((module) => {
        const entry = pick(records, module);
        return [module, limitDraft(entry.present, entry.value as number | null | undefined)];
      })
    ),
    modules: Object.fromEntries(
      GATED_MODULES.map((module) => {
        const entry = pick(modules, module);
        const mode: ModuleMode = !entry.present ? "plan" : entry.value === true ? "on" : "off";
        return [module, mode];
      })
    ) as Record<GatedModule, ModuleMode>,
  };
}

function limitValue(draft: LimitDraft): { set: boolean; value: number | null } {
  if (draft.mode === "plan") return { set: false, value: null };
  if (draft.mode === "unlimited") return { set: true, value: null };
  return { set: true, value: draft.value === "" ? 0 : draft.value };
}

/** Body `overrides`; undefined when nothing is overridden (the PUT then clears them). */
export function toOverridesBody(draft: OverridesDraft): PlatformOverrides | undefined {
  const body: PlatformOverrides = {};
  const users = limitValue(draft.maxUsers);
  if (users.set) body.maxUsers = users.value;
  const storage = limitValue(draft.maxStorageMb);
  if (storage.set) body.maxStorageMb = storage.value;

  const maxRecords: Record<string, number | null> = {};
  for (const [module, limit] of Object.entries(draft.maxRecords)) {
    const entry = limitValue(limit);
    if (entry.set) maxRecords[module] = entry.value;
  }
  if (Object.keys(maxRecords).length > 0) body.maxRecords = maxRecords;

  const modules: Partial<Record<GatedModule, boolean>> = {};
  for (const module of GATED_MODULES) {
    if (draft.modules[module] !== "plan") modules[module] = draft.modules[module] === "on";
  }
  if (Object.keys(modules).length > 0) body.modules = modules;

  return Object.keys(body).length > 0 ? body : undefined;
}

/** True when a draft limit is a custom number that is not a whole number >= 0. */
export function isInvalidLimit(draft: LimitDraft): boolean {
  return (
    draft.mode === "custom" &&
    (draft.value === "" || !Number.isInteger(draft.value) || draft.value < 0)
  );
}

// ---- Server validation errors ------------------------------------------------------------------------

/** `errors` of a 400 mapped to lower-camel dotted paths (`Overrides.MaxUsers` -> `overrides.maxUsers`), first message each. */
export function serverFieldErrors(error: unknown): Record<string, string> {
  const errors = getApiProblem(error)?.errors;
  const result: Record<string, string> = {};
  if (!errors) return result;
  for (const [raw, messages] of Object.entries(errors)) {
    const path = raw
      .split(".")
      .map((segment) => segment.charAt(0).toLowerCase() + segment.slice(1))
      .join(".");
    if (messages[0]) result[path] = messages[0];
  }
  return result;
}

// ---- Dates ---------------------------------------------------------------------------------------------

/** `YYYY-MM-DD` of a date in UTC. */
export function utcDay(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/** UTC day `days` days before `now` (the usage snapshots are UTC days). */
export function daysAgo(days: number, now: Date = new Date()): string {
  return utcDay(new Date(now.getTime() - days * 86_400_000));
}

/** Whole days between two `YYYY-MM-DD` days (`to - from`); NaN for invalid input. */
export function dayDiff(from: string, to: string): number {
  const a = Date.parse(`${from}T00:00:00Z`);
  const b = Date.parse(`${to}T00:00:00Z`);
  return Math.round((b - a) / 86_400_000);
}

export const isDay = (value: string): boolean => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(`${value}T00:00:00Z`));

/** Saves a fetched file through a temporary link (the CSV comes from the server, already UTF-8 with BOM). */
export function downloadBlob(filename: string, blob: Blob): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

// ---- Usage -------------------------------------------------------------------------------------------

/** Metric keys of a usage series (`sales.records`, `commerce.quotes`, ...), records totals first, sorted. */
export function metricKeys(days: readonly { metrics: Record<string, number> }[]): string[] {
  const keys = new Set<string>();
  for (const day of days) for (const key of Object.keys(day.metrics)) keys.add(key);
  return [...keys].sort((a, b) => {
    const aTotal = a.endsWith(".records");
    const bTotal = b.endsWith(".records");
    if (aTotal !== bTotal) return aTotal ? -1 : 1;
    return a.localeCompare(b);
  });
}
