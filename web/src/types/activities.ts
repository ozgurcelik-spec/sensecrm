/**
 * Milestone 3 API types (activities and reports) - see docs/plan/m3-aktivite-rapor.md. JSON is
 * camelCase, enums are lowercase strings and null fields are absent from responses.
 */

export const ACTIVITY_TYPES = ["task", "call", "meeting", "note"] as const;
export type ActivityType = (typeof ACTIVITY_TYPES)[number];

export const ACTIVITY_STATUSES = ["open", "completed", "cancelled"] as const;
export type ActivityStatus = (typeof ACTIVITY_STATUSES)[number];

export const ACTIVITY_PRIORITIES = ["low", "normal", "high"] as const;
export type ActivityPriority = (typeof ACTIVITY_PRIORITIES)[number];

export const RELATED_TYPES = ["account", "contact", "lead", "deal"] as const;
export type RelatedType = (typeof RELATED_TYPES)[number];

export interface Activity {
  id: string;
  type: ActivityType;
  subject: string;
  description?: string;
  status: ActivityStatus;
  priority: ActivityPriority;
  /** ISO date-time (tasks). */
  dueAt?: string;
  /** ISO date-time (calls and meetings). */
  startAt?: string;
  endAt?: string;
  relatedType?: RelatedType;
  relatedId?: string;
  /** Empty when the related record was deleted (the relation is soft). */
  relatedName?: string;
  assignedUserId: string;
  assignedUserName?: string;
  completedAt?: string;
  /** Open and `dueAt` in the past. */
  isOverdue: boolean;
  createdAt: string;
  updatedAt?: string;
}

export interface ActivityInput {
  type: ActivityType;
  subject: string;
  description?: string;
  /** Sent on update only; a new activity starts open (notes are always completed). */
  status?: ActivityStatus;
  priority?: ActivityPriority;
  dueAt?: string;
  startAt?: string;
  endAt?: string;
  relatedType?: RelatedType;
  relatedId?: string;
  assignedUserId?: string;
}

export interface ActivitySummary {
  openCount: number;
  overdueCount: number;
  dueTodayCount: number;
  completedThisWeek: number;
}

/** A related record chosen in a form (`name` is only used for display). */
export interface RelatedRecordRef {
  type: RelatedType;
  id: string;
  name?: string;
}

export interface FunnelStage {
  id: string;
  name: string;
  kind: "open" | "won" | "lost";
  order: number;
  probability: number;
  count: number;
  totalAmount: number;
}

export interface SalesFunnelReport {
  pipelineId: string;
  stages: FunnelStage[];
}

export type WonLostGroupBy = "month" | "week";

export interface WonLostRow {
  /** "2026-09" (month) or "2026-W38" (ISO week). */
  period: string;
  wonCount: number;
  wonAmount: number;
  lostCount: number;
  lostAmount: number;
}

export interface LeadSourceRow {
  source: string;
  count: number;
  convertedCount: number;
}

export interface OwnerReportRow {
  ownerUserId: string;
  ownerName: string;
  openDealCount: number;
  openDealAmount: number;
  wonCount: number;
  wonAmount: number;
  leadCount: number;
}

export interface ActivityUserRow {
  userId: string;
  userName: string;
  completedCount: number;
  openCount: number;
  overdueCount: number;
}
