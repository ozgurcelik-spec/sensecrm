import { useMemo } from "react";
import { Link, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Anchor, Badge, Button, Group, Select, Tabs, Text, Tooltip } from "@mantine/core";
import { Check, CheckCheck, Settings2, X } from "lucide-react";
import { DataTable, type Column } from "@/components/crm/data-table";
import { SeverityIcon } from "@/components/notifications/notification-item";
import { PageHeader } from "@/components/page-header";
import { useListParams } from "@/hooks/use-list-params";
import {
  useDismissNotification,
  useMarkAllNotificationsRead,
  useMarkNotificationRead,
  useNotifications,
} from "@/hooks/use-notifications";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { groupLabel, kindGroup, kindLabel, safeNotificationLink } from "@/lib/notifications";
import { useAuthStore } from "@/store/auth.store";
import {
  NOTIFICATION_KINDS,
  NOTIFICATION_SEVERITIES,
  type AppNotification,
  type NotificationListQuery,
} from "@/types";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["status", "kind", "severity"] as const;

/** Kind filter options grouped like the preferences page (catalog order). */
function kindOptions() {
  const groups = new Map<string, { value: string; label: string }[]>();
  for (const kind of NOTIFICATION_KINDS) {
    const group = kindGroup(kind);
    groups.set(group, [...(groups.get(group) ?? []), { value: kind, label: kindLabel(kind) }]);
  }
  return [...groups].map(([group, items]) => ({ group: groupLabel(group), items }));
}

/**
 * "Bildirimler": the caller's notifications, newest first. Tabs (all / unread), kind and severity
 * filters and paging live in the URL; a row can be marked read or hidden, and its title opens the
 * record it is about (the notification is marked read on the way).
 */
