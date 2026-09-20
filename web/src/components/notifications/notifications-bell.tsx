import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Button, Divider, Group, Indicator, Menu, Skeleton, Stack, Text } from "@mantine/core";
import { Bell, CheckCheck } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { useNotificationsAvailable } from "@/hooks/use-notification-access";
import {
  useMarkAllNotificationsRead,
  useMarkNotificationRead,
  useNotifications,
  useNotificationUnreadCount,
  useRefreshListsOnCountChange,
} from "@/hooks/use-notifications";
import { toastApiError } from "@/hooks/use-toast";
import { badgeLabel, safeNotificationLink } from "@/lib/notifications";
import type { AppNotification } from "@/types";
import { CriticalNoticeToaster } from "./critical-notice-toaster";
import { NotificationContent } from "./notification-item";

/** The list of the dropdown: the newest notifications only. */
const RECENT_QUERY = { page: 1, pageSize: 10 } as const;

function BellMenu() {
  const { t } = useTranslation(["notifications"]);
  const navigate = useNavigate();
  const [opened, setOpened] = useState(false);
  const { data: counts } = useNotificationUnreadCount();
  useRefreshListsOnCountChange(counts);
  const unread = counts?.unreadCount ?? 0;
  // The list is only requested while the menu is open (and refetched on every opening).
  const recent = useNotifications({ ...RECENT_QUERY }, opened);
  const markRead = useMarkNotificationRead();
  const markAll = useMarkAllNotificationsRead();

  function open(notification: AppNotification) {
    if (!notification.isRead) markRead.mutate(notification.id, { onError: toastApiError });
    // A link that is not an in-app path is ignored: the notification is only marked read.
    const link = safeNotificationLink(notification.link);
    if (link) void navigate(link);
  }

  const items = recent.data?.items;
  return (
    <>
      <Menu
        opened={opened}
        onChange={setOpened}
        position="bottom-end"
        width={380}
        shadow="md"
        withinPortal
      >
        <Menu.Target>
          <Indicator label={badgeLabel(unread)} size={16} color="red" disabled={unread === 0}>
            <ActionIcon
              variant="subtle"
              color="gray"
              size="lg"
              aria-label={unread > 0 ? t("notifications:bell.labelCount", { count: unread }) : t("notifications:bell.label")}
            >
              <Bell size={18} />
            </ActionIcon>
          </Indicator>
        </Menu.Target>
        <Menu.Dropdown>
          <Group justify="space-between" px="sm" py={6} wrap="nowrap">
            <Text fw={600} size="sm">
              {t("notifications:bell.title")}
            </Text>
            <Button
              variant="subtle"
              size="compact-xs"
              leftSection={<CheckCheck size={14} />}
              disabled={unread === 0 || markAll.isPending}
              onClick={() => markAll.mutate(undefined, { onError: toastApiError })}
            >
              {t("notifications:markAllRead")}
            </Button>
          </Group>
          <Divider />
          <Stack gap={0} mah={420} style={{ overflowY: "auto" }} data-testid="bell-list">
            {recent.error ? (
              <Stack p="sm">
                <LoadError error={recent.error} onRetry={() => void recent.refetch()} />
              </Stack>
            ) : !items ? (
              <Stack p="sm" gap="xs">
                <Skeleton h={36} />
                <Skeleton h={36} />
                <Skeleton h={36} />
              </Stack>
            ) : items.length === 0 ? (
              <Text size="sm" c="dimmed" ta="center" py="lg">
                {t("notifications:bell.empty")}
              </Text>
            ) : (
              items.map((notification) => (
                <Menu.Item key={notification.id} onClick={() => open(notification)}>
                  <NotificationContent notification={notification} compact />
                </Menu.Item>
              ))
            )}
          </Stack>
          <Divider />
          <Group justify="space-between" px="sm" py={6} wrap="nowrap">
            <Button component={Link} to="/app/notifications" variant="subtle" size="compact-sm" onClick={() => setOpened(false)}>
              {t("notifications:bell.viewAll")}
            </Button>
            <Button
              component={Link}
              to="/app/notifications/preferences"
              variant="subtle"
              size="compact-sm"
              color="gray"
              onClick={() => setOpened(false)}
            >
              {t("notifications:bell.preferences")}
            </Button>
          </Group>
        </Menu.Dropdown>
      </Menu>
      <CriticalNoticeToaster counts={counts} />
    </>
  );
}

/**
 * Top bar bell: unread badge (polled every 60 s while the tab is visible) and a dropdown with the
 * latest notifications. Shown to every signed-in member; a blocked tenant (`accessLevel: none`)
 * makes no request at all.
 */
export function NotificationsBell() {
  const available = useNotificationsAvailable();
  return available ? <BellMenu /> : null;
}
