/** Integrations (Milestone 8B) - `/integrations/**`: webhook subscriptions, delivery log, API keys, OpenAPI. */
import { apiClient } from "@/lib/api-client";
import type {
  ApiKey,
  ApiKeyCreated,
  ApiKeyInput,
  ApiKeyListQuery,
  ApiKeyPatch,
  ApiKeyUsageDay,
  IntegrationsStatus,
  ListResult,
  WebhookDelivery,
  WebhookDeliveryAccepted,
  WebhookDeliveryDetail,
  WebhookDeliveryQuery,
  WebhookEventInfo,
  WebhookInput,
  WebhookListQuery,
  WebhookRotatedSecret,
  WebhookSubscription,
  WebhookSubscriptionCreated,
} from "@/types";
import { cleanParams, getArray, getList, getOne, seg } from "./crm-http";

const BASE = "/integrations";

export const integrationKeys = {
  all: ["integrations"] as const,
  status: ["integrations", "status"] as const,
  events: ["integrations", "events"] as const,
  webhooks: ["integrations", "webhooks"] as const,
  webhookList: (query: WebhookListQuery) => ["integrations", "webhooks", "list", query] as const,
  webhookOptions: ["integrations", "webhooks", "options"] as const,
  deliveries: ["integrations", "deliveries"] as const,
  deliveryList: (query: WebhookDeliveryQuery) => ["integrations", "deliveries", "list", query] as const,
  delivery: (id: string) => ["integrations", "deliveries", "detail", id] as const,
  apiKeys: ["integrations", "api-keys"] as const,
  apiKeyList: (query: ApiKeyListQuery) => ["integrations", "api-keys", "list", query] as const,
  apiKeyUsage: (id: string, from: string, to: string) =>
    ["integrations", "api-keys", "usage", id, from, to] as const,
};

// ---- Status and catalog ----------------------------------------------------------------------------

export const getIntegrationsStatus = (): Promise<IntegrationsStatus> =>
  getOne<IntegrationsStatus>(`${BASE}/status`);

export const listWebhookEvents = (): Promise<WebhookEventInfo[]> =>
  getArray<WebhookEventInfo>(`${BASE}/webhook-events`);

/**
 * `GET /integrations/openapi.json` (needs the Bearer header, so it is fetched, not linked). The
 * parsed document is serialised again for the download.
 */
export async function downloadOpenApiDocument(): Promise<Blob> {
  const { data } = await apiClient.get<unknown>(`${BASE}/openapi.json`);
  const text = typeof data === "string" ? data : JSON.stringify(data, null, 2);
  return new Blob([text], { type: "application/json" });
}

// ---- Webhook subscriptions -------------------------------------------------------------------------

export const listWebhooks = (query: WebhookListQuery): Promise<ListResult<WebhookSubscription>> =>
  getList<WebhookSubscription>(`${BASE}/webhooks`, { ...query });

/** 201 + the raw `secret` (only here, `Cache-Control: no-store`). */
export async function createWebhook(input: WebhookInput): Promise<WebhookSubscriptionCreated> {
  const { data } = await apiClient.post<WebhookSubscriptionCreated>(`${BASE}/webhooks`, input);
  return data;
}

/** Full replacement (204). */
export async function updateWebhook(id: string, input: WebhookInput): Promise<void> {
  await apiClient.put(`${BASE}/webhooks/${seg(id)}`, input);
}

export async function setWebhookEnabled(id: string, enabled: boolean): Promise<void> {
  await apiClient.post(`${BASE}/webhooks/${seg(id)}/${enabled ? "enable" : "disable"}`);
}

export async function deleteWebhook(id: string): Promise<void> {
  await apiClient.delete(`${BASE}/webhooks/${seg(id)}`);
}

export async function rotateWebhookSecret(id: string, graceHours: number): Promise<WebhookRotatedSecret> {
  const { data } = await apiClient.post<WebhookRotatedSecret>(`${BASE}/webhooks/${seg(id)}/rotate-secret`, {
    graceHours,
  });
  return data;
}

/** 202: the worker delivers a `ping` within a few seconds. */
export async function testWebhook(id: string): Promise<WebhookDeliveryAccepted> {
  const { data } = await apiClient.post<WebhookDeliveryAccepted>(`${BASE}/webhooks/${seg(id)}/test`);
  return data;
}

// ---- Delivery log ----------------------------------------------------------------------------------

export const listWebhookDeliveries = (query: WebhookDeliveryQuery): Promise<ListResult<WebhookDelivery>> =>
  getList<WebhookDelivery>(`${BASE}/deliveries`, { ...query });

export const getWebhookDelivery = (id: string): Promise<WebhookDeliveryDetail> =>
  getOne<WebhookDeliveryDetail>(`${BASE}/deliveries/${seg(id)}`);

/** 202: a new `redelivery` row with the same event id and payload. */
export async function redeliverWebhookDelivery(id: string): Promise<WebhookDeliveryAccepted> {
  const { data } = await apiClient.post<WebhookDeliveryAccepted>(`${BASE}/deliveries/${seg(id)}/redeliver`);
  return data;
}

// ---- API keys --------------------------------------------------------------------------------------

export const listApiKeys = (query: ApiKeyListQuery): Promise<ListResult<ApiKey>> =>
  getList<ApiKey>(`${BASE}/api-keys`, { ...query });

/** 201 + the raw `key` (only here, `Cache-Control: no-store`). */
export async function createApiKey(input: ApiKeyInput): Promise<ApiKeyCreated> {
  const { data } = await apiClient.post<ApiKeyCreated>(`${BASE}/api-keys`, input);
  return data;
}

export async function updateApiKey(id: string, patch: ApiKeyPatch): Promise<void> {
  await apiClient.patch(`${BASE}/api-keys/${seg(id)}`, patch);
}

export async function revokeApiKey(id: string): Promise<void> {
  await apiClient.post(`${BASE}/api-keys/${seg(id)}/revoke`);
}

export async function deleteApiKey(id: string): Promise<void> {
  await apiClient.delete(`${BASE}/api-keys/${seg(id)}`);
}

export async function getApiKeyUsage(id: string, from: string, to: string): Promise<ApiKeyUsageDay[]> {
  const { data } = await apiClient.get<{ items: ApiKeyUsageDay[] }>(`${BASE}/api-keys/${seg(id)}/usage`, {
    params: cleanParams({ from, to }),
  });
  return data.items;
}
