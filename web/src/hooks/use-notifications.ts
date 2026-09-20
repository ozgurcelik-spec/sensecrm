import { useEffect, useRef } from "react";
import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
} from "@tanstack/react-query";
import {
  dismissNotification,
  getDelivery,
  getDeliverySummary,
  getNotificationPreferences,
  getNotificationSettings,
  getUnreadCount,
  listDeliveries,
  listNotifications,
  listRecipientIssues,
  markAllNotificationsRead,
  markNotificationRead,
  notificationKeys,
  resetRecipientIssue,
  retryDeliveries,
  sendTestEmail,
  updateNotificationPreferences,
  updateNotificationSettings,
} from "@/services/notifications.service";
import { notificationPollDelay } from "@/lib/notifications";
import type {
  AppNotification,
  ListResult,
  NotificationDeliveryQuery,
  NotificationDeliveryStatus,
  NotificationListQuery,
  NotificationPreferenceChange,
  NotificationSettingsInput,
  NotificationUnreadCount,
} from "@/types";

type NotificationPage = ListResult<AppNotification>;

/**
 * Unread badge numbers of the top bar. Polled about every 60 s (with a random offset) while the tab
 * is visible only: React Query pauses the interval in a hidden tab, and the query refetches when the
 * tab becomes visible again. No websockets (plan D11).
 */
export function useNotificationUnreadCount(enabled = true) {
  return useQuery({
    queryKey: notificationKeys.unreadCount,
    queryFn: getUnreadCount,
    enabled,
    refetchInterval: () => notificationPollDelay(),
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: true,
    staleTime: 0,
  });
}

/** Refetches the notification lists whenever the polled unread count changes (not on the first value). */
export function useRefreshListsOnCountChange(count: NotificationUnreadCount | undefined): void {
  const queryClient = useQueryClient();
  const previous = useRef<string | undefined>(undefined);
  const signature = count ? `${count.unreadCount}|${count.newestCriticalId ?? ""}` : undefined;
  useEffect(() => {
    if (signature === undefined) return;
    if (previous.current !== undefined && previous.current !== signature) {
      void queryClient.invalidateQueries({ queryKey: notificationKeys.lists });
    }
    previous.current = signature;
  }, [signature, queryClient]);
}

export function useNotifications(query: NotificationListQuery, enabled = true) {
  return useQuery({
    queryKey: notificationKeys.list(query),
    queryFn: () => listNotifications(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

interface OptimisticSnapshot {
  lists: [readonly unknown[], NotificationPage | undefined][];
  count: NotificationUnreadCount | undefined;
}

/** Status filter of a cached list key (`["notifications", "list", query]`). */
function listStatus(key: readonly unknown[]): string | undefined {
  const query = key[2] as NotificationListQuery | undefined;
  return query?.status;
}

function snapshot(queryClient: QueryClient): OptimisticSnapshot {
  return {
    lists: queryClient.getQueriesData<NotificationPage>({ queryKey: notificationKeys.lists }),
    count: queryClient.getQueryData<NotificationUnreadCount>(notificationKeys.unreadCount),
  };
}

function restore(queryClient: QueryClient, saved: OptimisticSnapshot | undefined): void {
  if (!saved) return;
  for (const [key, data] of saved.lists) queryClient.setQueryData(key, data);
  queryClient.setQueryData(notificationKeys.unreadCount, saved.count);
}

function findCached(saved: OptimisticSnapshot, id: string): AppNotification | undefined {
  for (const [, data] of saved.lists) {
    const hit = data?.items.find((item) => item.id === id);
    if (hit) return hit;
  }
  return undefined;
}

function adjustCount(
  queryClient: QueryClient,
  change: (count: NotificationUnreadCount) => NotificationUnreadCount
): void {
  queryClient.setQueryData<NotificationUnreadCount>(notificationKeys.unreadCount, (count) =>
    count ? change(count) : count
  );
}

function decrement(count: NotificationUnreadCount, item: AppNotification): NotificationUnreadCount {
  const next: NotificationUnreadCount = {
    ...count,
    unreadCount: Math.max(0, count.unreadCount - 1),
    criticalUnreadCount:
      item.severity === "critical" ? Math.max(0, count.criticalUnreadCount - 1) : count.criticalUnreadCount,
  };
  if (item.severity === "critical" && count.newestCriticalId === item.id) delete next.newestCriticalId;
  if (next.criticalUnreadCount === 0) delete next.newestCriticalId;
  return next;
}

async function settle(queryClient: QueryClient) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: notificationKeys.lists }),
    queryClient.invalidateQueries({ queryKey: notificationKeys.unreadCount }),
  ]);
}

async function prepare(queryClient: QueryClient): Promise<OptimisticSnapshot> {
  // A poll answering after the optimistic write would put the stale number back.
  await Promise.all([
    queryClient.cancelQueries({ queryKey: notificationKeys.lists }),
    queryClient.cancelQueries({ queryKey: notificationKeys.unreadCount }),
  ]);
  return snapshot(queryClient);
}

