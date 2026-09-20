/**
 * Small pure helpers of the integration screens (M8B): webhook URL pre-validation (the syntactic
 * SSRF rules of the plan, the server stays authoritative), CIDR checks, API key scope rules, expiry
 * math, badge colours and the friendly text of the `webhook.*` / `api_key.*` / `delivery.*` codes.
 */
import i18n from "@/i18n";
import { getApiProblem } from "@/lib/api-error";
import { isModuleOn, isPermissionEffective, permissionModule } from "@/lib/entitlements";
import {
  PERMISSIONS,
  type ApiKeyStatus,
  type Me,
  type WebhookDeliveryStatus,
  type WebhookHealth,
} from "@/types";

export const WEBHOOK_NAME_MAX = 100;
export const WEBHOOK_DESCRIPTION_MAX = 500;
export const WEBHOOK_URL_MAX = 2048;
export const API_KEY_NAME_MAX = 100;
export const API_KEY_DESCRIPTION_MAX = 500;
/** `Integrations:ApiKeys:MaxCidrsPerKey` (server default). */
export const API_KEY_MAX_CIDRS = 10;
export const ROTATE_GRACE_MIN = 0;
export const ROTATE_GRACE_MAX = 168;
export const ROTATE_GRACE_DEFAULT = 24;
/** Test ping: the result is looked up every 2 s for at most 15 s. */
export const PING_POLL_MS = 2_000;
export const PING_TIMEOUT_MS = 15_000;
/** Keys expiring within this many days are shown in orange. */
export const API_KEY_EXPIRY_WARN_DAYS = 14;
export const API_KEY_EXPIRY_PRESETS = [30, 90, 365] as const;

/** Code of the delivery / limit answers the screens word specially. */
export const WEBHOOK_URL_INVALID = "webhook.url_invalid";
export const WEBHOOK_NAME_TAKEN = "webhook.name_taken";
export const API_KEY_NAME_TAKEN = "api_key.name_taken";
export const API_KEY_SCOPE_NOT_ALLOWED = "api_key.scope_not_allowed";
export const ROLE_PERMISSION_ESCALATION = "role.permission_escalation";

// ---- Status helpers --------------------------------------------------------------------------------

export const DELIVERY_FINAL_STATUSES: readonly WebhookDeliveryStatus[] = ["succeeded", "failed"];

/** Only finished deliveries can be sent again (`pending` / `delivering` answer `409 delivery.not_redeliverable`). */
export function canRedeliver(status: WebhookDeliveryStatus): boolean {
  return DELIVERY_FINAL_STATUSES.includes(status);
}

export const HEALTH_COLOR: Record<WebhookHealth, string> = {
  healthy: "green",
  degraded: "yellow",
  failing: "red",
  disabled: "gray",
};

export const DELIVERY_STATUS_COLOR: Record<WebhookDeliveryStatus, string> = {
  pending: "gray",
  delivering: "blue",
  succeeded: "green",
  failed: "red",
};

export const API_KEY_STATUS_COLOR: Record<ApiKeyStatus, string> = {
  active: "green",
  expired: "orange",
  revoked: "gray",
};

export function eventTypeLabel(type: string): string {
  return i18n.t(`integrations:events.${type}`, { defaultValue: type });
}

/** Text of a delivery's `failureReason` (`timeout`, `blocked_destination`, `retries_exhausted`, ...); unknown reasons show as they are. */
export function failureReasonText(reason: string | undefined): string | undefined {
  if (!reason) return undefined;
  return i18n.t(`integrations:deliveries.failureReasons.${reason}`, { defaultValue: reason });
}

/** The server cuts the stored response at 2 KiB: a snippet of that size was very likely cut. */
export const RESPONSE_SNIPPET_LIMIT = 2048;
export function isSnippetTruncated(snippet: string): boolean {
  return snippet.length >= RESPONSE_SNIPPET_LIMIT;
}

// ---- Webhook URL -----------------------------------------------------------------------------------

