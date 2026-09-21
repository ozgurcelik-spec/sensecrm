import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActionIcon, Button, Chip, Group, Select, Stack, Text, TextInput, Tooltip } from "@mantine/core";
import { Eye, RotateCcw } from "lucide-react";
import { DataTable, type Column } from "@/components/crm/data-table";
import { useListParams, formatSort } from "@/hooks/use-list-params";
import { useWebhookDeliveries, useWebhookEvents, useWebhookOptions } from "@/hooks/use-integrations";
import { formatDateTime } from "@/lib/dates";
import { eventTypeLabel, failureReasonText } from "@/lib/integrations";
import { useAuthStore } from "@/store/auth.store";
import {
  WEBHOOK_DELIVERY_KINDS,
  WEBHOOK_DELIVERY_STATUSES,
  type WebhookDelivery,
  type WebhookDeliveryQuery,
} from "@/types";
import { DeliveryStatusBadge } from "./badges";
import { DeliveryDetailDrawer } from "./delivery-detail-drawer";
import { RedeliverConfirm } from "./redeliver-confirm";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["subscriptionId", "status", "eventType", "eventId", "kind", "from", "to"] as const;

const YMD = /^\d{4}-\d{2}-\d{2}$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_RANGE_DAYS = 90;
const DAY_MS = 86_400_000;

const validYmd = (value: string) => (YMD.test(value) ? value : "");

/** Inclusive number of days between two `YYYY-MM-DD` strings. */
function rangeDays(from: string, to: string): number {
  return Math.round((Date.parse(to) - Date.parse(from)) / DAY_MS) + 1;
}

/**
 * Delivery log: subscription, status chips, event type, event id, kind and day-range filters (all
 * in the URL), the paged table with status badges and attempt counts, the detail drawer and the
 * resend of finished deliveries. Rows never show the receiver's URL beyond its host.
 */
