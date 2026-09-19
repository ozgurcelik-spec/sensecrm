import type { CaseDetail, CaseListItem, TimelineItem } from "@/types";

/** An open, on-track case assigned to the test user; override what a test cares about. */
export function caseItem(id: string, overrides: Partial<CaseListItem> = {}): CaseListItem {
  return {
    id,
    number: `C-2026-000${id}`,
    subject: `Talep ${id}`,
    status: "open",
    priority: "normal",
    channel: "email",
    assignedUserId: "user-1",
    assignedUserName: "Ada Lovelace",
    reopenCount: 0,
    firstResponseAt: "2026-09-19T08:30:00Z",
    firstResponseDueAt: "2026-09-19T16:00:00Z",
    dueAt: "2099-01-01T08:00:00Z",
    isSlaBreached: false,
    slaState: "ok",
    firstResponseBreached: false,
    resolutionBreached: false,
    createdAt: "2026-09-19T08:00:00Z",
    createdByUserId: "user-1",
    createdByName: "Ada Lovelace",
    ...overrides,
  };
}

export function caseDetail(id: string, overrides: Partial<CaseDetail> = {}): CaseDetail {
  return { ...caseItem(id), description: "Fatura tutarı hatalı", ...overrides };
}

export function timelineItem(id: string, overrides: Partial<TimelineItem> = {}): TimelineItem {
  return {
    id,
    type: "comment",
    occurredAt: "2026-09-19T09:00:00Z",
    actorUserId: "user-1",
    actorName: "Ada Lovelace",
    visibility: "public",
    body: `Yorum ${id}`,
    ...overrides,
  };
}
