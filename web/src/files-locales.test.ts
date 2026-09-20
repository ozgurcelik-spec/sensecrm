import { describe, expect, it } from "vitest";
import tr from "../public/locales/tr/files.json";
import en from "../public/locales/en/files.json";
import { ATTACHMENT_RECORD_TYPES } from "@/types";
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

/** Every code of the plan's error table that the file endpoints return and the UI words itself. */
const FILE_ERROR_CODES = [
  "file.upload_invalid",
  "file.too_many_files",
  "file.empty",
  "file.name_invalid",
  "file.extension_change_not_allowed",
  "file.too_large",
  "file.type_not_allowed",
  "file.content_mismatch",
  "file.infected",
  "file.quota_exceeded",
  "file.quarantined",
  "file.content_missing",
  "file.scan_unavailable",
  "file.storage_unavailable",
  "file.record_not_found",
  "general.rate_limit_exceeded",
];

describe("files locale files (TR / EN parity)", () => {
  it("files.json has exactly the same keys in Turkish and English", () => {
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

  it("words every file error code of the plan in both languages", () => {
    for (const code of FILE_ERROR_CODES) {
      expect(lookup(tr.errors, code), `tr ${code}`).toBeTruthy();
      expect(lookup(en.errors, code), `en ${code}`).toBeTruthy();
    }
  });

  it("names all nine record types", () => {
    for (const type of ATTACHMENT_RECORD_TYPES) {
      expect(lookup(tr.recordTypes, type), `tr ${type}`).toBeTruthy();
      expect(lookup(en.recordTypes, type), `en ${type}`).toBeTruthy();
    }
  });

  it("is registered as an i18n namespace (loaded by the http backend)", () => {
    // The module itself starts the http backend, so its source is read instead of importing it.
    expect(i18nSource).toMatch(/NAMESPACES = \[[^\]]*"files"/s);
  });
});