/** Client side reasons; the server's `args.reason` adds `port` and `not_allow_listed`. */
export type WebhookUrlReason = "required" | "invalid" | "scheme" | "too_long" | "userinfo" | "ip_literal" | "host";

const IPV4_LITERAL = /^\d{1,3}(\.\d{1,3}){3}$/;
const BLOCKED_HOST_SUFFIXES = [".localhost", ".local", ".internal", ".lan"];

/**
 * Pre-validation of a webhook URL with the syntactic rules of the plan (HTTPS only, at most 2048
 * characters, no user info, no IP address host, no `localhost` / `.local` / `.internal` / `.lan` /
 * single-label host). The `URL` parser already turns decimal / octal / hexadecimal IPv4 spellings
 * into dotted form, so those are caught as IP literals. Ports and the operator allow-list are only
 * known to the server (`webhook.url_invalid` with `args.reason`). Returns undefined when it looks fine.
 */
export function validateWebhookUrl(raw: string): WebhookUrlReason | undefined {
  const value = raw.trim();
  if (!value) return "required";
  if (value.length > WEBHOOK_URL_MAX) return "too_long";
  if (!/^https:\/\//i.test(value)) return "scheme";
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return "invalid";
  }
  if (url.username || url.password) return "userinfo";
  const host = url.hostname.toLowerCase().replace(/\.$/, "");
  if (!host) return "invalid";
  if (host.startsWith("[") || IPV4_LITERAL.test(host)) return "ip_literal";
  if (host === "localhost" || BLOCKED_HOST_SUFFIXES.some((suffix) => host.endsWith(suffix)) || !host.includes(".")) {
    return "host";
  }
  return undefined;
}

export function webhookUrlErrorText(reason: string | undefined): string {
  return i18n.t(`integrations:webhooks.form.urlErrors.${reason ?? "invalid"}`, {
    defaultValue: i18n.t("integrations:webhooks.form.urlErrors.invalid"),
  });
}

export type WebhookFormField = "name" | "url" | "eventTypes" | "description";
const WEBHOOK_FIELDS: readonly WebhookFormField[] = ["name", "url", "eventTypes", "description"];

/**
 * Server errors of the webhook form as field messages: `webhook.url_invalid` (with `args.reason`) on
 * the URL, `webhook.name_taken` on the name and the `validation` `errors` by field name. An empty
 * result means "nothing to show on a field" (the caller toasts the error instead).
 */
export function webhookServerFieldErrors(error: unknown): Partial<Record<WebhookFormField, string>> {
  const problem = getApiProblem(error);
  const result: Partial<Record<WebhookFormField, string>> = {};
  if (!problem) return result;
  if (problem.code === WEBHOOK_URL_INVALID) {
    const reason = typeof problem.args?.reason === "string" ? problem.args.reason : undefined;
    result.url = webhookUrlErrorText(reason);
  } else if (problem.code === WEBHOOK_NAME_TAKEN) {
    result.name = i18n.t("integrations:errors.webhook.name_taken");
  }
  for (const [path, messages] of Object.entries(problem.errors ?? {})) {
    const lowered = path.charAt(0).toLowerCase() + path.slice(1);
    const field = WEBHOOK_FIELDS.find((f) => f === lowered);
    if (field && messages[0] && !result[field]) result[field] = messages[0];
  }
  return result;
}

// ---- CIDR ------------------------------------------------------------------------------------------

function isIpv4(value: string): boolean {
  if (!IPV4_LITERAL.test(value)) return false;
  return value.split(".").every((part) => Number(part) <= 255 && String(Number(part)) === part);
}

function isIpv6(value: string): boolean {
  if (!/^[0-9a-f:.]+$/i.test(value) || !value.includes(":")) return false;
  const halves = value.split("::");
  if (halves.length > 2) return false;
  const groups = (part: string | undefined) => (part ? part.split(":") : []);
  const all = [...groups(halves[0]), ...groups(halves[1])];
  let count = 0;
  for (const [index, group] of all.entries()) {
    if (group.includes(".")) {
      if (index !== all.length - 1 || !isIpv4(group)) return false;
      count += 2;
    } else if (/^[0-9a-f]{1,4}$/i.test(group)) {
      count += 1;
    } else {
      return false;
    }
  }
  return halves.length === 2 ? count < 8 : count === 8;
}

