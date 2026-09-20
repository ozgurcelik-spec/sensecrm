import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Alert, Anchor, Badge, Button, Group, Menu, Select, Stack, Switch, Text, Tooltip } from "@mantine/core";
import { Ellipsis, KeyRound, ListChecks, Pencil, Plus, Trash2 } from "lucide-react";
import { DataTable, type Column } from "@/components/crm/data-table";
import { SearchInput } from "@/components/crm/search-input";
import { toastApiError } from "@/hooks/use-toast";
import { useIntegrationsStatus, useSetWebhookEnabled, useWebhookEvents, useWebhooks } from "@/hooks/use-integrations";
import { useListParams } from "@/hooks/use-list-params";
import { formatDateTime } from "@/lib/dates";
import { eventTypeLabel } from "@/lib/integrations";
import { useAuthStore } from "@/store/auth.store";
import type { IntegrationsStatus, WebhookListQuery, WebhookRotatedSecret, WebhookSubscription } from "@/types";
import { HealthBadge } from "./badges";
import { DeleteWebhookDialog } from "./delete-webhook-dialog";
import { DeliveryDetailDrawer } from "./delivery-detail-drawer";
import { EgressDisabledBanner } from "./egress-disabled-banner";
import { RotateSecretDialog } from "./rotate-secret-dialog";
import { SecretRevealDialog } from "./secret-reveal-dialog";
import { TestPingButton } from "./test-ping-button";
import { WebhookFormDialog } from "./webhook-form-dialog";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["enabled", "eventType"] as const;

const MAX_CHIPS = 3;

/** A secret to show once: a new subscription's, or the result of a rotation. */
type Reveal =
  | { kind: "created"; name: string; secret: string }
  | { kind: "rotated"; name: string; secret: string; graceUntil?: string };

function limitReached(status: IntegrationsStatus | undefined): boolean {
  const max = status?.limits.maxWebhooks;
  return max !== undefined && (status?.usage.webhooks ?? 0) >= max;
}

/**
 * Webhooks tab: the deployment notes (webhooks off / allow-listed hosts), the plan limit, search /
 * enabled / event type filters in the URL, the subscription table (health, last success / failure,
 * enabled switch, test ping, edit, rotate secret, delete) and the one-time secret display.
 */