/** Marks one notification read: the cached lists and the badge change at once and roll back on failure. */
export function useMarkNotificationRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => markNotificationRead(id),
    onMutate: async (id): Promise<OptimisticSnapshot> => {
      const saved = await prepare(queryClient);
      const item = findCached(saved, id);
      for (const [key, data] of saved.lists) {
        if (!data) continue;
        const status = listStatus(key);
        const hit = data.items.some((entry) => entry.id === id);
        if (!hit) continue;
        queryClient.setQueryData<NotificationPage>(key, {
          ...data,
          items:
            status === "unread"
              ? data.items.filter((entry) => entry.id !== id)
              : data.items.map((entry) => (entry.id === id ? { ...entry, isRead: true } : entry)),
          totalCount: status === "unread" ? Math.max(0, data.totalCount - 1) : data.totalCount,
        });
      }
      if (item && !item.isRead) adjustCount(queryClient, (count) => decrement(count, item));
      return saved;
    },
    onError: (_error, _id, saved) => restore(queryClient, saved),
    onSettled: () => settle(queryClient),
  });
}

/** Marks everything (optionally one kind) read. */
export function useMarkAllNotificationsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (kind?: string) => markAllNotificationsRead(kind),
    onMutate: async (kind): Promise<OptimisticSnapshot> => {
      const saved = await prepare(queryClient);
      for (const [key, data] of saved.lists) {
        if (!data) continue;
        const status = listStatus(key);
        const matches = (item: AppNotification) => !kind || item.kind === kind;
        const removed = status === "unread" ? data.items.filter(matches).length : 0;
        queryClient.setQueryData<NotificationPage>(key, {
          ...data,
          items:
            status === "unread"
              ? data.items.filter((item) => !matches(item))
              : data.items.map((item) => (matches(item) ? { ...item, isRead: true } : item)),
          totalCount: Math.max(0, data.totalCount - removed),
        });
      }
      // Without a kind everything is read; with one the server number arrives with the refetch.
      if (!kind) {
        adjustCount(queryClient, (count) => {
          const next = { ...count, unreadCount: 0, criticalUnreadCount: 0 };
          delete next.newestCriticalId;
          return next;
        });
      }
      return saved;
    },
    onError: (_error, _kind, saved) => restore(queryClient, saved),
    onSettled: () => settle(queryClient),
  });
}

/** Hides a notification ("delete"): gone from the lists at once, back on failure. */
export function useDismissNotification() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => dismissNotification(id),
    onMutate: async (id): Promise<OptimisticSnapshot> => {
      const saved = await prepare(queryClient);
      const item = findCached(saved, id);
      for (const [key, data] of saved.lists) {
        if (!data?.items.some((entry) => entry.id === id)) continue;
        queryClient.setQueryData<NotificationPage>(key, {
          ...data,
          items: data.items.filter((entry) => entry.id !== id),
          totalCount: Math.max(0, data.totalCount - 1),
        });
      }
      if (item && !item.isRead) adjustCount(queryClient, (count) => decrement(count, item));
      return saved;
    },
    onError: (_error, _id, saved) => restore(queryClient, saved),
    onSettled: () => settle(queryClient),
  });
}

export function useNotificationPreferences() {
  return useQuery({ queryKey: notificationKeys.preferences, queryFn: getNotificationPreferences });
}

export function useUpdateNotificationPreferences() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (items: NotificationPreferenceChange[]) => updateNotificationPreferences(items),
    // Also after a failure: a refused cell means the server's matrix differs from the screen.
    onSettled: () => queryClient.invalidateQueries({ queryKey: notificationKeys.preferences }),
  });
}

export function useNotificationSettings(enabled = true) {
  return useQuery({ queryKey: notificationKeys.settings, queryFn: getNotificationSettings, enabled });
}

export function useUpdateNotificationSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: NotificationSettingsInput) => updateNotificationSettings(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: notificationKeys.settings }),
  });
}

export function useDeliveries(query: NotificationDeliveryQuery, enabled = true) {
  return useQuery({
    queryKey: notificationKeys.deliveryList(query),
    queryFn: () => listDeliveries(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useDeliverySummary(from?: string, to?: string, enabled = true) {
  return useQuery({
    queryKey: notificationKeys.deliverySummary(from, to),
    queryFn: () => getDeliverySummary(from, to),
    enabled,
  });
}

const FINAL_STATUSES: readonly NotificationDeliveryStatus[] = ["sent", "dead", "skipped"];

/** One delivery; with `pollMs` it is re-read until it reaches a final status (the test e-mail result). */
export function useDelivery(id: string | undefined, pollMs?: number) {
  return useQuery({
    queryKey: notificationKeys.delivery(id ?? ""),
    queryFn: () => getDelivery(id as string),
    enabled: !!id,
    refetchInterval: pollMs
      ? (query) => (query.state.data && FINAL_STATUSES.includes(query.state.data.status) ? false : pollMs)
      : false,
  });
}

export function useRetryDeliveries() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) => retryDeliveries(ids),
    onSettled: () => queryClient.invalidateQueries({ queryKey: notificationKeys.deliveries }),
  });
}

export function useRecipientIssues(enabled = true) {
  return useQuery({ queryKey: notificationKeys.recipientIssues, queryFn: listRecipientIssues, enabled });
}

export function useResetRecipientIssue() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, channel }: { userId: string; channel: string }) =>
      resetRecipientIssue(userId, channel),
    onSettled: () => queryClient.invalidateQueries({ queryKey: notificationKeys.recipientIssues }),
  });
}

export function useSendTestEmail() {
  return useMutation({ mutationFn: sendTestEmail });
}
