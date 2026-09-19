import { describe, expect, it } from "vitest";
import tr from "../public/locales/tr/service.json";
import en from "../public/locales/en/service.json";
import trCommon from "../public/locales/tr/common.json";
import enCommon from "../public/locales/en/common.json";
import trNav from "../public/locales/tr/navigation.json";
import enNav from "../public/locales/en/navigation.json";
import trUsers from "../public/locales/tr/users.json";
import enUsers from "../public/locales/en/users.json";

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
  return path.split(".").reduce<unknown>((acc, key) => (acc as Record<string, unknown>)[key], node) as string;
}

describe("service locale files", () => {
  it("service.json has exactly the same keys in Turkish and English", () => {
    expect(keysOf(en).sort()).toEqual(keysOf(tr).sort());
  });

  it("has no empty strings and the same interpolation placeholders in both languages", () => {
    for (const key of keysOf(tr)) {
      const trText = lookup(tr, key);
      const enText = lookup(en, key);
      expect(trText.trim(), `tr ${key}`).not.toBe("");
      expect(enText.trim(), `en ${key}`).not.toBe("");
      expect(placeholders(enText), key).toEqual(placeholders(trText));
    }
  });

  it("translates the case error codes, permissions and navigation entries in both languages", () => {
    const codes = [
      "case.account_not_found",
      "case.contact_not_found",
      "case.contact_account_mismatch",
      "case.invalid_transition",
      "case.resolution_required",
      "case.reopen_window_expired",
      "case.not_active",
      "case.closed",
      "general.concurrency_conflict",
    ];
    for (const code of codes) {
      expect(lookup(trCommon.errors, code), `tr ${code}`).toBeTruthy();
      expect(lookup(enCommon.errors, code), `en ${code}`).toBeTruthy();
    }
    for (const key of ["read", "write"]) {
      expect(lookup(trUsers.permissions.crm, `cases.${key}`)).toBeTruthy();
      expect(lookup(enUsers.permissions.crm, `cases.${key}`)).toBeTruthy();
    }
    for (const key of ["cases", "sla"]) {
      expect((trNav as Record<string, string>)[key]).toBeTruthy();
      expect((enNav as Record<string, string>)[key]).toBeTruthy();
    }
  });
});
