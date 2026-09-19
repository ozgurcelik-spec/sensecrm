import { describe, expect, it } from "vitest";
import { createTestI18n } from "@/test-utils";
import { PERMISSIONS } from "@/types";
import tr from "../../public/locales/tr/commerce.json";
import en from "../../public/locales/en/commerce.json";

function flatten(value: unknown, prefix = ""): string[] {
  if (value && typeof value === "object") {
    return Object.entries(value).flatMap(([key, child]) => flatten(child, prefix ? `${prefix}.${key}` : key));
  }
  return [prefix];
}

const sources = import.meta.glob<string>("/src/**/*.{ts,tsx}", {
  query: "?raw",
  import: "default",
  eager: true,
});

describe("commerce translations", () => {
  it("has the same keys in Turkish and English, none empty", () => {
    expect(flatten(en).sort()).toEqual(flatten(tr).sort());
    for (const dictionary of [tr, en]) {
      for (const key of flatten(dictionary)) {
        const value = key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)[part], dictionary);
        expect(typeof value === "string" && value.trim().length > 0, key).toBe(true);
      }
    }
  });

  it("defines every statically referenced commerce: key used in the source (both languages)", () => {
    const used = new Set<string>();
    for (const [path, code] of Object.entries(sources)) {
      if (path.includes(".test.") || path.includes("/test/")) continue;
      for (const match of code.matchAll(/commerce:([A-Za-z0-9_.]+)(?=["'`])/g)) used.add(match[1] as string);
    }
    expect(used.size).toBeGreaterThan(50);
    const trKeys = new Set(flatten(tr));
    const enKeys = new Set(flatten(en));
    const missingTr = [...used].filter((key) => !trKeys.has(key));
    const missingEn = [...used].filter((key) => !enKeys.has(key));
    expect(missingTr).toEqual([]);
    expect(missingEn).toEqual([]);
  });

  it("has every status label", () => {
    for (const dictionary of [tr, en]) {
      expect(Object.keys(dictionary.quoteStatuses).sort()).toEqual(
        ["accepted", "draft", "expired", "rejected", "sent"]
      );
      expect(Object.keys(dictionary.orderStatuses).sort()).toEqual(
        ["cancelled", "confirmed", "draft", "fulfilled"]
      );
    }
  });

  it.each(["tr", "en"] as const)("labels the six permissions, navigation entries and error codes (%s)", (lng) => {
    const i18n = createTestI18n(lng);
    for (const key of [
      PERMISSIONS.crmProductsRead,
      PERMISSIONS.crmProductsWrite,
      PERMISSIONS.crmQuotesRead,
      PERMISSIONS.crmQuotesWrite,
      PERMISSIONS.crmOrdersRead,
      PERMISSIONS.crmOrdersWrite,
    ]) {
      expect(i18n.exists(`users:permissions.${key}`, { lng }), key).toBe(true);
    }
    for (const key of ["products", "quotes", "orders"]) {
      expect(i18n.exists(`navigation:${key}`, { lng }), key).toBe(true);
    }
    for (const code of [
      "commerce.related_not_found",
      "commerce.contact_account_mismatch",
      "commerce.deal_account_mismatch",
      "commerce.concurrent_update",
      "product.code_taken",
      "quote.not_editable",
      "quote.invalid_transition",
      "quote.expired",
      "quote.no_lines",
      "quote.not_accepted",
      "quote.already_converted",
      "order.not_editable",
      "order.invalid_transition",
      "order.no_lines",
    ]) {
      expect(i18n.exists(`common:errors.${code}`, { lng }), code).toBe(true);
    }
  });
});