export type CidrProblem = "invalid" | "any";

/** `invalid`: not an `address/prefix` IPv4 / IPv6 CIDR. `any`: `0.0.0.0/0` or `::/0` (the server refuses them too). */
export function cidrProblem(value: string): CidrProblem | undefined {
  const [address, prefix, ...rest] = value.trim().split("/");
  if (!address || prefix === undefined || rest.length > 0 || !/^\d{1,3}$/.test(prefix)) return "invalid";
  const bits = Number(prefix);
  if (isIpv4(address)) {
    if (bits > 32) return "invalid";
  } else if (isIpv6(address)) {
    if (bits > 128) return "invalid";
  } else {
    return "invalid";
  }
  return bits === 0 ? "any" : undefined;
}

/** Splits the CIDR textarea (one per line, commas also work) into trimmed non-empty entries. */
export function parseCidrLines(text: string): string[] {
  return text
    .split(/[\s,;]+/)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

/** First problem of a CIDR list as an i18n key suffix (`apikeys.form.cidrErrors.<key>`), or undefined. */
export function cidrListProblem(entries: readonly string[]): { key: "tooMany" | CidrProblem; value?: string } | undefined {
  if (entries.length > API_KEY_MAX_CIDRS) return { key: "tooMany" };
  for (const entry of entries) {
    const problem = cidrProblem(entry);
    if (problem) return { key: problem, value: entry };
  }
  return undefined;
}

// ---- API key scopes --------------------------------------------------------------------------------

/** Never grantable to a key: approval decisions stay with people. `org.*` (administration) is excluded as a whole. */
export const API_KEY_EXCLUDED_SCOPES: readonly string[] = ["crm.approvals.decide"];

export function isApiKeyScopeAllowed(key: string): boolean {
  return key.startsWith("crm.") && !API_KEY_EXCLUDED_SCOPES.includes(key);
}

const KNOWN_SCOPES = Object.values(PERMISSIONS).filter(isApiKeyScopeAllowed);

/**
 * Scopes the picker lists: every `crm.*` permission of the app plus the ones the caller holds
 * (new permissions), minus `crm.approvals.decide`, and never the permissions of a module the plan
 * switches off. `org.*` never appears.
 */
export function apiKeyScopeOptions(me: Me | null | undefined): string[] {
  const keys = new Set<string>(KNOWN_SCOPES);
  for (const key of me?.permissions ?? []) if (isApiKeyScopeAllowed(key)) keys.add(key);
  return [...keys]
    .filter((key) => {
      const module = permissionModule(key);
      return !module || isModuleOn(me?.subscription, module);
    })
    .sort(compareScopes);
}

/** Scopes the caller may hand out: the creator's own effective permissions (the server checks again: `role.permission_escalation`). */
export function ownedScopes(me: Me | null | undefined): Set<string> {
  return new Set(apiKeyScopeOptions(me).filter((key) => isPermissionEffective(me, key)));
}

function compareScopes(a: string, b: string): number {
  const [, resourceA = "", actionA = ""] = a.split(".");
  const [, resourceB = "", actionB = ""] = b.split(".");
  if (resourceA !== resourceB) return resourceA.localeCompare(resourceB);
  // read before write
  return actionA === actionB ? 0 : actionA === "read" ? -1 : actionB === "read" ? 1 : actionA.localeCompare(actionB);
}

export interface ScopeGroup {
  /** `leads`, `accounts`, ... */
  resource: string;
  scopes: string[];
}

export function groupScopes(keys: readonly string[]): ScopeGroup[] {
  const groups: ScopeGroup[] = [];
  for (const key of keys) {
    const resource = key.split(".")[1] ?? key;
    const group = groups.find((entry) => entry.resource === resource);
    if (group) group.scopes.push(key);
    else groups.push({ resource, scopes: [key] });
  }
  return groups;
}

export function isReadScope(key: string): boolean {
  return key.endsWith(".read");
}

// ---- Expiry ----------------------------------------------------------------------------------------

const DAY_MS = 86_400_000;

export type ExpiryLevel = "expired" | "soon" | "ok";

/** Orange within 14 days of the expiry, red once it has passed. */
export function expiryLevel(expiresAt: string, now: number = Date.now()): ExpiryLevel {
  const at = new Date(expiresAt).getTime();
  if (Number.isNaN(at)) return "ok";
  if (at <= now) return "expired";
  return at - now <= API_KEY_EXPIRY_WARN_DAYS * DAY_MS ? "soon" : "ok";
}

/** `YYYY-MM-DD` of an instant in UTC. */
export function utcDay(value: number | Date): string {
  return new Date(value).toISOString().slice(0, 10);
}

/** The instant `days` from now. */
export function expiryFromDays(days: number, now: number = Date.now()): string {
  return new Date(now + days * DAY_MS).toISOString();
}

/** A picked calendar day as the last second of that UTC day (`2027-01-31` -> `2027-01-31T23:59:59.000Z`). */
export function endOfUtcDay(day: string): string {
  return `${day}T23:59:59.000Z`;
}

/** Inclusive UTC day range of the last `days` days (today included), for the usage chart. */
export function usageRange(days: number, now: number = Date.now()): { from: string; to: string } {
  return { from: utcDay(now - (days - 1) * DAY_MS), to: utcDay(now) };
}

// ---- API key form errors ---------------------------------------------------------------------------

export type ApiKeyFormField = "name" | "description" | "scopes" | "expiresAt" | "allowedCidrs";
const API_KEY_FIELDS: readonly ApiKeyFormField[] = ["name", "description", "scopes", "expiresAt", "allowedCidrs"];

/**
 * Server errors of the API key form as field messages: `api_key.scope_not_allowed` (names the scope)
 * and `role.permission_escalation` land on the scope picker, `api_key.name_taken` on the name and
 * the `validation` `errors` by field. An empty result means the caller toasts the error instead
 * (`402 plan.limit_exceeded`, `429`, ...).
 */
export function apiKeyServerFieldErrors(error: unknown): Partial<Record<ApiKeyFormField, string>> {
  const problem = getApiProblem(error);
  const result: Partial<Record<ApiKeyFormField, string>> = {};
  if (!problem) return result;
  if (problem.code === API_KEY_SCOPE_NOT_ALLOWED) {
    const scope = typeof problem.args?.scope === "string" ? problem.args.scope : "";
    result.scopes = i18n.t("integrations:errors.api_key.scope_not_allowed", { scope });
  } else if (problem.code === ROLE_PERMISSION_ESCALATION) {
    result.scopes = i18n.t("integrations:errors.role.permission_escalation");
  } else if (problem.code === API_KEY_NAME_TAKEN) {
    result.name = i18n.t("integrations:errors.api_key.name_taken");
  }
  for (const [path, messages] of Object.entries(problem.errors ?? {})) {
    const lowered = path.charAt(0).toLowerCase() + path.slice(1);
    const field = API_KEY_FIELDS.find((f) => f === lowered);
    if (field && messages[0] && !result[field]) result[field] = messages[0];
  }
  return result;
}

// ---- Examples --------------------------------------------------------------------------------------

/** Absolute base URL of the API (`/api/v1` resolved against this origin unless a full URL is configured). */
export function apiBaseUrl(): string {
  const configured = (import.meta.env.VITE_API_BASE_URL as string | undefined) || "/api/v1";
  return configured.startsWith("/") ? `${window.location.origin}${configured}` : configured;
}

/** A first call with a key (`GET /leads`); the caller decides whether `key` is the real one or a placeholder. */
export function curlExample(key: string, baseUrl: string = apiBaseUrl()): string {
  return `curl -H "Authorization: Bearer ${key}" \\\n  "${baseUrl}/leads?page=1&pageSize=25"`;
}