export function WebhooksTab() {
  const { t } = useTranslation(["integrations", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const [, setSearchParams] = useSearchParams();
  const params = useListParams(FILTERS);
  const { enabled, eventType } = params.filters;
  const status = useIntegrationsStatus();
  const setEnabled = useSetWebhookEnabled();

  const [editing, setEditing] = useState<WebhookSubscription | "new" | null>(null);
  const [rotating, setRotating] = useState<WebhookSubscription | null>(null);
  const [deleting, setDeleting] = useState<WebhookSubscription | null>(null);
  const [reveal, setReveal] = useState<Reveal | null>(null);
  const [openDelivery, setOpenDelivery] = useState<string | null>(null);

  const query = useMemo<WebhookListQuery>(
    () => ({
      page: params.page,
      pageSize: params.pageSize,
      q: params.q || undefined,
      sort: params.query.sort,
      enabled: enabled || undefined,
      eventType: eventType || undefined,
    }),
    [params.page, params.pageSize, params.q, params.query.sort, enabled, eventType]
  );
  const { data, isLoading, isFetching, error, refetch } = useWebhooks(query);
  const webhooksEnabled = status.data?.webhooksEnabled ?? false;
  const atLimit = limitReached(status.data);
  const maxWebhooks = status.data?.limits.maxWebhooks;

  async function toggle(webhook: WebhookSubscription, next: boolean) {
    try {
      await setEnabled.mutateAsync({ id: webhook.id, enabled: next });
    } catch (failure) {
      toastApiError(failure);
    }
  }

  const events = useWebhookEvents();
  const eventOptions = (events.data ?? []).map((event) => ({ value: event.type, label: eventTypeLabel(event.type) }));

  const columns: Column<WebhookSubscription>[] = [
    {
      key: "name",
      header: t("integrations:webhooks.columns.name"),
      sortField: "name",
      render: (w) => (
        <Stack gap={0}>
          <Text size="sm" fw={500}>
            {w.name}
          </Text>
          <Text size="xs" c="dimmed" ff="monospace">
            {w.host}
          </Text>
        </Stack>
      ),
    },
    {
      key: "events",
      header: t("integrations:webhooks.columns.events"),
      render: (w) => (
        <Group gap={4} wrap="wrap">
          {w.eventTypes.slice(0, MAX_CHIPS).map((type) => (
            <Badge key={type} size="sm" variant="outline" color="gray">
              {eventTypeLabel(type)}
            </Badge>
          ))}
          {w.eventTypes.length > MAX_CHIPS && (
            <Tooltip label={w.eventTypes.slice(MAX_CHIPS).map(eventTypeLabel).join(", ")} multiline maw={300}>
              <Badge size="sm" variant="light" color="gray">
                +{w.eventTypes.length - MAX_CHIPS}
              </Badge>
            </Tooltip>
          )}
        </Group>
      ),
    },
    {
      key: "health",
      header: t("integrations:webhooks.columns.health"),
      render: (w) => (
        <Stack gap={4} align="flex-start">
          <HealthBadge health={w.health} />
          {!w.enabled && w.disabledReason === "failing" && (
            <>
              <Text size="xs" c="red" data-testid={`auto-disabled-${w.id}`}>
                {t("integrations:webhooks.autoDisabled")}
              </Text>
              <Button
                size="compact-xs"
                variant="light"
                loading={setEnabled.isPending && setEnabled.variables?.id === w.id}
                onClick={() => void toggle(w, true)}
              >
                {t("integrations:webhooks.reenable")}
              </Button>
            </>
          )}
        </Stack>
      ),
    },
    {
      key: "lastSuccess",
      header: t("integrations:webhooks.columns.lastSuccess"),
      render: (w) => (w.lastSuccessAt ? formatDateTime(w.lastSuccessAt, timeZone) : "-"),
    },
    {
      key: "lastFailure",
      header: t("integrations:webhooks.columns.lastFailure"),
      sortField: "lastFailureAt",
      render: (w) => (w.lastFailureAt ? formatDateTime(w.lastFailureAt, timeZone) : "-"),
    },
    {
      key: "enabled",
      header: t("integrations:webhooks.columns.enabled"),
      render: (w) => (
        <Switch
          aria-label={t("integrations:webhooks.enabledNamed", { name: w.name })}
          checked={w.enabled}
          disabled={setEnabled.isPending && setEnabled.variables?.id === w.id}
          onChange={(event) => void toggle(w, event.currentTarget.checked)}
        />
      ),
    },
    {
      key: "actions",
      header: "",
      width: 96,
      render: (w) => (
        <Group gap={2} wrap="nowrap">
          <TestPingButton webhook={w} webhooksEnabled={webhooksEnabled} onResult={setOpenDelivery} />
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <ActionIcon variant="subtle" aria-label={t("integrations:webhooks.actionsNamed", { name: w.name })}>
                <Ellipsis size={16} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item leftSection={<Pencil size={14} />} onClick={() => setEditing(w)}>
                {t("common:edit")}
              </Menu.Item>
              <Menu.Item leftSection={<KeyRound size={14} />} onClick={() => setRotating(w)}>
                {t("integrations:webhooks.rotate")}
              </Menu.Item>
              <Menu.Item
                leftSection={<ListChecks size={14} />}
                onClick={() => setSearchParams({ tab: "deliveries", subscriptionId: w.id })}
              >
                {t("integrations:webhooks.showDeliveries")}
              </Menu.Item>
              <Menu.Divider />
              <Menu.Item color="red" leftSection={<Trash2 size={14} />} onClick={() => setDeleting(w)}>
                {t("common:delete")}
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>
      ),
    },
  ];

  return (
    <Stack gap="md">
      <EgressDisabledBanner status={status.data} />
      {atLimit && (
        <Alert color="orange" variant="light" data-testid="webhook-limit">
          {t("integrations:webhooks.limitReached", { used: status.data?.usage.webhooks ?? 0, max: maxWebhooks ?? 0 })}{" "}
          <Anchor component={Link} to="/app/settings/plan" size="sm">
            {t("integrations:planLink")}
          </Anchor>
        </Alert>
      )}
      <Group justify="space-between" align="flex-end" wrap="wrap" gap="sm">
        <Group gap="sm" wrap="wrap">
          <SearchInput
            value={params.q}
            onSearch={params.setQ}
            placeholder={t("integrations:webhooks.searchPlaceholder")}
          />
          <Select
            aria-label={t("integrations:webhooks.filters.enabled")}
            placeholder={t("integrations:webhooks.filters.enabled")}
            w={160}
            clearable
            data={[
              { value: "true", label: t("integrations:webhooks.filters.enabledOn") },
              { value: "false", label: t("integrations:webhooks.filters.enabledOff") },
            ]}
            value={enabled || null}
            onChange={(value) => params.setFilter("enabled", value)}
          />
          <Select
            aria-label={t("integrations:webhooks.filters.eventType")}
            placeholder={t("integrations:webhooks.filters.eventType")}
            w={220}
            clearable
            searchable
            data={eventOptions}
            value={eventType || null}
            onChange={(value) => params.setFilter("eventType", value)}
          />
          {params.hasActiveFilters && (
            <Button variant="subtle" size="sm" onClick={params.clearFilters}>
              {t("common:clearFilters")}
            </Button>
          )}
        </Group>
        <Tooltip label={t("integrations:webhooks.limitTooltip")} disabled={!atLimit}>
          <Button leftSection={<Plus size={16} />} disabled={atLimit} onClick={() => setEditing("new")}>
            {t("integrations:webhooks.create")}
          </Button>
        </Tooltip>
      </Group>

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(w) => w.id}
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
        emptyMessage={t("integrations:webhooks.empty")}
        minWidth={1000}
      />

      {editing && (
        <WebhookFormDialog
          webhook={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onCreated={(created) => setReveal({ kind: "created", name: created.name, secret: created.secret })}
        />
      )}
      {rotating && (
        <RotateSecretDialog
          webhook={rotating}
          onClose={() => setRotating(null)}
          onRotated={(result: WebhookRotatedSecret) =>
            setReveal({ kind: "rotated", name: rotating.name, secret: result.secret, graceUntil: result.previousSecretExpiresAt })
          }
        />
      )}
      {deleting && <DeleteWebhookDialog webhook={deleting} onClose={() => setDeleting(null)} />}
      {openDelivery && <DeliveryDetailDrawer id={openDelivery} onClose={() => setOpenDelivery(null)} />}
      {reveal && (
        <SecretRevealDialog
          title={reveal.kind === "created" ? t("integrations:webhooks.reveal.createdTitle") : t("integrations:webhooks.reveal.rotatedTitle")}
          intro={
            reveal.kind === "created"
              ? t("integrations:webhooks.reveal.createdIntro", { name: reveal.name })
              : t("integrations:webhooks.reveal.rotatedIntro", { name: reveal.name })
          }
          label={t("integrations:webhooks.reveal.label")}
          value={reveal.secret}
          extra={
            reveal.kind === "rotated" && reveal.graceUntil ? (
              <Alert color="blue" variant="light" data-testid="grace-note">
                {t("integrations:webhooks.reveal.graceNote", { date: formatDateTime(reveal.graceUntil, timeZone) })}
              </Alert>
            ) : undefined
          }
          onClose={() => setReveal(null)}
        />
      )}
    </Stack>
  );
}
