import { describe, expect, it } from "vitest";
import { createTestI18n } from "@/test-utils";
import { PERMISSIONS } from "@/types";
import invoicesEn from "../public/locales/en/invoices.json";
import invoicesTr from "../public/locales/tr/invoices.json";
import inventoryEn from "../public/locales/en/inventory.json";
import inventoryTr from "../public/locales/tr/inventory.json";
import commerceEn from "../public/locales/en/commerce.json";
import commerceTr from "../public/locales/tr/commerce.json";
import i18nSource from "./i18n.ts?raw";

function flatten(value: unknown, prefix = ""): string[] {
  if (value && typeof value === "object") {
    return Object.entries(value).flatMap(([key, child]) => flatten(child, prefix ? `${prefix}.${key}` : key));
  }
  return [prefix];
}

function lookup(dictionary: unknown, key: string): unknown {
  return key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], dictionary);
}

const sources = import.meta.glob<string>("/src/**/*.{ts,tsx}", { query: "?raw", import: "default", eager: true });

const NAMESPACES = {
  invoices: { tr: invoicesTr, en: invoicesEn },
  inventory: { tr: inventoryTr, en: inventoryEn },
  commerce: { tr: commerceTr, en: commerceEn },
} as const;

describe.each(["invoices", "inventory"] as const)("%s translations", (namespace) => {
  const { tr, en } = NAMESPACES[namespace];

  it("has the same keys in Turkish and English, none empty", () => {
    expect(flatten(en).sort()).toEqual(flatten(tr).sort());
    for (const dictionary of [tr, en]) {
      for (const key of flatten(dictionary)) {
        const value = lookup(dictionary, key);
        expect(typeof value === "string" && value.trim().length > 0, `${namespace}.${key}`).toBe(true);
      }
    }
  });

  it("keeps the same interpolation placeholders in both languages", () => {
    const placeholders = (text: unknown) => [...String(text).matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]).sort();
    for (const key of flatten(tr)) {
      expect(placeholders(lookup(en, key)), `${namespace}.${key}`).toEqual(placeholders(lookup(tr, key)));
    }
  });

  it("defines every statically referenced key used in the source (both languages)", () => {
    const used = new Set<string>();
    const pattern = new RegExp(`${namespace}:([A-Za-z0-9_.]+)(?=["'\`])`, "g");
    for (const [path, code] of Object.entries(sources)) {
      if (path.includes(".test.") || path.includes("/test/")) continue;
      for (const match of code.matchAll(pattern)) used.add(match[1] as string);
    }
    expect(used.size).toBeGreaterThan(20);
    for (const key of used) {
      expect(lookup(tr, key), `tr ${namespace}:${key}`).toBeTruthy();
      expect(lookup(en, key), `en ${namespace}:${key}`).toBeTruthy();
    }
  });
});

describe("M9C additions to the commerce namespace", () => {
  it("names the negotiation stage and the new document fields in both languages", () => {
    for (const dictionary of [commerceTr, commerceEn]) {
      expect(lookup(dictionary, "quoteStatuses.negotiation")).toBeTruthy();
      for (const key of ["dueDate", "customerPoNumber", "exciseTax", "salesCommission", "pending", "priceBook", "carrier"]) {
        expect(lookup(dictionary, `fields.${key}`), key).toBeTruthy();
      }
      for (const key of ["billing", "shipping", "country", "building", "street", "city", "state", "postalCode", "clearAll", "copy"]) {
        expect(lookup(dictionary, `address.${key}`), key).toBeTruthy();
      }
    }
  });
});

describe.each(["tr", "en"] as const)("permissions, navigation and error codes (%s)", (lng) => {
  const i18n = createTestI18n(lng);

  it("labels the eight new permissions", () => {
    for (const key of [
      PERMISSIONS.crmInvoicesRead,
      PERMISSIONS.crmInvoicesWrite,
      PERMISSIONS.crmPriceBooksRead,
      PERMISSIONS.crmPriceBooksWrite,
      PERMISSIONS.crmVendorsRead,
      PERMISSIONS.crmVendorsWrite,
      PERMISSIONS.crmPurchaseOrdersRead,
      PERMISSIONS.crmPurchaseOrdersWrite,
    ]) {
      expect(i18n.exists(`users:permissions.${key}`, { lng }), key).toBe(true);
    }
  });

  it("names the four navigation entries", () => {
    for (const key of ["pricebooks", "purchaseOrders", "invoices", "vendors"]) {
      expect(i18n.exists(`navigation:${key}`, { lng }), key).toBe(true);
    }
  });

  it("words every M9C error code (found by getApiErrorMessage through invoices: / inventory:)", () => {
    for (const code of [
      "order.not_invoiceable",
      "order.already_invoiced",
      "order.has_active_invoice",
      "invoice.not_editable",
      "invoice.invalid_transition",
      "invoice.no_lines",
      "invoice.has_payments",
      "invoice.not_payable",
      "invoice.payment_exceeds_balance",
    ]) {
      expect(i18n.exists(`invoices:errors.${code}`, { lng }), code).toBe(true);
    }
    for (const code of [
      "vendor.in_use",
      "pricebook.name_taken",
      "pricebook.model_immutable",
      "pricebook.model_mismatch",
      "pricebook.not_effective",
      "pricebook.entry_limit",
      "purchase_order.not_editable",
      "purchase_order.invalid_transition",
      "purchase_order.no_lines",
    ]) {
      expect(i18n.exists(`inventory:errors.${code}`, { lng }), code).toBe(true);
    }
  });
});

describe("i18n registration", () => {
  it("registers both namespaces (loaded by the http backend)", () => {
    expect(i18nSource).toMatch(/NAMESPACES = \[[^\]]*"invoices"/s);
    expect(i18nSource).toMatch(/NAMESPACES = \[[^\]]*"inventory"/s);
  });
});
