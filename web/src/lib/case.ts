import i18n from "@/i18n";
import type { CasePriority, CaseStatus, SlaState } from "@/types";

/**
 * Case state machine of docs/plan/m6b-servis.md §3.2 (never back to `new`; a resolved or closed case
 * can only be reopened, a resolved one can also be closed). The server enforces it as well.
 */
export const CASE_TRANSITIONS: Readonly<Record<CaseStatus, readonly CaseStatus[]>> = {
  new: ["open", "pending", "resolved", "closed"],
  open: ["pending", "resolved", "closed"],
  pending: ["open", "resolved", "closed"],
  resolved: ["open", "closed"],
  closed: ["open"],
};

/** Days after `closedAt` in which a closed case can be reopened (`CaseRules.ReopenWindowDays`). */
export const REOPEN_WINDOW_DAYS = 14;

export function allowedTransitions(status: CaseStatus): readonly CaseStatus[] {
  return CASE_TRANSITIONS[status];
}

/** Edit, priority and assignment are only possible while the case is new, open or pending. */
export function isActiveStatus(status: CaseStatus): boolean {
  return status === "new" || status === "open" || status === "pending";
}

/** A resolution note is required to resolve, and to close a case that was not resolved first. */
export function needsResolutionNote(from: CaseStatus, to: CaseStatus): boolean {
  return to === "resolved" || (to === "closed" && from !== "resolved");
}

/** Reopening is a move back to `open` from resolved / closed. */
export function isReopen(from: CaseStatus, to: CaseStatus): boolean {
  return to === "open" && (from === "resolved" || from === "closed");
}

/** False once a closed case is older than the reopen window (the button is hidden; the server decides). */
export function canReopenClosed(
  closedAt: string | undefined,
  now: Date = new Date(),
  windowDays: number = REOPEN_WINDOW_DAYS
): boolean {
  if (!closedAt) return true;
  const closed = new Date(closedAt).getTime();
  if (Number.isNaN(closed)) return true;
  return now.getTime() - closed <= windowDays * 24 * 60 * 60 * 1000;
}

/** Whole minutes from `now` to `iso`; negative once the instant has passed. Rounded towards "later". */
export function minutesUntil(iso: string, now: Date = new Date()): number {
  const diff = new Date(iso).getTime() - now.getTime();
  return Math.ceil(diff / 60_000);
}

/** `135` -> "2 sa 15 dk" (UI language); days appear from 24 hours on, zero parts are left out. */
export function formatDuration(minutes: number): string {
  const total = Math.max(0, Math.round(minutes));
  const days = Math.floor(total / 1440);
  const hours = Math.floor((total % 1440) / 60);
  const mins = total % 60;
  const parts: string[] = [];
  if (days > 0) parts.push(`${days} ${i18n.t("service:duration.day")}`);
  if (hours > 0) parts.push(`${hours} ${i18n.t("service:duration.hour")}`);
  if (mins > 0 || parts.length === 0) parts.push(`${mins} ${i18n.t("service:duration.minute")}`);
  return parts.join(" ");
}

/** "2 sa 15 dk kaldı" / "40 dk geçti" for a target instant. */
export function formatSlaDelta(dueIso: string, now: Date = new Date()): string {
  const delta = minutesUntil(dueIso, now);
  return delta >= 0
    ? i18n.t("service:sla.remaining", { duration: formatDuration(delta) })
    : i18n.t("service:sla.overdue", { duration: formatDuration(-delta) });
}

export const SLA_COLOR: Record<SlaState, string> = { ok: "green", atRisk: "yellow", breached: "red" };

export const PRIORITY_BADGE_COLOR: Record<CasePriority, string> = {
  low: "gray",
  normal: "blue",
  high: "orange",
  urgent: "red",
};

export const STATUS_BADGE_COLOR: Record<CaseStatus, string> = {
  new: "blue",
  open: "cyan",
  pending: "yellow",
  resolved: "green",
  closed: "gray",
};
