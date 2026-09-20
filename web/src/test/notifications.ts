/** Fixtures for the Milestone 8A tests (notification bell, page, preferences, tenant settings, delivery log). */
import type {
  AppNotification,
  NotificationDelivery,
  NotificationPreferenceCell,
  NotificationPreferenceKind,
  NotificationPreferences,
  NotificationTenantSettings,
} from "@/types";

export function notification(id: string, overrides: Partial<AppNotification> = {}): AppNotification {
  return {
    id,
    kind: "approval.requested",
    severity: "info",
    title: `Bildirim ${id}`,
    body: `Gövde ${id}`,
    link: "/app/approvals",
    isRead: false,
    isMandatory: false,
    createdAt: "2026-09-20T09:00:00Z",
    ...overrides,
  };
}

const cell = (enabled: boolean, overrides: Partial<NotificationPreferenceCell> = {}): NotificationPreferenceCell => ({
  enabled,
  default: enabled,
  locked: false,
  ...overrides,
});

export function preferenceKind(
  kind: string,
  group: string,
  overrides: Partial<NotificationPreferenceKind> = {}
): NotificationPreferenceKind {
  return {
    kind,
    group,
    severity: "info",
    mandatory: false,
    channels: {
      inApp: cell(true),
      email: cell(true),
      sms: cell(false, { supported: false }),
    },
    ...overrides,
  };
}

/** A small slice of the catalog: approvals (workflows), a service kind, an activity kind and a mandatory account kind. */
export function preferences(overrides: Partial<NotificationPreferences> = {}): NotificationPreferences {
  return {
    channels: {
      inApp: { available: true },
      email: { available: true, addressIssue: false },
      sms: { available: false, reason: "platform" },
    },
    kinds: [
      preferenceKind("approval.requested", "approvals", { module: "workflows" }),
      preferenceKind("case.assigned", "service", { module: "service" }),
      preferenceKind("activity.daily_agenda", "activities", {
        channels: {
          inApp: cell(true),
          email: cell(false, { supported: false }),
          sms: cell(false, { supported: false }),
        },
      }),
      preferenceKind("tenant.suspended", "account", {
        mandatory: true,
        severity: "critical",
        channels: {
          inApp: cell(true, { locked: true }),
          email: cell(true, { locked: true }),
          sms: cell(false, { supported: true }),
        },
      }),
    ],
    ...overrides,
  };
}

export function tenantSettings(overrides: Partial<NotificationTenantSettings> = {}): NotificationTenantSettings {
  return {
    email: { enabled: true, availableByPlatform: true, availableByPlan: true, effective: true },
    sms: { enabled: false, availableByPlatform: false, availableByPlan: false, effective: false },
    senderName: "Acme A.Ş.",
    replyTo: "destek@acme.com.tr",
    usage: { emailsToday: 42, dailyEmailLimit: 500 },
    ...overrides,
  };
}

export function delivery(id: string, overrides: Partial<NotificationDelivery> = {}): NotificationDelivery {
  return {
    id,
    notificationId: `n-${id}`,
    channel: "email",
    kind: "case.sla_breached",
    status: "sent",
    attempts: 1,
    isMandatory: false,
    recipientUserId: "user-2",
    recipientName: "Mert Kaya",
    addressMasked: "m***@acme.com.tr",
    createdAt: "2026-09-20T09:00:00Z",
    lastAttemptAt: "2026-09-20T09:00:05Z",
    sentAt: "2026-09-20T09:00:05Z",
    ...overrides,
  };
}
