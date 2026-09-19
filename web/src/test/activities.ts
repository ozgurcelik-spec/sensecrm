import type { Activity } from "@/types";

/** An open task assigned to the test user; override what a test cares about. */
export function activity(id: string, overrides: Partial<Activity> = {}): Activity {
  return {
    id,
    type: "task",
    subject: `Görev ${id}`,
    status: "open",
    priority: "normal",
    assignedUserId: "user-1",
    assignedUserName: "Ada Lovelace",
    isOverdue: false,
    createdAt: "2026-05-01T10:00:00Z",
    ...overrides,
  };
}