export default function NotificationsPage() {
  const { t } = useTranslation(["notifications", "common"]);
  const navigate = useNavigate();
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const params = useListParams(FILTERS);
  const { status, kind, severity } = params.filters;
  const query = useMemo<NotificationListQuery>(
    () => ({
      page: params.page,
      pageSize: params.pageSize,
      status: status === "unread" || status === "read" ? status : undefined,
      kind: kind || undefined,
      severity: severity || undefined,
    }),
    [params.page, params.pageSize, status, kind, severity]
  );
  const { data, isLoading, isFetching, error, refetch } = useNotifications(query);
  const markRead = useMarkNotificationRead();
  const markAll = useMarkAllNotificationsRead();
  const dismiss = useDismissNotification();
  const kinds = kindOptions();

  const tab = status === "unread" ? "unread" : status === "read" ? "read" : "all";

  function open(notification: AppNotification) {
    if (!notification.isRead) markRead.mutate(notification.id, { onError: toastApiError });
    const link = safeNotificationLink(notification.link);
    if (link) void navigate(link);
  }

  async function onMarkAll() {
    try {
      // The contract takes one kind (or none = everything); a hand-edited list of kinds goes one by one.
      const selected = kind ? kind.split(",").filter(Boolean) : [undefined];
      let total = 0;
      for (const single of selected) total += (await markAll.mutateAsync(single)).updatedCount;
      toast({ variant: "success", description: t("notifications:page.markedAll", { count: total }) });
    } catch (failure) {
      toastApiError(failure);
    }
  }

  const columns: Column<AppNotification>[] = [
    {
      key: "notification",
      header: t("notifications:page.columns.notification"),
      render: (n) => {
        const link = safeNotificationLink(n.link);
        return (
          <Group gap="sm" wrap="nowrap" align="flex-start">
            <SeverityIcon severity={n.severity} size={26} />
            <div style={{ minWidth: 0 }}>
              <Group gap={6} wrap="nowrap">
                {link ? (
                  <Anchor
                    component="button"
                    type="button"
                    size="sm"
                    fw={n.isRead ? 500 : 700}
                    ta="left"
                    onClick={() => open(n)}
                  >
                    {n.title}
                  </Anchor>
                ) : (
                  <Text size="sm" fw={n.isRead ? 500 : 700}>
                    {n.title}
                  </Text>
                )}
                {!n.isRead && (
                  <Badge size="xs" variant="filled" color="brand">
                    {t("notifications:new")}
                  </Badge>
                )}
                {n.isMandatory && (
                  <Badge size="xs" variant="light" color="gray">
                    {t("notifications:mandatoryBadge")}
                  </Badge>
                )}
              </Group>
              <Text size="xs" c="dimmed" style={{ whiteSpace: "pre-wrap" }}>
                {n.body}
              </Text>
            </div>
          </Group>
        );
      },
    },
    {
      key: "kind",
      header: t("notifications:page.columns.kind"),
      render: (n) => (
        <Text size="sm">{kindLabel(n.kind)}</Text>
      ),
    },
    {
      key: "createdAt",
      header: t("notifications:page.columns.createdAt"),
      render: (n) => (
        <Text size="sm" style={{ whiteSpace: "nowrap" }}>
          {formatDateTime(n.createdAt, timeZone)}
        </Text>
      ),
    },
    {
      key: "actions",
      header: "",
      width: 96,
      render: (n) => (
        <Group gap={4} wrap="nowrap" justify="flex-end">
          {!n.isRead && (
            <Tooltip label={t("notifications:page.markRead")}>
              <ActionIcon
                variant="subtle"
                aria-label={t("notifications:page.markReadNamed", { title: n.title })}
                onClick={() => markRead.mutate(n.id, { onError: toastApiError })}
              >
                <Check size={16} />
              </ActionIcon>
            </Tooltip>
          )}
          <Tooltip label={t("notifications:page.dismiss")}>
            <ActionIcon
              variant="subtle"
              color="red"
              aria-label={t("notifications:page.dismissNamed", { title: n.title })}
              onClick={() => dismiss.mutate(n.id, { onError: toastApiError })}
            >
              <X size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        title={t("notifications:page.title")}
        description={t("notifications:page.description")}
        actions={
          <>
            <Button
              variant="default"
              leftSection={<CheckCheck size={16} />}
              loading={markAll.isPending}
              onClick={() => void onMarkAll()}
            >
              {t("notifications:markAllRead")}
            </Button>
            <Button
              component={Link}
              to="/app/notifications/preferences"
              variant="default"
              leftSection={<Settings2 size={16} />}
            >
              {t("notifications:page.preferences")}
            </Button>
          </>
        }
      />
      <Tabs
        value={tab}
        onChange={(value) => params.setFilter("status", value === "all" ? null : value)}
        keepMounted={false}
      >
        <Tabs.List mb="md" aria-label={t("notifications:page.tabs.label")}>
          <Tabs.Tab value="all">{t("notifications:page.tabs.all")}</Tabs.Tab>
          <Tabs.Tab value="unread">{t("notifications:page.tabs.unread")}</Tabs.Tab>
          <Tabs.Tab value="read">{t("notifications:page.tabs.read")}</Tabs.Tab>
        </Tabs.List>
      </Tabs>

      <Group gap="sm" align="flex-end" wrap="wrap" mb="md">
        <Select
          aria-label={t("notifications:page.filters.kind")}
          placeholder={t("notifications:page.filters.kind")}
          w={260}
          clearable
          searchable
          data={kinds}
          value={kind && !kind.includes(",") ? kind : null}
          onChange={(value) => params.setFilter("kind", value)}
        />
        <Select
          aria-label={t("notifications:page.filters.severity")}
          placeholder={t("notifications:page.filters.severity")}
          w={180}
          clearable
          data={NOTIFICATION_SEVERITIES.map((s) => ({ value: s, label: t(`notifications:severities.${s}`) }))}
          value={severity || null}
          onChange={(value) => params.setFilter("severity", value)}
        />
        {(kind || severity) && (
          <Button variant="subtle" size="sm" onClick={() => params.setFilters({ kind: null, severity: null })}>
            {t("common:clearFilters")}
          </Button>
        )}
      </Group>

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(n) => n.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={() => void refetch()}
        sort={null}
        onSort={() => undefined}
        page={params.page}
        pageSize={params.pageSize}
        totalCount={data?.totalCount}
        onPageChange={params.setPage}
        onPageSizeChange={params.setPageSize}
        emptyMessage={tab === "unread" ? t("notifications:page.emptyUnread") : t("notifications:page.empty")}
        minWidth={720}
      />
    </>
  );
}
