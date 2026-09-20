import { describe, expect, it } from "vitest";
import trPlatform from "../public/locales/tr/platform.json";
import enPlatform from "../public/locales/en/platform.json";
import trSubscription from "../public/locales/tr/subscription.json";
import enSubscription from "../public/locales/en/subscription.json";
import trNav from "../public/locales/tr/navigation.json";
import enNav from "../public/locales/en/navigation.json";
import { GATED_MODULES, RECORD_MODULES, TENANT_STATUSES } from "@/types";
import { PLATFORM_AUDIT_ACTIONS } from "@/lib/platform";

/** Dotted paths of every string leaf of a nested JSON object. */
function keysOf(node: unknown, prefix = ""): string[] {
  if (node === null || typeof node !== "object") return [prefix];
  return Object.entries(node as Record<string, unknown>).flatMap(([key, value]) =>
    keysOf(value, prefix ? `${prefix}.${key}` : key)
  );
}

/** Leaf keys with dotted JSON keys (audit action names) kept whole: walk one level at a time. */
function lookup(node: unknown, path: string[]): string {
  return path.reduce<unknown>((acc, key) => (acc as Record<string, unknown>)[key], node) as string;
}

function leaves(node: unknown, trail: string[] = []): string[][] {
  if (node === null || typeof node !== "object") return [trail];
  return Object.entries(node as Record<string, unknown>).flatMap(([key, value]) =>
    leaves(value, [...trail, key])
  );
}

const placeholders = (text: string) =>
  [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1] as string).sort();

const FILES = [
  ["platform", trPlatform, enPlatform],
  ["subscription", trSubscription, enSubscription],
] as const;

describe.each(FILES)("%s locale files", (_name, tr, en) => {
  it("has exactly the same keys in Turkish and English", () => {
    expect(keysOf(en).sort()).toEqual(keysOf(tr).sort());
  });

  it("has no empty strings and the same interpolation placeholders in both languages", () => {
    for (const path of leaves(tr)) {
      const trText = lookup(tr, path);
      const enText = lookup(en, path);
      expect(trText.trim(), `tr ${path.join(".")}`).not.toBe("");
      expect(enText.trim(), `en ${path.join(".")}`).not.toBe("");
      expect(placeholders(enText), path.join(".")).toEqual(placeholders(trText));
    }
  });
});

describe("M7 texts cover the contract values", () => {
  it("translates every effective status, module, audit action and plan-state error code", () => {
    for (const lang of [
      [trPlatform, trSubscription],
      [enPlatform, enSubscription],
    ] as const) {
      const [platform, subscription] = lang;
      for (const status of TENANT_STATUSES) {
        expect(lookup(platform, ["status", status])).toBeTruthy();
      }
      for (const module of [...GATED_MODULES, ...RECORD_MODULES]) {
        expect(lookup(subscription, ["modules", module])).toBeTruthy();
      }
      for (const action of PLATFORM_AUDIT_ACTIONS) {
        expect(lookup(platform, ["audit", "actions", action])).toBeTruthy();
      }
      for (const reason of ["suspended", "trial_expired", "pending_deletion", "deleted"]) {
        expect(lookup(subscription, ["reasons", reason])).toBeTruthy();
      }
      expect(lookup(subscription, ["errors", "tenant", "suspended"])).toBeTruthy();
      expect(lookup(subscription, ["errors", "plan", "module_disabled"])).toBeTruthy();
      expect(lookup(subscription, ["errors", "plan", "limit_exceeded"])).toBeTruthy();
      for (const code of [
        "plan_not_found",
        "invalid_transition",
        "deletion_not_cancellable",
        "system_tenant_protected",
      ]) {
        expect(lookup(platform, ["errors", "platform", code])).toBeTruthy();
      }
    }
  });

  it("has the navigation entries in both languages", () => {
    for (const nav of [trNav, enNav] as Record<string, string>[]) {
      for (const key of ["planUsage", "platform", "platformOrganizations", "platformPlans", "platformAudit"]) {
        expect(nav[key], key).toBeTruthy();
      }
    }
  });
});
