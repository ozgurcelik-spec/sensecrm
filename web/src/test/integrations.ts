/** Fixtures for the Milestone 8B tests (webhooks, delivery log, API keys, developer guide). */
import type {
  ApiKey,
  IntegrationsStatus,
  WebhookDelivery,
  WebhookDeliveryDetail,
  WebhookEventInfo,
  WebhookSubscription,
} from "@/types";

export function webhook(id: string, overrides: Partial<WebhookSubscription> = {}): WebhookSubscription {
  return {
    id,
    name: `Webhook ${id}`,
    url: `https://hooks.acme.com.tr/${id}`,
    host: "hooks.acme.com.tr",
    eventTypes: ["lead.created", "deal.won"],
    enabled: true,
    secretHint: "…a1b2",
    secretVersion: 1,
    consecutiveFailures: 0,
    health: "healthy",
    lastSuccessAt: "2026-09-20T08:00:00Z",
    createdAt: "2026-09-01T10:00:00Z",
    createdByUserId: "user-1",
    createdByName: "Ada Lovelace",
    ...overrides,
  };
}

export function integrationsStatus(overrides: Partial<IntegrationsStatus> = {}): IntegrationsStatus {
  return {
    webhooksEnabled: true,
    restrictedHosts: false,
    maxAttempts: 8,
    timeoutSeconds: 10,
    signatureToleranceSeconds: 300,
    deliveryRetentionDays: 30,
    apiKeys: { maxLifetimeDays: 730, defaultLifetimeDays: 365, rateLimitPerMinute: 120 },
    limits: {},
    usage: { webhooks: 1, apiKeys: 1 },
    ...overrides,
  };
}

export function eventInfo(type: string, group: WebhookEventInfo["group"], overrides: Partial<WebhookEventInfo> = {}): WebhookEventInfo {
  return {
    type,
    version: 1,
    group,
    module: group === "sales" ? "sales" : group,
    available: true,
    deprecated: false,
    sample: { id: "0192f0a1-0000-7000-8000-000000000001", type, version: 1, data: { sampleId: "x" } },
    ...overrides,
  };
}

/** A slice of the catalog: two sales types, a commerce type the plan lacks and a service type. */
export const EVENTS: WebhookEventInfo[] = [
  eventInfo("lead.created", "sales"),
  eventInfo("deal.won", "sales"),
  eventInfo("quote.accepted", "commerce", { available: false }),
  eventInfo("case.created", "service"),
];

export function delivery(id: string, overrides: Partial<WebhookDelivery> = {}): WebhookDelivery {
  return {
    id,
    subscriptionId: "w1",
    subscriptionName: "ERP köprüsü",
    eventId: "0192f0a1-0000-7000-8000-00000000e001",
    eventType: "lead.created",
    kind: "event",
    status: "succeeded",
    attempts: 1,
    maxAttempts: 8,
    completedAt: "2026-09-20T09:00:05Z",
    lastAttemptAt: "2026-09-20T09:00:05Z",
    responseStatus: 200,
    durationMs: 120,
    host: "hooks.acme.com.tr",
    createdAt: "2026-09-20T09:00:00Z",
    ...overrides,
  };
}

export function deliveryDetail(id: string, overrides: Partial<WebhookDeliveryDetail> = {}): WebhookDeliveryDetail {
  return {
    ...delivery(id),
    payload: { id: "0192f0a1-0000-7000-8000-00000000e001", type: "lead.created", version: 1, data: { leadId: "l1" } },
    requestHeaders: {
      "Content-Type": "application/json; charset=utf-8",
      "X-Crm-Event-Type": "lead.created",
      "X-Crm-Signature": "[redacted]",
    },
    attemptLog: [
      { attemptNo: 1, startedAt: "2026-09-20T09:00:04Z", durationMs: 120, responseStatus: 200, responseSnippet: "ok" },
    ],
    ...overrides,
  };
}

export function apiKey(id: string, overrides: Partial<ApiKey> = {}): ApiKey {
  return {
    id,
    name: `Anahtar ${id}`,
    prefix: `crmk_a1b2c3d${id.slice(-1)}`,
    scopes: ["crm.leads.read", "crm.accounts.read"],
    expiresAt: "2027-09-20T00:00:00Z",
    allowedCidrs: [],
    status: "active",
    createdAt: "2026-09-01T10:00:00Z",
    createdByUserId: "user-1",
    createdByName: "Ada Lovelace",
    ...overrides,
  };
}