export function DeliveryLog() {
  const { t } = useTranslation(["integrations", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const params = useListParams(FILTERS);
  const { subscriptionId, status, eventType, eventId, kind } = params.filters;
  const from = validYmd(params.filters.from);
  const to = validYmd(params.filters.to);
  const validEventId = GUID.test(eventId.trim()) ? eventId.trim() : "";
  const rangeInvalid = !!from && !!to && (rangeDays(from, to) < 1 || rangeDays(from, to) > MAX_RANGE_DAYS);
  const [openId, setOpenId] = useState<string | null>(null);
  const [redeliverId, setRedeliverId] = useState<string | undefined>();
  const webhooks = useWebhookOptions();
  const events = useWebhookEvents();

  const query = useMemo<WebhookDeliveryQuery>(
    () => ({
      page: params.page,
      pageSize: params.pageSize,
      sort: formatSort(params.sort),
      subscriptionId: subscriptionId || undefined,
      status: status || undefined,
      eventType: eventType || undefined,
      eventId: validEventId || undefined,
      kind: kind || undefined,
      from: from || undefined,
      to: to || undefined,
    }),
    [params.page, params.pageSize, params.sort, subscriptionId, status, eventType, validEventId, kind, from, to]
  );
  const { data, isLoading, isFetching, error, refetch } = useWebhookDeliveries(query, !rangeInvalid);

  const selectedStatuses = WEBHOOK_DELIVERY_STATUSES.filter((s) => status.split(",").includes(s));
  const eventOptions = (events.data ?? []).map((event) => ({ value: event.type, label: eventTypeLabel(event.type) }));
  // Pings are not catalog types but do show up in the log.
  if (!eventOptions.some((option) => option.value === "ping")) {
    eventOptions.push({ value: "ping", label: eventTypeLabel("ping") });
  }

  const columns: Column<WebhookDelivery>[] = [
    {
      key: "createdAt",
      header: t("integrations:deliveries.columns.createdAt"),
      sortField: "createdAt",
      render: (d) => <Text size="sm" style={{ whiteSpace: "nowrap" }}>{formatDateTime(d.createdAt, timeZone)}</Text>,
    },
    {
      key: "event",
      header: t("integrations:deliveries.columns.event"),
      render: (d) => (
        <Stack gap={0}>
          <Text size="sm">{eventTypeLabel(d.eventType)}</Text>
          <Text size="xs" c="dimmed" ff="monospace">
            {d.eventId}
          </Text>
        </Stack>
      ),
    },
    {
      key: "subscription",
      header: t("integrations:deliveries.columns.subscription"),
      render: (d) => (
        <Stack gap={0}>
          <Text size="sm">{d.subscriptionName}</Text>
          <Text size="xs" c="dimmed">
            {d.host}
          </Text>
        </Stack>
      ),
    },
    {
      key: "status",
      header: t("integrations:deliveries.columns.status"),
      sortField: "status",
      render: (d) => (
        <Stack gap={2}>
          <Group gap={6} wrap="nowrap">
            <DeliveryStatusBadge status={d.status} />
            {d.kind !== "event" && (
              <Text size="xs" c="dimmed">
                {t(`integrations:deliveries.kinds.${d.kind}`, { defaultValue: d.kind })}
              </Text>
            )}
          </Group>
          {d.failureReason && (
            <Text size="xs" c="red">
              {failureReasonText(d.failureReason)}
            </Text>
          )}
        </Stack>
      ),
    },
    {
      key: "attempts",
      header: t("integrations:deliveries.columns.attempts"),
      sortField: "attempts",
      render: (d) => `${d.attempts}/${d.maxAttempts}`,
    },
    { key: "http", header: t("integrations:deliveries.columns.http"), render: (d) => d.responseStatus ?? "-" },
    {
      key: "duration",
      header: t("integrations:deliveries.columns.duration"),
      render: (d) => (d.durationMs !== undefined ? `${d.durationMs} ms` : "-"),
    },
    {
      key: "nextAttempt",
      header: t("integrations:deliveries.columns.nextAttempt"),
      render: (d) => (d.nextAttemptAt ? formatDateTime(d.nextAttemptAt, timeZone) : "-"),
    },
    {
      key: "actions",
      header: "",
      width: 88,
      render: (d) => (
        <Group gap={2} wrap="nowrap">
          <Tooltip label={t("integrations:deliveries.open")}>
            <ActionIcon
              variant="subtle"
              aria-label={t("integrations:deliveries.openNamed", { event: eventTypeLabel(d.eventType) })}
              onClick={() => setOpenId(d.id)}
            >
              <Eye size={16} />
            </ActionIcon>
          </Tooltip>
          {d.status === "failed" && (
            <Tooltip label={t("integrations:deliveries.redeliver")}>
              <ActionIcon
                variant="subtle"
                aria-label={t("integrations:deliveries.redeliverNamed", { event: eventTypeLabel(d.eventType) })}
                onClick={() => setRedeliverId(d.id)}
              >
                <RotateCcw size={16} />
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
      ),
    },
  ];

  return (
    <Stack gap="md">
      <Group gap="sm" align="flex-end" wrap="wrap">
        <Select
          aria-label={t("integrations:deliveries.filters.subscription")}
          placeholder={t("integrations:deliveries.filters.subscription")}
          w={220}
          clearable
          searchable
          data={(webhooks.data?.items ?? []).map((webhook) => ({ value: webhook.id, label: webhook.name }))}
          value={subscriptionId || null}
          onChange={(value) => params.setFilter("subscriptionId", value)}
        />
        <Select
          aria-label={t("integrations:deliveries.filters.eventType")}
          placeholder={t("integrations:deliveries.filters.eventType")}
          w={220}
          clearable
          searchable
          data={eventOptions}
          value={eventType || null}
          onChange={(value) => params.setFilter("eventType", value)}
        />
        <Select
          aria-label={t("integrations:deliveries.filters.kind")}
          placeholder={t("integrations:deliveries.filters.kind")}
          w={150}
          clearable
          data={WEBHOOK_DELIVERY_KINDS.map((k) => ({ value: k, label: t(`integrations:deliveries.kinds.${k}`) }))}
          value={kind || null}
          onChange={(value) => params.setFilter("kind", value)}
        />
        <TextInput
          aria-label={t("integrations:deliveries.filters.eventId")}
          placeholder={t("integrations:deliveries.filters.eventId")}
          w={300}
          value={eventId}
          error={eventId.trim() && !validEventId ? t("integrations:deliveries.filters.eventIdInvalid") : undefined}
          onChange={(event) => params.setFilter("eventId", event.currentTarget.value.trim() || null)}
        />
        <TextInput
          type="date"
          label={t("integrations:deliveries.filters.from")}
          value={from}
          onChange={(event) => params.setFilter("from", event.currentTarget.value || null)}
        />
        <TextInput
          type="date"
          label={t("integrations:deliveries.filters.to")}
          value={to}
          error={rangeInvalid ? t("integrations:deliveries.filters.rangeInvalid", { days: MAX_RANGE_DAYS }) : undefined}
          onChange={(event) => params.setFilter("to", event.currentTarget.value || null)}
        />
        {params.hasActiveFilters && (
          <Button variant="subtle" size="sm" onClick={params.clearFilters}>
            {t("common:clearFilters")}
          </Button>
        )}
      </Group>
      <Chip.Group
        multiple
        value={selectedStatuses}
        onChange={(values) =>
          params.setFilter("status", WEBHOOK_DELIVERY_STATUSES.filter((s) => values.includes(s)).join(",") || null)
        }
      >
        <Group gap="xs" role="group" aria-label={t("integrations:deliveries.filters.status")}>
          {WEBHOOK_DELIVERY_STATUSES.map((s) => (
            <Chip key={s} value={s} size="xs" variant="outline">
              {t(`integrations:deliveries.statuses.${s}`)}
            </Chip>
          ))}
        </Group>
      </Chip.Group>

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(d) => d.id}
        isLoading={isLoading}
        isFetching={isFetching}
        error={error}
        onRetry={() => void refetch()}
        sort={params.sort}
        onSort={params.toggleSort}
        page={params.page}
        pageSize={params.pageSize}
        totalCount={data?.totalCount}
        onPageChange={params.setPage}
        onPageSizeChange={params.setPageSize}
        emptyMessage={t("integrations:deliveries.empty")}
        minWidth={1100}
      />

      {openId && <DeliveryDetailDrawer id={openId} onClose={() => setOpenId(null)} />}
      <RedeliverConfirm deliveryId={redeliverId} onClose={() => setRedeliverId(undefined)} />
    </Stack>
  );
}
