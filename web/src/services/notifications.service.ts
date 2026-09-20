/** Notifications (Milestone 8A) - `/notifications`. */
import { apiClient } from "@/lib/api-client";
import type {
  AppNotification,
  ListResult,
  NotificationDelivery,
  NotificationDeliveryQuery,
  NotificationDeliverySummary,
  NotificationListQuery,
  NotificationPreferenceChange,
  NotificationPreferences,
  NotificationReadAllResult,
  NotificationRecipientIssue,
  NotificationRetryResult,
  NotificationSettingsInput,
  NotificationTenantSettings,
  NotificationTestEmailResult,
  NotificationUnreadCount,
} from "@/types";
import { cleanParams, getArray, getList, getOne, seg } from "./crm-http";

export const notificationKeys = {
  all: ["notifications"] as const,
  lists: ["notifications", "list"] as const,
  list: (query: NotificationListQuery) => ["notifications", "list", query] as const,
  unreadCount: ["notifications", "unread-count"] as const,
  preferences: ["notifications", "preferences"] as const,
  settings: ["notifications", "settings"] as const,
  deliveries: ["notifications", "deliveries"] as const,
  deliveryList: (query: NotificationDeliveryQuery) => ["notifications", "deliveries", "list", query] as const,
  delivery: (id: string) => ["notifications", "deliveries", "detail", id] as const,
  deliverySummary: (from?: string, to?: string) =>
    ["notifications", "deliveries", "summary", from ?? "", to ?? ""] as const,
  recipientIssues: ["notifications", "deliveries", "recipient-issues"] as const,
};

export const listNotifications = (query: NotificationListQuery): Promise<ListResult<AppNotification>> =>
  getList<AppNotification>("/notifications", { ...query });

export const getUnreadCount = (): Promise<NotificationUnreadCount> =>
  getOne<NotificationUnreadCount>("/notifications/unread-count");

export async function markNotificationRead(id: string): Promise<void> {
  await apiClient.post(`/notifications/${seg(id)}/read`);
}

export async function markAllNotificationsRead(kind?: string): Promise<NotificationReadAllResult> {
  const { data } = await apiClient.post<NotificationReadAllResult>(
    "/notifications/read-all",
    cleanParams({ kind })
  );
  return data;
}

/** "Delete" is a hide (`dismissed_at`): the row stays on the server. */
export async function dismissNotification(id: string): Promise<void> {
  await apiClient.delete(`/notifications/${seg(id)}`);
}

export const getNotificationPreferences = (): Promise<NotificationPreferences> =>
  getOne<NotificationPreferences>("/notifications/preferences");

export async function updateNotificationPreferences(items: NotificationPreferenceChange[]): Promise<void> {
  await apiClient.put("/notifications/preferences", { items });
}

export const getNotificationSettings = (): Promise<NotificationTenantSettings> =>
  getOne<NotificationTenantSettings>("/notifications/settings");

export async function updateNotificationSettings(input: NotificationSettingsInput): Promise<void> {
  await apiClient.put("/notifications/settings", input);
}

export const listDeliveries = (query: NotificationDeliveryQuery): Promise<ListResult<NotificationDelivery>> =>
  getList<NotificationDelivery>("/notifications/deliveries", { ...query });

export const getDelivery = (id: string): Promise<NotificationDelivery> =>
  getOne<NotificationDelivery>(`/notifications/deliveries/${seg(id)}`);

export async function getDeliverySummary(from?: string, to?: string): Promise<NotificationDeliverySummary> {
  const { data } = await apiClient.get<NotificationDeliverySummary>("/notifications/deliveries/summary", {
    params: cleanParams({ from, to }),
  });
  return data;
}

export async function retryDeliveries(ids: string[]): Promise<NotificationRetryResult> {
  const { data } = await apiClient.post<NotificationRetryResult>("/notifications/deliveries/retry", { ids });
  return data;
}

export const listRecipientIssues = (): Promise<NotificationRecipientIssue[]> =>
  getArray<NotificationRecipientIssue>("/notifications/deliveries/recipient-issues");

export async function resetRecipientIssue(userId: string, channel: string): Promise<void> {
  await apiClient.post(`/notifications/deliveries/recipient-issues/${seg(userId)}/reset`, undefined, {
    params: { channel },
  });
}

export async function sendTestEmail(): Promise<NotificationTestEmailResult> {
  const { data } = await apiClient.post<NotificationTestEmailResult>("/notifications/test-email");
  return data;
}
