import { blankToUndefined } from "@/lib/format";
import { fromZonedInput } from "@/lib/zoned-time";
import {
  PERMISSIONS,
  type Activity,
  type ActivityInput,
  type ActivityPriority,
  type ActivityStatus,
  type ActivityType,
  type RelatedType,
} from "@/types";

/** Detail route segment of each related record type. */
export const RELATED_PATH: Record<RelatedType, string> = {
  account: "accounts",
  contact: "contacts",
  lead: "leads",
  deal: "deals",
};

export const RELATED_READ_PERMISSION: Record<RelatedType, string> = {
  account: PERMISSIONS.crmAccountsRead,
  contact: PERMISSIONS.crmContactsRead,
  lead: PERMISSIONS.crmLeadsRead,
  deal: PERMISSIONS.crmDealsRead,
};

export const TYPE_COLOR: Record<ActivityType, string> = {
  task: "blue",
  call: "teal",
  meeting: "violet",
  note: "gray",
};

export const PRIORITY_COLOR: Record<ActivityPriority, string> = {
  low: "gray",
  normal: "blue",
  high: "red",
};

export const STATUS_COLOR: Record<ActivityStatus, string> = {
  open: "blue",
  completed: "green",
  cancelled: "gray",
};

/** Tasks are due; calls and meetings start; notes have no date of their own. */
export const usesDue = (type: ActivityType) => type === "task";
export const usesRange = (type: ActivityType) => type === "call" || type === "meeting";

/** The date column of an activity: `dueAt` for tasks, `startAt` for calls and meetings. */
export function activityDate(activity: Activity): string | undefined {
  return activity.dueAt ?? activity.startAt;
}

/** A note is always completed, so it has no complete/reopen action. */
export const hasStatusAction = (activity: Activity) => activity.type !== "note";

/** Form state of the activity dialog (all strings; date-times are `datetime-local` values). */
export interface ActivityFormValues {
  type: ActivityType;
  subject: string;
  description: string;
  status: ActivityStatus;
  priority: ActivityPriority;
  dueAt: string;
  startAt: string;
  endAt: string;
  relatedType: string;
  relatedId: string;
  assignedUserId: string;
}

/**
 * Request body of an activity: only the fields the type uses are sent (due for tasks, start/end for
 * calls and meetings, nothing date-like and no priority/status for notes). Status is sent on update
 * only; a new activity always starts open (a note is completed by the server).
 */
export function buildActivityInput(
  values: ActivityFormValues,
  timeZone: string | undefined,
  isEdit: boolean
): ActivityInput {
  const input: ActivityInput = {
    type: values.type,
    subject: values.subject.trim(),
    description: blankToUndefined(values.description),
    assignedUserId: blankToUndefined(values.assignedUserId),
  };
  if (values.type !== "note") {
    input.priority = values.priority;
    if (isEdit) input.status = values.status;
  }
  if (usesDue(values.type)) input.dueAt = fromZonedInput(values.dueAt, timeZone);
  if (usesRange(values.type)) {
    input.startAt = fromZonedInput(values.startAt, timeZone);
    input.endAt = fromZonedInput(values.endAt, timeZone);
  }
  if (values.relatedType && values.relatedId) {
    input.relatedType = values.relatedType as RelatedType;
    input.relatedId = values.relatedId;
  }
  return input;
}
