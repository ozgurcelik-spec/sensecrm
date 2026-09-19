/**
 * Milestone 6B API types (service: cases, comments, SLA) - see docs/plan/m6b-servis.md. JSON is
 * camelCase, enums are camelCase strings and null fields are absent from responses.
 */

export const CASE_STATUSES = ["new", "open", "pending", "resolved", "closed"] as const;
export type CaseStatus = (typeof CASE_STATUSES)[number];

/** Statuses of a case that is still being worked on (edit / priority / assignment are allowed). */
export const ACTIVE_CASE_STATUSES = ["new", "open", "pending"] as const;

export const CASE_PRIORITIES = ["low", "normal", "high", "urgent"] as const;
export type CasePriority = (typeof CASE_PRIORITIES)[number];

export const CASE_CHANNELS = ["email", "phone", "web", "other"] as const;
export type CaseChannel = (typeof CASE_CHANNELS)[number];

export const SLA_STATES = ["ok", "atRisk", "breached"] as const;
export type SlaState = (typeof SLA_STATES)[number];

export const COMMENT_VISIBILITIES = ["public", "internal"] as const;
export type CommentVisibility = (typeof COMMENT_VISIBILITIES)[number];

export interface CaseListItem {
  id: string;
  number: string;
  subject: string;
  status: CaseStatus;
  priority: CasePriority;
  channel: CaseChannel;
  accountId?: string;
  /** Empty when the linked account was deleted (the link is soft). */
  accountName?: string;
  contactId?: string;
  contactName?: string;
  assignedUserId?: string;
  assignedUserName?: string;
  reopenCount: number;
  firstResponseAt?: string;
  resolvedAt?: string;
  closedAt?: string;
  firstResponseDueAt: string;
  /** Resolution target. */
  dueAt: string;
  isSlaBreached: boolean;
  slaState: SlaState;
  firstResponseBreached: boolean;
  resolutionBreached: boolean;
  createdAt: string;
  updatedAt?: string;
  createdByUserId: string;
  createdByName?: string;
}

export interface CaseDetail extends CaseListItem {
  description?: string;
  resolutionNote?: string;
}

/** `POST /cases`. */
export interface CaseCreateInput {
  subject: string;
  description?: string;
  accountId?: string;
  contactId?: string;
  priority?: CasePriority;
  channel?: CaseChannel;
  assignedUserId?: string;
}

/** `PUT /cases/{id}` (full replacement of these fields; status, priority and assignee are separate actions). */
export interface CaseUpdateInput {
  subject: string;
  description?: string;
  accountId?: string;
  contactId?: string;
  channel?: CaseChannel;
}

export interface CaseStatusInput {
  status: CaseStatus;
  resolutionNote?: string;
}

export interface CaseCommentInput {
  visibility: CommentVisibility;
  body: string;
}

export interface CaseComment {
  id: string;
  caseId: string;
  visibility: CommentVisibility;
  body: string;
  authorUserId: string;
  authorName?: string;
  createdAt: string;
}

export const TIMELINE_TYPES = [
  "comment",
  "created",
  "statusChanged",
  "priorityChanged",
  "assigned",
] as const;
export type TimelineType = (typeof TIMELINE_TYPES)[number];

export interface TimelineItem {
  id: string;
  type: TimelineType;
  occurredAt: string;
  actorUserId?: string;
  actorName?: string;
  /** Comments only. */
  visibility?: CommentVisibility;
  body?: string;
  /** Status / priority values of `statusChanged` / `priorityChanged`. */
  from?: string;
  to?: string;
  /** User names of `assigned` (absent = unassigned). */
  fromName?: string;
  toName?: string;
  /** Resolution / closing note of a status change. */
  note?: string;
}

export interface CasesSummary {
  openCount: number;
  overdueCount: number;
  mineCount: number;
  unassignedCount: number;
}

export interface SlaPolicy {
  priority: CasePriority;
  firstResponseMinutes: number;
  resolutionMinutes: number;
}

export interface ServiceStatusCount {
  status: CaseStatus;
  count: number;
}

export interface ServicePriorityCount {
  priority: CasePriority;
  count: number;
}

export interface ServiceSummaryReport {
  from: string;
  to: string;
  totalCount: number;
  resolvedCount: number;
  byStatus: ServiceStatusCount[];
  byPriority: ServicePriorityCount[];
  /** Absent when no case has a first response in the range. */
  avgFirstResponseMinutes?: number;
  avgResolutionMinutes?: number;
  slaBreachedCount: number;
  /** 0..1 */
  slaBreachRate: number;
}

export interface ServiceAssigneeRow {
  /** Absent on the "unassigned" row. */
  assignedUserId?: string;
  assignedUserName?: string;
  totalCount: number;
  openCount: number;
  resolvedCount: number;
  avgFirstResponseMinutes?: number;
  avgResolutionMinutes?: number;
  slaBreachedCount: number;
}
