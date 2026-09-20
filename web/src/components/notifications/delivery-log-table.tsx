import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActionIcon, Badge, Button, Card, Group, Select, SimpleGrid, Stack, Text, TextInput, Tooltip } from "@mantine/core";
import { Eye, RotateCcw } from "lucide-react";
import { DataTable, type Column } from "@/components/crm/data-table";
import { useCanChangeNotificationSettings } from "@/hooks/use-notification-access";
import { useListParams } from "@/hooks/use-list-params";
import { useDeliveries, useDeliverySummary, useRetryDeliveries } from "@/hooks/use-notifications";
import { useMembers } from "@/hooks/use-organization-queries";
import { useRowSelection } from "@/hooks/use-row-selection";
import { toast } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import { formatNumber } from "@/lib/format";
import { deliveryReasonText, kindLabel, notificationErrorMessage } from "@/lib/notifications";
import { useAuthStore } from "@/store/auth.store";
import {
  NOTIFICATION_DELIVERY_CHANNELS,
  NOTIFICATION_DELIVERY_KINDS,
  NOTIFICATION_DELIVERY_STATUSES,
  type NotificationDelivery,
  type NotificationDeliveryQuery,
} from "@/types";
import { DeliveryDrawer } from "./delivery-drawer";
import { DeliveryStatusBadge } from "./delivery-status-badge";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["channel", "status", "kind", "recipientUserId", "from", "to"] as const;

const YMD = /^\d{4}-\d{2}-\d{2}$/;
const validYmd = (value: string) => (YMD.test(value) ? value : "");

