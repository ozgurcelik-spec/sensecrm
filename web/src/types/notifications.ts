/**
 * Notifications (M8A, `docs/plan/m8a-bildirimler.md`): DTOs of `/notifications`. JSON is camelCase;
 * `null` fields are absent. Exported names are prefixed (`AppNotification`, `Notification*`) so they
 * never clash with the DOM `Notification` or the toast helpers.
 */

/** Notification kinds the UI words itself (the plan's catalog, minus the hidden `system.test_email`). */
export const NOTIFICATION_KINDS = [
  "approval.requested",
  "approval.decided",
  "case.assigned",
  "case.resolved",
  "case.sla_at_risk",
  "case.sla_breached",
  "activity.due_soon",
  "activity.overdue",
  "activity.daily_agenda",
  "lead.assigned",
  "quote.accepted",
  "tenant.plan_changed",
  "tenant.suspended",
  "tenant.reactivated",
  "tenant.deletion_requested",
  "tenant.deletion_cancelled",
  "tenant.trial_ending",
  "tenant.trial_expired",
] as const;
/** Known kinds; the wire value stays a plain `string` because the server may add kinds later. */
export type NotificationKind = (typeof NOTIFICATION_KINDS)[number];

/** Delivery log kinds: the catalog plus the hidden self-test message. */
export const NOTIFICATION_DELIVERY_KINDS = [...NOTIFICATION_KINDS, "system.test_email"] as const;

export const NOTIFICATION_GROUPS = ["approvals", "service", "activities", "sales", "commerce", "account"] as const;
export type NotificationGroup = (typeof NOTIFICATION_GROUPS)[number];

export const NOTIFICATION_SEVERITIES = ["info", "warning", "critical"] as const;
export type NotificationSeverity = (typeof NOTIFICATION_SEVERITIES)[number];

/** Preference channels (`PUT /notifications/preferences`). */
export const NOTIFICATION_CHANNELS = ["inApp", "email", "sms"] as const;
export type NotificationChannel = (typeof NOTIFICATION_CHANNELS)[number];

/** Delivery channels (the in-app channel is the notification row itself). */
export const NOTIFICATION_DELIVERY_CHANNELS = ["email", "sms"] as const;
export type NotificationDeliveryChannel = (typeof NOTIFICATION_DELIVERY_CHANNELS)[number];

export const NOTIFICATION_READ_STATUSES = ["unread", "read"] as const;
export type NotificationReadStatus = (typeof NOTIFICATION_READ_STATUSES)[number];

export interface AppNotification {
  id: string;
  kind: string;
  severity: NotificationSeverity;
  /** Rendered by the server in the caller's language. */
  title: string;
  body: string;
  /** Relative SPA path, always starts with `/app/` (the client re-checks it). */
  link?: string;
  subject?: { type: string; id: string };
  isRead: boolean;
  isMandatory: boolean;
  createdAt: string;
  readAt?: string;
}

export interface NotificationListQuery {
  status?: string;
  /** Comma separated kinds. */
  kind?: string;
  severity?: string;
  page?: number;
  pageSize?: number;
  [key: string]: string | number | boolean | undefined | null;
}

export interface NotificationUnreadCount {
  /** Cut off at 1000 by the server. */
  unreadCount: number;
  criticalUnreadCount: number;
  /** Only present while a critical notification is unread. */
  newestCriticalId?: string;
}

export interface NotificationReadAllResult {
  updatedCount: number;
}

/** Why a channel cannot be used: `platform` (server not configured), `plan` (not in the plan), `tenant` (admin switched it off). */
export type NotificationUnavailableReason = "platform" | "plan" | "tenant";

export interface NotificationChannelAvailability {
  available: boolean;
  reason?: NotificationUnavailableReason;
  /** E-mail only: the last mails to the caller's address were rejected. */
  addressIssue?: boolean;
}

export interface NotificationPreferenceCell {
  enabled: boolean;
  default: boolean;
  /** Mandatory kind: always on, cannot be changed. */
  locked: boolean;
  /** SMS only: `false` when the kind never uses the channel. */
  supported?: boolean;
}

export interface NotificationPreferenceKind {
  kind: string;
  group: string;
  /** Source module (`workflows`, `service`, `commerce`): a group of a module the plan switches off is hidden. */
  module?: string;
  severity: NotificationSeverity;
  mandatory: boolean;
  channels: Record<NotificationChannel, NotificationPreferenceCell>;
}

export interface NotificationPreferences {
  channels: {
    inApp: NotificationChannelAvailability;
    email: NotificationChannelAvailability;
    sms: NotificationChannelAvailability;
  };
  kinds: NotificationPreferenceKind[];
}

export interface NotificationPreferenceChange {
  kind: string;
  channel: NotificationChannel;
  enabled: boolean;
}

export interface NotificationChannelState {
  enabled: boolean;
  availableByPlatform: boolean;
  availableByPlan: boolean;
  /** Platform, plan and tenant switch together. */
  effective: boolean;
}

export interface NotificationTenantSettings {
  email: NotificationChannelState;
  sms: NotificationChannelState;
  senderName?: string;
  replyTo?: string;
  usage: { emailsToday: number; dailyEmailLimit?: number };
}

/** `PUT /notifications/settings`: a full replacement (an omitted optional field is cleared). */
export interface NotificationSettingsInput {
  emailEnabled: boolean;
  smsEnabled: boolean;
  senderName?: string;
  replyTo?: string;
}

export const NOTIFICATION_DELIVERY_STATUSES = ["pending", "sending", "sent", "dead", "skipped"] as const;
export type NotificationDeliveryStatus = (typeof NOTIFICATION_DELIVERY_STATUSES)[number];

export const NOTIFICATION_ERROR_CODES = [
  "recipientRejected",
  "messageRejected",
  "temporaryFailure",
  "timeout",
  "connectionFailed",
  "authFailed",
  "tlsFailed",
  "expired",
] as const;

export const NOTIFICATION_SKIP_REASONS = [
  "noAddress",
  "addressInvalid",
  "channelDisabled",
  "tenantNotActive",
  "dailyLimit",
  "userInactive",
  "preferenceOff",
] as const;

export interface NotificationDelivery {
  id: string;
  notificationId: string;
  channel: NotificationDeliveryChannel;
  kind: string;
  status: NotificationDeliveryStatus;
  attempts: number;
  isMandatory: boolean;
  recipientUserId: string;
  recipientName?: string;
  /** Masked (`m***@acme.com.tr`): the full address is never stored or returned. */
  addressMasked?: string;
  errorCode?: string;
  skipReason?: string;
  nextAttemptAt?: string;
  sentAt?: string;
  createdAt: string;
  lastAttemptAt?: string;
}

export interface NotificationDeliveryQuery {
  channel?: string;
  status?: string;
  kind?: string;
  recipientUserId?: string;
  /** UTC day `YYYY-MM-DD`, inclusive. */
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
  [key: string]: string | number | boolean | undefined | null;
}

export interface NotificationDeliverySummary {
  counts: Record<NotificationDeliveryStatus, number>;
  sentToday: number;
  dailyEmailLimit?: number;
  oldestPendingAt?: string;
}

export interface NotificationRetryResult {
  retried: number;
}

export interface NotificationRecipientIssue {
  userId: string;
  userName?: string;
  channel: NotificationDeliveryChannel;
  hardFailures: number;
  invalidSince?: string;
  lastFailureAt?: string;
}

export interface NotificationTestEmailResult {
  deliveryId: string;
}
