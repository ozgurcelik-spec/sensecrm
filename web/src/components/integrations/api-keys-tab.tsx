import { useMemo, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Alert, Anchor, Badge, Button, Group, Menu, Select, Stack, Text, Tooltip } from "@mantine/core";
import { ChartColumn, Ellipsis, Pencil, Plus, Trash2, Ban } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { SearchInput } from "@/components/crm/search-input";
import { toast, toastApiError } from "@/hooks/use-toast";
import {
  useApiKeys,
  useDeleteApiKey,
  useIntegrationsStatus,
  useRevokeApiKey,
} from "@/hooks/use-integrations";
import { useListParams } from "@/hooks/use-list-params";
import { formatDateTime } from "@/lib/dates";
import { curlExample, expiryLevel } from "@/lib/integrations";
import { useAuthStore } from "@/store/auth.store";
import { API_KEY_STATUSES, type ApiKey, type ApiKeyListQuery } from "@/types";
import { ApiKeyFormDialog } from "./api-key-form-dialog";
import { ApiKeyUsageDrawer } from "./api-key-usage-drawer";
import { ApiKeyStatusBadge } from "./badges";
import { CodeBlock } from "./code-block";
import { SecretRevealDialog } from "./secret-reveal-dialog";

// Module-level: `useListParams` needs a stable array.
const FILTERS = ["status"] as const;

const MAX_SCOPE_CHIPS = 2;

/** Expiry cell: red once expired, orange within 14 days. */
function ExpiryCell({ apiKey, timeZone }: { apiKey: ApiKey; timeZone?: string }) {
  const level = apiKey.status === "revoked" ? "ok" : expiryLevel(apiKey.expiresAt);
  const color = level === "expired" ? "red" : level === "soon" ? "orange" : undefined;
  return (
    <Text size="sm" c={color} data-testid={`expiry-${apiKey.id}`} data-level={level} style={{ whiteSpace: "nowrap" }}>
      {formatDateTime(apiKey.expiresAt, timeZone)}
    </Text>
  );
}

/**
 * API keys tab: list (name, prefix, scopes, expiry colour, last use with IP, status), create with the
 * one-time key display, edit, usage chart, revoke (confirmation) and delete (only revoked / expired
 * keys, confirmation). The plan limit (`maxApiKeys`, active keys) is shown above the list.
 */
