import { useTranslation } from "react-i18next";
import { Badge, Box, Group, Stack, Text, ThemeIcon } from "@mantine/core";
import { CircleAlert, Info, TriangleAlert } from "lucide-react";
import { formatRelativeTime } from "@/lib/notifications";
import type { AppNotification, NotificationSeverity } from "@/types";

const SEVERITY_COLOR: Record<NotificationSeverity, string> = {
  info: "blue",
  warning: "orange",
  critical: "red",
};

/** Severity glyph: info circle, warning triangle, critical alert (the label is for screen readers). */
export function SeverityIcon({ severity, size = 28 }: { severity: NotificationSeverity; size?: number }) {
  const { t } = useTranslation(["notifications"]);
  const Icon = severity === "critical" ? CircleAlert : severity === "warning" ? TriangleAlert : Info;
  return (
    <ThemeIcon
      variant="light"
      color={SEVERITY_COLOR[severity]}
      size={size}
      radius="xl"
      role="img"
      aria-label={t(`notifications:severities.${severity}`)}
    >
      <Icon size={Math.round(size * 0.57)} />
    </ThemeIcon>
  );
}

interface NotificationContentProps {
  notification: AppNotification;
  /** Two-line body (top bar dropdown) instead of the full text. */
  compact?: boolean;
}

/** Icon, title, body and age of one notification; unread ones are bold with a dot. */
export function NotificationContent({ notification, compact = false }: NotificationContentProps) {
  const { t } = useTranslation(["notifications"]);
  return (
    <Group gap="sm" wrap="nowrap" align="flex-start" data-unread={!notification.isRead || undefined}>
      <SeverityIcon severity={notification.severity} />
      <Stack gap={2} style={{ minWidth: 0, flex: 1 }}>
        <Group gap={6} wrap="nowrap">
          <Text size="sm" fw={notification.isRead ? 500 : 700} lineClamp={compact ? 1 : undefined}>
            {notification.title}
          </Text>
          {notification.isMandatory && (
            <Badge size="xs" variant="light" color="gray">
              {t("notifications:mandatoryBadge")}
            </Badge>
          )}
        </Group>
        <Text size="xs" c="dimmed" lineClamp={compact ? 2 : undefined} style={{ whiteSpace: "pre-wrap" }}>
          {notification.body}
        </Text>
        <Text size="xs" c="dimmed">
          {formatRelativeTime(notification.createdAt)}
        </Text>
      </Stack>
      {!notification.isRead && (
        <Box
          w={8}
          h={8}
          mt={6}
          style={{ borderRadius: "50%", flexShrink: 0, background: "var(--mantine-color-brand-6)" }}
          role="img"
          aria-label={t("notifications:unread")}
        />
      )}
    </Group>
  );
}
