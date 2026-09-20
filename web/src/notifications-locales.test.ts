import { describe, expect, it } from "vitest";
import tr from "../public/locales/tr/notifications.json";
import en from "../public/locales/en/notifications.json";
import trNav from "../public/locales/tr/navigation.json";
import enNav from "../public/locales/en/navigation.json";
import trUsers from "../public/locales/tr/users.json";
import enUsers from "../public/locales/en/users.json";
import { createTestI18n } from "@/test-utils";
import {
  NOTIFICATION_DELIVERY_KINDS,
  NOTIFICATION_DELIVERY_STATUSES,
  NOTIFICATION_ERROR_CODES,
  NOTIFICATION_GROUPS,
  NOTIFICATION_SEVERITIES,
  NOTIFICATION_SKIP_REASONS,
  PERMISSIONS,
} from "@/types";
import i18nSource from "./i18n.ts?raw";

/** Dotted paths of every string leaf of a nested JSON object. */
function keysOf(node: unknown, prefix = ""): string[] {
  if (node === null || typeof node !== "object") return [prefix];
  return Object.entries(node as Record<string, unknown>).flatMap(([key, value]) =>
    keysOf(value, prefix ? `${prefix}.${key}` : key)
  );
}

/** `{{placeholder}}` names of a string, sorted. */
function placeholders(text: string): string[] {
  return [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1] as string).sort();
}

function lookup(node: unknown, path: string): string {
  return path
    .split(".")
    .reduce<unknown>((acc, key) => (acc as Record<string, unknown> | undefined)?.[key], node) as string;
}

/** Every code of the plan's error table that the notification endpoints return and the UI words itself. */
const ERROR_CODES = [
  "notification.mandatory_preference",
  "notification.email_not_configured",
  "notification.delivery_not_retryable",
  "general.rate_limit_exceeded",
];

describe("notifications locale files (TR / EN parity)", () => {
  it("notifications.json has exactly the same keys in Turkish and English", () => {
    expect(keysOf(en).sort()).toEqual(keysOf(tr).sort());
  });

  it("has no empty strings (except the hidden test e-mail description) and the same placeholders in both languages", () => {
    for (const key of keysOf(tr)) {
      const trText = lookup(tr, key);
      const enText = lookup(en, key);
      if (key !== "kinds.system.test_email.description") {
        expect(trText.trim(), `tr ${key}`).not.toBe("");
        expect(enText.trim(), `en ${key}`).not.toBe("");
      }
      expect(placeholders(enText), key).toEqual(placeholders(trText));
    }
  });

  it("words every notification kind (label and description), group, severity, delivery status, error code and skip reason", () => {
    for (const kind of NOTIFICATION_DELIVERY_KINDS) {
      for (const [lng, file] of [["tr", tr], ["en", en]] as const) {
        expect(lookup(file.kinds, `${kind}.label`), `${lng} kinds.${kind}.label`).toBeTruthy();
        if (kind !== "system.test_email") expect(lookup(file.kinds, `${kind}.description`), `${lng} ${kind} description`).toBeTruthy();
      }
    }
    for (const [lng, file] of [["tr", tr], ["en", en]] as const) {
      for (const group of NOTIFICATION_GROUPS) expect(lookup(file.groups, group), `${lng} group ${group}`).toBeTruthy();
      for (const severity of NOTIFICATION_SEVERITIES) expect(lookup(file.severities, severity), `${lng} ${severity}`).toBeTruthy();
      for (const status of NOTIFICATION_DELIVERY_STATUSES) expect(lookup(file.delivery.statuses, status), `${lng} ${status}`).toBeTruthy();
      for (const code of NOTIFICATION_ERROR_CODES) expect(lookup(file.delivery.errorCodes, code), `${lng} ${code}`).toBeTruthy();
      for (const reason of NOTIFICATION_SKIP_REASONS) expect(lookup(file.delivery.skipReasons, reason), `${lng} ${reason}`).toBeTruthy();
    }
  });

  it("words every notification error code of the plan in both languages", () => {
    for (const code of ERROR_CODES) {
      expect(lookup(tr.errors, code), `tr ${code}`).toBeTruthy();
      expect(lookup(en.errors, code), `en ${code}`).toBeTruthy();
    }
  });

  it("resolves the dotted kind keys and the error codes through i18next", () => {
    const tri = createTestI18n("tr");
    const eni = createTestI18n("en");
    expect(tri.t("notifications:kinds.approval.requested.label")).toBe("Onay istendi");
    expect(eni.t("notifications:kinds.case.sla_breached.label")).toBe("SLA breached");
    expect(tri.t("notifications:errors.notification.mandatory_preference")).toBe(
      "Bu zorunlu bildirim kapatılamaz."
    );
  });

  it("labels the navigation entry and the permission in both languages", () => {
    expect(trNav.notificationsSettings).toBeTruthy();
    expect(enNav.notificationsSettings).toBeTruthy();
    expect(PERMISSIONS.orgNotificationsManage).toBe("org.notifications.manage");
    expect(trUsers.permissions.org.notifications.manage).toBeTruthy();
    expect(enUsers.permissions.org.notifications.manage).toBeTruthy();
  });

  it("is registered as an i18n namespace (loaded by the http backend)", () => {
    // The module itself starts the http backend, so its source is read instead of importing it.
    expect(i18nSource).toMatch(/NAMESPACES = \[[^\]]*"notifications"/s);
  });
});