export function ApiKeysTab() {
  const { t } = useTranslation(["integrations", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const params = useListParams(FILTERS);
  const statusFilter = params.filters.status;
  const status = useIntegrationsStatus();
  const revoke = useRevokeApiKey();
  const remove = useDeleteApiKey();

  const [editing, setEditing] = useState<ApiKey | "new" | null>(null);
  const [usageOf, setUsageOf] = useState<ApiKey | null>(null);
  const [revoking, setRevoking] = useState<ApiKey | null>(null);
  const [deleting, setDeleting] = useState<ApiKey | null>(null);
  const [created, setCreated] = useState<{ name: string; key: string } | null>(null);

  const query = useMemo<ApiKeyListQuery>(
    () => ({
      page: params.page,
      pageSize: params.pageSize,
      q: params.q || undefined,
      sort: params.query.sort,
      status: statusFilter || undefined,
    }),
    [params.page, params.pageSize, params.q, params.query.sort, statusFilter]
  );
  const { data, isLoading, isFetching, error, refetch } = useApiKeys(query);
  const maxKeys = status.data?.limits.maxApiKeys;
  const atLimit = maxKeys !== undefined && (status.data?.usage.apiKeys ?? 0) >= maxKeys;

  async function onRevoke() {
    if (!revoking) return;
    try {
      await revoke.mutateAsync(revoking.id);
      toast({ variant: "success", description: t("integrations:apiKeys.revoked", { name: revoking.name }) });
      setRevoking(null);
    } catch (failure) {
      toastApiError(failure);
      setRevoking(null);
    }
  }

  async function onDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("integrations:apiKeys.deleted", { name: deleting.name }) });
      setDeleting(null);
    } catch (failure) {
      toastApiError(failure);
      setDeleting(null);
    }
  }

  const columns: Column<ApiKey>[] = [
    {
      key: "name",
      header: t("integrations:apiKeys.columns.name"),
      sortField: "name",
      render: (k) => (
        <Stack gap={0}>
          <Text size="sm" fw={500}>
            {k.name}
          </Text>
          <Text size="xs" c="dimmed" ff="monospace">
            {k.prefix}
          </Text>
        </Stack>
      ),
    },
    {
      key: "scopes",
      header: t("integrations:apiKeys.columns.scopes"),
      render: (k) => (
        <Group gap={4} wrap="wrap">
          {k.scopes.slice(0, MAX_SCOPE_CHIPS).map((scope) => (
            <Badge key={scope} size="sm" variant="outline" color="gray" ff="monospace">
              {scope}
            </Badge>
          ))}
          {k.scopes.length > MAX_SCOPE_CHIPS && (
            <Tooltip label={k.scopes.slice(MAX_SCOPE_CHIPS).join(", ")} multiline maw={320}>
              <Badge size="sm" variant="light" color="gray">
                +{k.scopes.length - MAX_SCOPE_CHIPS}
              </Badge>
            </Tooltip>
          )}
          <Text size="xs" c="dimmed">
            {t("integrations:apiKeys.scopeCount", { count: k.scopes.length })}
          </Text>
        </Group>
      ),
    },
    {
      key: "expiresAt",
      header: t("integrations:apiKeys.columns.expiresAt"),
      sortField: "expiresAt",
      render: (k) => <ExpiryCell apiKey={k} timeZone={timeZone} />,
    },
    {
      key: "lastUsed",
      header: t("integrations:apiKeys.columns.lastUsed"),
      sortField: "lastUsedAt",
      render: (k) =>
        k.lastUsedAt ? (
          <Stack gap={0}>
            <Text size="sm">{formatDateTime(k.lastUsedAt, timeZone)}</Text>
            {k.lastUsedIp && (
              <Text size="xs" c="dimmed" ff="monospace">
                {k.lastUsedIp}
              </Text>
            )}
          </Stack>
        ) : (
          <Text size="sm" c="dimmed">
            {t("integrations:apiKeys.neverUsed")}
          </Text>
        ),
    },
    {
      key: "status",
      header: t("integrations:apiKeys.columns.status"),
      render: (k) => <ApiKeyStatusBadge status={k.status} />,
    },
    {
      key: "actions",
      header: "",
      width: 48,
      render: (k) => (
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <ActionIcon variant="subtle" aria-label={t("integrations:apiKeys.actionsNamed", { name: k.name })}>
              <Ellipsis size={16} />
            </ActionIcon>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<ChartColumn size={14} />} onClick={() => setUsageOf(k)}>
              {t("integrations:apiKeys.usage.open")}
            </Menu.Item>
            {k.status !== "revoked" && (
              <Menu.Item leftSection={<Pencil size={14} />} onClick={() => setEditing(k)}>
                {t("common:edit")}
              </Menu.Item>
            )}
            {k.status === "active" && (
              <Menu.Item color="red" leftSection={<Ban size={14} />} onClick={() => setRevoking(k)}>
                {t("integrations:apiKeys.revoke")}
              </Menu.Item>
            )}
            {k.status !== "active" && (
              <Menu.Item color="red" leftSection={<Trash2 size={14} />} onClick={() => setDeleting(k)}>
                {t("common:delete")}
              </Menu.Item>
            )}
          </Menu.Dropdown>
        </Menu>
      ),
    },
  ];

  return (
    <Stack gap="md">
      <Alert color="blue" variant="light">
        {t("integrations:apiKeys.intro")}
      </Alert>
      {atLimit && (
        <Alert color="orange" variant="light" data-testid="apikey-limit">
          {t("integrations:apiKeys.limitReached", { used: status.data?.usage.apiKeys ?? 0, max: maxKeys ?? 0 })}{" "}
          <Anchor component={Link} to="/app/settings/plan" size="sm">
            {t("integrations:planLink")}
          </Anchor>
        </Alert>
      )}
      <Group justify="space-between" align="flex-end" wrap="wrap" gap="sm">
        <Group gap="sm" wrap="wrap">
          <SearchInput value={params.q} onSearch={params.setQ} placeholder={t("integrations:apiKeys.searchPlaceholder")} />
          <Select
            aria-label={t("integrations:apiKeys.filters.status")}
            placeholder={t("integrations:apiKeys.filters.status")}
            w={160}
            clearable
            data={API_KEY_STATUSES.map((s) => ({ value: s, label: t(`integrations:apiKeys.statuses.${s}`) }))}
            value={statusFilter || null}
            onChange={(value) => params.setFilter("status", value)}
          />
          {params.hasActiveFilters && (
            <Button variant="subtle" size="sm" onClick={params.clearFilters}>
              {t("common:clearFilters")}
            </Button>
          )}
        </Group>
        <Tooltip label={t("integrations:apiKeys.limitTooltip")} disabled={!atLimit}>
          <Button leftSection={<Plus size={16} />} disabled={atLimit} onClick={() => setEditing("new")}>
            {t("integrations:apiKeys.create")}
          </Button>
        </Tooltip>
      </Group>

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(k) => k.id}
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
        emptyMessage={t("integrations:apiKeys.empty")}
        minWidth={980}
      />

      {editing && (
        <ApiKeyFormDialog
          apiKey={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onCreated={(key) => setCreated({ name: key.name, key: key.key })}
        />
      )}
      {usageOf && <ApiKeyUsageDrawer apiKey={usageOf} onClose={() => setUsageOf(null)} />}
      <ConfirmDialog
        opened={!!revoking}
        title={t("integrations:apiKeys.revokeTitle", { name: revoking?.name ?? "" })}
        message={t("integrations:apiKeys.revokeMessage")}
        confirmLabel={t("integrations:apiKeys.revoke")}
        destructive
        loading={revoke.isPending}
        onConfirm={() => void onRevoke()}
        onClose={() => setRevoking(null)}
      />
      <ConfirmDialog
        opened={!!deleting}
        title={t("integrations:apiKeys.deleteTitle", { name: deleting?.name ?? "" })}
        message={t("integrations:apiKeys.deleteMessage")}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void onDelete()}
        onClose={() => setDeleting(null)}
      />
      {created && (
        <SecretRevealDialog
          title={t("integrations:apiKeys.reveal.title")}
          intro={t("integrations:apiKeys.reveal.intro", { name: created.name })}
          label={t("integrations:apiKeys.reveal.label")}
          value={created.key}
          extra={<CodeBlock code={curlExample(created.key)} label={t("integrations:apiKeys.reveal.curl")} testId="reveal-curl" />}
          onClose={() => setCreated(null)}
        />
      )}
    </Stack>
  );
}
