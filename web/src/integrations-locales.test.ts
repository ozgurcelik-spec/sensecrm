import { describe, expect, it } from "vitest";
import tr from "../public/locales/tr/integrations.json";
import en from "../public/locales/en/integrations.json";
import trNav from "../public/locales/tr/navigation.json";
import enNav from "../public/locales/en/navigation.json";
import trUsers from "../public/locales/tr/users.json";
import enUsers from "../public/locales/en/users.json";
import trSubscription from "../public/locales/tr/subscription.json";
import enSubscription from "../public/locales/en/subscription.json";
import { createTestI18n } from "@/test-utils";
import {
  API_KEY_STATUSES,
  GATED_MODULES,
  PERMISSIONS,
  WEBHOOK_DELIVERY_KINDS,
  WEBHOOK_DELIVERY_STATUSES,
  WEBHOOK_EVENT_GROUPS,
  WEBHOOK_HEALTHS,
} from "@/types";
import { REQUEST_HEADERS } from "@/lib/integrations-guide";
import i18nSource from "./i18n.ts?raw";

/** Dotted paths of every string leaf of a nested JSON object. */
function keysOf(node: unknown, prefix = ""): string[] {
  if (node === null || typeof node !== "object") return [prefix];
  return Object.entries(node as Record<string, unknown>).flatMap(([key, value]) =>
    keysOf(value, prefix ? `${prefix}.${key}` : key)
  );
}

const placeholders = (text: string) =>
  [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1] as string).sort();

function lookup(node: unknown, path: string): string {
  return path
    .split(".")
    .reduce<unknown>((acc, key) => (acc as Record<string, unknown> | undefined)?.[key], node) as string;
}

/** Every event type of the plan's catalog (the guide and the picker label them under `events.<type>`). */
const EVENT_TYPES = [
  "lead.created",
  "lead.converted",
  "account.created",
  "contact.created",
  "deal.stage_changed",
  "deal.won",
  "deal.lost",
  "quote.accepted",
  "order.created",
  "case.created",
  "case.resolved",
  "ping",
];

const FAILURE_REASONS = [
  "timeout",
  "connection_error",
  "http_error",
  "redirect",
  "blocked_destination",
  "tls_error",
  "dns_error",
  "retries_exhausted",
  "expired",
  "payload_too_large",
];

const URL_REASONS = ["required", "invalid", "scheme", "too_long", "userinfo", "ip_literal", "host", "port", "not_allow_listed"];

/** Every code of the plan's error table the integration screens word themselves. */
const ERROR_CODES = [
  "webhook.url_invalid",
  "webhook.name_taken",
  "webhook.delivery_unavailable",
  "webhook.disabled",
  "delivery.not_redeliverable",
  "api_key.scope_not_allowed",
  "api_key.name_taken",
  "api_key.revoked",
  "api_key.active",
  "api_key.expired",
  "api_key.owner_inactive",
  "api_key.ip_not_allowed",
  "api_key.not_allowed",
  "role.permission_escalation",
  "general.rate_limit_exceeded",
];

describe("integrations locale files (TR / EN parity)", () => {
  it("has exactly the same keys in Turkish and English", () => {
    expect(keysOf(en).sort()).toEqual(keysOf(tr).sort());
  });

  it("has no empty strings and the same placeholders in both languages", () => {
    for (const key of keysOf(tr)) {
      const trText = lookup(tr, key);
      const enText = lookup(en, key);
      expect(trText.trim(), `tr ${key}`).not.toBe("");
      expect(enText.trim(), `en ${key}`).not.toBe("");
      expect(placeholders(enText), key).toEqual(placeholders(trText));
    }
  });

  it("words every event type, group, health, delivery status / kind, failure reason and key status", () => {
    for (const [lng, file] of [["tr", tr], ["en", en]] as const) {
      for (const type of EVENT_TYPES) expect(lookup(file.events, type), `${lng} events.${type}`).toBeTruthy();
      for (const group of WEBHOOK_EVENT_GROUPS) expect(lookup(file.events.groups, group), `${lng} group ${group}`).toBeTruthy();
      for (const health of WEBHOOK_HEALTHS) expect(lookup(file.health, health), `${lng} ${health}`).toBeTruthy();
      for (const status of WEBHOOK_DELIVERY_STATUSES) expect(lookup(file.deliveries.statuses, status)).toBeTruthy();
      for (const kind of WEBHOOK_DELIVERY_KINDS) expect(lookup(file.deliveries.kinds, kind)).toBeTruthy();
      for (const reason of FAILURE_REASONS) expect(lookup(file.deliveries.failureReasons, reason), `${lng} ${reason}`).toBeTruthy();
      for (const status of API_KEY_STATUSES) expect(lookup(file.apiKeys.statuses, status)).toBeTruthy();
      for (const reason of URL_REASONS) expect(lookup(file.webhooks.form.urlErrors, reason), `${lng} url ${reason}`).toBeTruthy();
      for (const header of REQUEST_HEADERS) expect(lookup(file.guide.webhooks.headerDescriptions, header.key)).toBeTruthy();
    }
  });

  it("words every error code of the plan in both languages", () => {
    for (const code of ERROR_CODES) {
      expect(lookup(tr.errors, code), `tr ${code}`).toBeTruthy();
      expect(lookup(en.errors, code), `en ${code}`).toBeTruthy();
    }
  });

  it("resolves the dotted event keys and error codes through i18next", () => {
    const tri = createTestI18n("tr");
    const eni = createTestI18n("en");
    expect(tri.t("integrations:events.deal.stage_changed")).toBe("Fırsat aşaması değişti");
    expect(eni.t("integrations:events.lead.created")).toBe("Lead created");
    expect(tri.t("integrations:errors.api_key.scope_not_allowed", { scope: "org.x" })).toContain("org.x");
    expect(eni.t("integrations:errors.role.permission_escalation")).toContain("cannot give");
  });

  it("labels the navigation entry, the permission and the plan module / limits in both languages", () => {
    expect(trNav.integrations).toBe("Entegrasyonlar");
    expect(enNav.integrations).toBe("Integrations");
    expect(PERMISSIONS.orgIntegrationsManage).toBe("org.integrations.manage");
    expect(trUsers.permissions.org.integrations.manage).toBeTruthy();
    expect(enUsers.permissions.org.integrations.manage).toBeTruthy();
    expect(GATED_MODULES).toContain("integrations");
    for (const subscription of [trSubscription, enSubscription]) {
      expect(subscription.modules.integrations).toBeTruthy();
      expect(subscription.limits.webhooks).toBeTruthy();
      expect(subscription.limits.apiKeys).toBeTruthy();
    }
  });

  it("is registered as an i18n namespace (loaded by the http backend)", () => {
    expect(i18nSource).toMatch(/NAMESPACES = \[[^\]]*"integrations"/s);
  });
});