function SummaryCards({ from, to }: { from?: string; to?: string }) {
  const { t } = useTranslation(["notifications"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { data } = useDeliverySummary(from, to);
  if (!data) return null;
  return (
    <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm" data-testid="delivery-summary">
      {NOTIFICATION_DELIVERY_STATUSES.map((status) => (
        <Card key={status} withBorder padding="sm">
          <Text size="xs" c="dimmed">
            {t(`notifications:delivery.statuses.${status}`)}
          </Text>
          <Text fw={700} fz="xl" data-testid={`summary-${status}`}>
            {formatNumber(data.counts[status] ?? 0)}
          </Text>
        </Card>
      ))}
      <Card withBorder padding="sm">
        <Text size="xs" c="dimmed">
          {t("notifications:delivery.summary.sentToday")}
        </Text>
        <Text fw={700} fz="xl" data-testid="summary-sentToday">
          {formatNumber(data.sentToday)}
          {data.dailyEmailLimit !== undefined && (
            <Text span size="sm" c="dimmed" fw={400}>
              {" "}/ {formatNumber(data.dailyEmailLimit)}
            </Text>
          )}
        </Text>
        {data.oldestPendingAt && (
          <Text size="xs" c="dimmed">
            {t("notifications:delivery.summary.oldestPending", { date: formatDateTime(data.oldestPendingAt, timeZone) })}
          </Text>
        )}
      </Card>
    </SimpleGrid>
  );
}

/**
 * Delivery log (`org.notifications.manage`): summary cards, filters (channel, status, kind,
 * recipient, day range - all in the URL), the paged table and the retry of dead rows.
 */
export function DeliveryLogTable() {
  const { t } = useTranslation(["notifications", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const canRetry = useCanChangeNotificationSettings();
  const params = useListParams(FILTERS);
  const { channel, status, kind, recipientUserId } = params.filters;
  const from = validYmd(params.filters.from);
  const to = validYmd(params.filters.to);
  const [openId, setOpenId] = useState<string | null>(null);
  const members = useMembers();
  const retry = useRetryDeliveries();

  const query = useMemo<NotificationDeliveryQuery>(
    () => ({
      page: params.page,
      pageSize: params.pageSize,
      channel: channel || undefined,
      status: status || undefined,
      kind: kind || undefined,
      recipientUserId: recipientUserId || undefined,
      from: from || undefined,
      to: to || undefined,
    }),
    [params.page, params.pageSize, channel, status, kind, recipientUserId, from, to]
  );
  const { data, isLoading, isFetching, error, refetch } = useDeliveries(query);
  const selection = useRowSelection(JSON.stringify(query));
  const deadIds = useMemo(
    () => (data?.items ?? []).filter((row) => row.status === "dead" && selection.selected.has(row.id)).map((row) => row.id),
    [data, selection.selected]
  );

  const recipients = (members.data ?? [])
    .filter((member) => member.status !== "pending" && !!member.userId)
    .map((member) => ({ value: member.userId as string, label: member.displayName ?? member.email }));

  async function onRetry() {
    try {
      const result = await retry.mutateAsync(deadIds);
      selection.clear();
      toast({ variant: "success", description: t("notifications:delivery.retried", { count: result.retried }) });
    } catch (failure) {
      toast({
        variant: "destructive",
        title: t("common:errors.title"),
        description: notificationErrorMessage(failure),
      });
    }
  }

  const columns: Column<NotificationDelivery>[] = [
    {
      key: "status",
      header: t("notifications:delivery.columns.status"),
      render: (d) => (
        <Group gap={6} wrap="nowrap">
          <DeliveryStatusBadge status={d.status} />
          {d.isMandatory && (
            <Badge size="xs" variant="light" color="gray">
              {t("notifications:mandatoryBadge")}
            </Badge>
          )}
        </Group>
      ),
    },
    {
      key: "channel",
      header: t("notifications:delivery.columns.channel"),
      render: (d) => t(`notifications:channels.${d.channel}`),
    },
    { key: "kind", header: t("notifications:delivery.columns.kind"), render: (d) => kindLabel(d.kind) },
    {
      key: "recipient",
      header: t("notifications:delivery.columns.recipient"),
      render: (d) => (
        <Stack gap={0}>
          <Text size="sm">{d.recipientName ?? "-"}</Text>
          {d.addressMasked && (
            <Text size="xs" c="dimmed" ff="monospace">
              {d.addressMasked}
            </Text>
          )}
        </Stack>
      ),
    },
    { key: "attempts", header: t("notifications:delivery.columns.attempts"), render: (d) => d.attempts },
    {
      key: "reason",
      header: t("notifications:delivery.columns.reason"),
      render: (d) => <Text size="sm">{deliveryReasonText(d) ?? "-"}</Text>,
    },
    {
      key: "createdAt",
      header: t("notifications:delivery.columns.createdAt"),
      render: (d) => formatDateTime(d.createdAt, timeZone),
    },
    {
      key: "lastAttemptAt",
      header: t("notifications:delivery.columns.lastAttemptAt"),
      render: (d) => (d.lastAttemptAt ? formatDateTime(d.lastAttemptAt, timeZone) : "-"),
    },
    {
      key: "actions",
      header: "",
      width: 48,
      render: (d) => (
        <Tooltip label={t("notifications:delivery.open")}>
          <ActionIcon variant="subtle" aria-label={t("notifications:delivery.openNamed", { kind: kindLabel(d.kind) })} onClick={() => setOpenId(d.id)}>
            <Eye size={16} />
          </ActionIcon>
        </Tooltip>
      ),
    },
  ];

  return (
    <Stack gap="md">
      <SummaryCards from={from || undefined} to={to || undefined} />
      <Group gap="sm" align="flex-end" wrap="wrap">
        <Select
          aria-label={t("notifications:delivery.filters.channel")}
          placeholder={t("notifications:delivery.filters.channel")}
          w={150}
          clearable
          data={NOTIFICATION_DELIVERY_CHANNELS.map((c) => ({ value: c, label: t(`notifications:channels.${c}`) }))}
          value={channel || null}
          onChange={(value) => params.setFilter("channel", value)}
        />
        <Select
          aria-label={t("notifications:delivery.filters.status")}
          placeholder={t("notifications:delivery.filters.status")}
          w={170}
          clearable
          data={NOTIFICATION_DELIVERY_STATUSES.map((s) => ({ value: s, label: t(`notifications:delivery.statuses.${s}`) }))}
          value={status || null}
          onChange={(value) => params.setFilter("status", value)}
        />
        <Select
          aria-label={t("notifications:delivery.filters.kind")}
          placeholder={t("notifications:delivery.filters.kind")}
          w={240}
          clearable
          searchable
          data={NOTIFICATION_DELIVERY_KINDS.map((k) => ({ value: k, label: kindLabel(k) }))}
          value={kind || null}
          onChange={(value) => params.setFilter("kind", value)}
        />
        {recipients.length > 0 && (
          <Select
            aria-label={t("notifications:delivery.filters.recipient")}
            placeholder={t("notifications:delivery.filters.recipient")}
            w={220}
            clearable
            searchable
            data={recipients}
            value={recipientUserId || null}
            onChange={(value) => params.setFilter("recipientUserId", value)}
          />
        )}
        <TextInput
          type="date"
          label={t("notifications:delivery.filters.from")}
          value={from}
          onChange={(event) => params.setFilter("from", event.currentTarget.value || null)}
        />
        <TextInput
          type="date"
          label={t("notifications:delivery.filters.to")}
          value={to}
          onChange={(event) => params.setFilter("to", event.currentTarget.value || null)}
        />
        {params.hasActiveFilters && (
          <Button variant="subtle" size="sm" onClick={params.clearFilters}>
            {t("common:clearFilters")}
          </Button>
        )}
        {canRetry && (
          <Button
            ml="auto"
            leftSection={<RotateCcw size={16} />}
            disabled={deadIds.length === 0}
            loading={retry.isPending}
            onClick={() => void onRetry()}
          >
            {t("notifications:delivery.retry", { count: deadIds.length })}
          </Button>
        )}
      </Group>

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(d) => d.id}
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
        emptyMessage={t("notifications:delivery.empty")}
        minWidth={1000}
        selection={
          canRetry
            ? {
                selected: selection.selected,
                onChange: selection.onChange,
                isSelectable: (d) => d.status === "dead",
                rowLabel: (d) => t("notifications:delivery.selectNamed", { kind: kindLabel(d.kind) }),
              }
            : undefined
        }
      />

      {openId && <DeliveryDrawer id={openId} onClose={() => setOpenId(null)} />}
    </Stack>
  );
}
