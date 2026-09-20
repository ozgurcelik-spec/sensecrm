/**
 * Milestone 8B API types (outgoing webhooks, API keys, delivery log, OpenAPI) - see
 * docs/plan/m8b-entegrasyonlar.md ("HTTP kontratı"). JSON is camelCase, null fields are absent,
 * timestamps are ISO 8601 UTC and "day" fields are `YYYY-MM-DD`.
 */

/** `disabled`: switched off. `failing`: 5+ failed deliveries in a row. `degraded`: a recent failure after the last success. */
export const WEBHOOK_HEALTHS = ["healthy", "degraded", "failing", "disabled"] as const;
export type WebhookHealth = (typeof WEBHOOK_HEALTHS)[number];

/** Why a subscription is off: by hand, or automatically after too many failed deliveries. */
export type WebhookDisabledReason = "manual" | "failing";

export interface WebhookSubscription {
  id: string;
  name: string;
  url: string;
  host: string;
  eventTypes: string[];
  enabled: boolean;
  disabledReason?: WebhookDisabledReason;
  description?: string;
  /** `…` + the last 4 characters of the secret; the raw secret is never returned again. */
  secretHint: string;
  secretVersion: number;
  previousSecretExpiresAt?: string;
  consecutiveFailures: number;
  health: WebhookHealth;
  lastSuccessAt?: string;
  lastFailureAt?: string;
  createdAt: string;
  updatedAt?: string;
  createdByUserId: string;
  createdByName?: string;
}

/** `POST /integrations/webhooks` answer: the subscription plus the raw `secret`, shown exactly once. */
export interface WebhookSubscriptionCreated extends WebhookSubscription {
  secret: string;
}

/** `POST .../rotate-secret` answer: the new raw secret is shown exactly once. */
export interface WebhookRotatedSecret {
  secret: string;
  secretHint: string;
  secretVersion: number;
  previousSecretExpiresAt?: string;
}

/** Create body (`enabled` defaults to true on the server) and full-replacement update body. */
export interface WebhookInput {
  name: string;
  url: string;
  eventTypes: string[];
  description?: string;
  enabled?: boolean;
}

export interface WebhookListQuery {
  page?: number;
  pageSize?: number;
  q?: string;
  sort?: string;
  /** `true` / `false`. */
  enabled?: string;
  eventType?: string;
}

export type WebhookEventGroup = "sales" | "commerce" | "service";
export const WEBHOOK_EVENT_GROUPS: readonly WebhookEventGroup[] = ["sales", "commerce", "service"];

/** One row of `GET /integrations/webhook-events` (`ping` is not part of it). */
export interface WebhookEventInfo {
  type: string;
  version: number;
  group: WebhookEventGroup;
  module: string;
  /** The source module is on in the tenant's plan. */
  available: boolean;
  deprecated: boolean;
  sunsetOn?: string;
  description?: string;
  /** Example envelope. */
  sample: Record<string, unknown>;
}

export interface IntegrationsStatus {
  /** Deployment flag AND a valid egress setup: `false` = no webhook is sent from this installation. */
  webhooksEnabled: boolean;
  /** An operator allow-list narrows the destination hosts. */
  restrictedHosts: boolean;
  maxAttempts: number;
  timeoutSeconds: number;
  signatureToleranceSeconds: number;
  deliveryRetentionDays: number;
  apiKeys: { maxLifetimeDays: number; defaultLifetimeDays: number; rateLimitPerMinute: number };
  /** Only finite limits are present. */
  limits: { maxWebhooks?: number; maxApiKeys?: number };
  usage: { webhooks: number; apiKeys: number };
}

export const WEBHOOK_DELIVERY_STATUSES = ["pending", "delivering", "succeeded", "failed"] as const;
export type WebhookDeliveryStatus = (typeof WEBHOOK_DELIVERY_STATUSES)[number];

export const WEBHOOK_DELIVERY_KINDS = ["event", "ping", "redelivery"] as const;
export type WebhookDeliveryKind = (typeof WEBHOOK_DELIVERY_KINDS)[number];

export interface WebhookDelivery {
  id: string;
  subscriptionId: string;
  subscriptionName: string;
  eventId: string;
  eventType: string;
  kind: WebhookDeliveryKind;
  status: WebhookDeliveryStatus;
  attempts: number;
  maxAttempts: number;
  nextAttemptAt?: string;
  lastAttemptAt?: string;
  completedAt?: string;
  responseStatus?: number;
  durationMs?: number;
  failureReason?: string;
  host: string;
  createdAt: string;
}

export interface WebhookDeliveryAttempt {
  attemptNo: number;
  startedAt: string;
  durationMs?: number;
  responseStatus?: number;
  failureReason?: string;
  errorDetail?: string;
  /** First 2 KiB of the answer, sanitised by the server. */
  responseSnippet?: string;
}

export interface WebhookDeliveryDetail extends WebhookDelivery {
  /** The envelope that was signed and sent. */
  payload: Record<string, unknown>;
  /** Sent headers; `X-Crm-Signature` is `[redacted]`. */
  requestHeaders: Record<string, string>;
  attemptLog: WebhookDeliveryAttempt[];
}

export interface WebhookDeliveryQuery {
  page?: number;
  pageSize?: number;
  sort?: string;
  subscriptionId?: string;
  /** Comma separated statuses. */
  status?: string;
  eventType?: string;
  eventId?: string;
  kind?: string;
  /** UTC day, inclusive. */
  from?: string;
  to?: string;
}

/** 202 answer of test ping and redelivery: the queued delivery. */
export interface WebhookDeliveryAccepted {
  deliveryId: string;
}

export const API_KEY_STATUSES = ["active", "expired", "revoked"] as const;
export type ApiKeyStatus = (typeof API_KEY_STATUSES)[number];

export interface ApiKey {
  id: string;
  name: string;
  description?: string;
  /** `crmk_a1b2c3d4`: the only visible part of a key. */
  prefix: string;
  scopes: string[];
  expiresAt: string;
  allowedCidrs: string[];
  status: ApiKeyStatus;
  createdAt: string;
  createdByUserId: string;
  createdByName?: string;
  lastUsedAt?: string;
  lastUsedIp?: string;
  revokedAt?: string;
  revokedByName?: string;
}

/** `POST /integrations/api-keys` answer: the key plus the raw `key`, shown exactly once. */
export interface ApiKeyCreated extends ApiKey {
  key: string;
}

export interface ApiKeyInput {
  name: string;
  scopes: string[];
  /** ISO instant; the server defaults to its default lifetime when omitted. */
  expiresAt?: string;
  allowedCidrs?: string[];
  description?: string;
}

/** Scope and expiry cannot be changed; `description: null` clears the description. */
export interface ApiKeyPatch {
  name?: string;
  description?: string | null;
  allowedCidrs?: string[];
}

export interface ApiKeyListQuery {
  page?: number;
  pageSize?: number;
  q?: string;
  sort?: string;
  status?: string;
}

export interface ApiKeyUsageDay {
  day: string;
  requests: number;
  errors: number;
  throttled: number;
}
