import { describe, expect, it, vi } from "vitest";
import { ATTACHMENT_RECORD_TYPES, PERMISSIONS } from "@/types";
import { FILE_LIMITS, makeFile, makeFileOfSize } from "@/test/files";
import {
  ATTACHMENT_PERMISSIONS,
  acceptAttribute,
  baseNameLength,
  extensionOf,
  fileIconKind,
  formatBytes,
  isBytesMetric,
  mbToBytes,
  previewableType,
  sameExtension,
  validateClientFile,
} from "./files";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

describe("record type -> permission mapping (plan table)", () => {
  // The binding table of docs/plan/m8c-dosya-ekleri.md ("Kayıt türleri ve izin eşlemesi").
  const PLAN_TABLE: Record<string, [string, string]> = {
    account: ["crm.accounts.read", "crm.accounts.write"],
    contact: ["crm.contacts.read", "crm.contacts.write"],
    lead: ["crm.leads.read", "crm.leads.write"],
    deal: ["crm.deals.read", "crm.deals.write"],
    activity: ["crm.activities.read", "crm.activities.write"],
    case: ["crm.cases.read", "crm.cases.write"],
    quote: ["crm.quotes.read", "crm.quotes.write"],
    order: ["crm.orders.read", "crm.orders.write"],
    campaign: ["crm.campaigns.read", "crm.campaigns.write"],
  };

  it("covers exactly the nine record types of the plan", () => {
    expect([...ATTACHMENT_RECORD_TYPES].sort()).toEqual(Object.keys(PLAN_TABLE).sort());
    expect(Object.keys(ATTACHMENT_PERMISSIONS).sort()).toEqual(Object.keys(PLAN_TABLE).sort());
  });

  it.each(Object.entries(PLAN_TABLE))("%s uses %s / %s", (type, [read, write]) => {
    const entry = ATTACHMENT_PERMISSIONS[type as keyof typeof ATTACHMENT_PERMISSIONS];
    expect(entry).toEqual({ read, write });
  });

  it("only uses permission keys the app knows (no new crm.files.* permission exists)", () => {
    const known = new Set<string>(Object.values(PERMISSIONS));
    for (const { read, write } of Object.values(ATTACHMENT_PERMISSIONS)) {
      expect(known.has(read), read).toBe(true);
      expect(known.has(write), write).toBe(true);
    }
    expect([...known].some((key) => key.startsWith("crm.files."))).toBe(false);
  });
});

describe("formatBytes", () => {
  it("uses binary units with one decimal (Turkish separators by default)", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1023)).toBe("1.023 B");
    expect(formatBytes(1024)).toBe("1 KB");
    expect(formatBytes(184223)).toBe("179,9 KB");
    expect(formatBytes(26214400)).toBe("25 MB");
    expect(formatBytes(3221225472)).toBe("3 GB");
    expect(formatBytes(mbToBytes(1024))).toBe("1 GB");
  });

  it("is a dash for invalid input", () => {
    expect(formatBytes(-1)).toBe("-");
    expect(formatBytes(Number.NaN)).toBe("-");
  });
});

describe("names and types", () => {
  it("reads the last extension in lower case", () => {
    expect(extensionOf("Rapor.PDF")).toBe("pdf");
    expect(extensionOf("a.tar.gz")).toBe("gz");
    expect(extensionOf("noext")).toBe("");
    expect(extensionOf(".hidden")).toBe("");
    expect(extensionOf("trailing.")).toBe("");
  });

  it("compares the extension of a rename and finds where the base name ends", () => {
    expect(sameExtension("a.pdf", "b.PDF")).toBe(true);
    expect(sameExtension("a.pdf", "b.png")).toBe(false);
    expect(sameExtension("a.pdf", "b")).toBe(false);
    expect(baseNameLength("Sözleşme.pdf")).toBe("Sözleşme".length);
    expect(baseNameLength("noext")).toBe(5);
  });

  it("maps extensions to icon kinds", () => {
    expect(fileIconKind("pdf")).toBe("pdf");
    expect(fileIconKind("PNG")).toBe("image");
    expect(fileIconKind("xlsx")).toBe("sheet");
    expect(fileIconKind("csv")).toBe("sheet");
    expect(fileIconKind("docx")).toBe("document");
    expect(fileIconKind("zip")).toBe("other");
  });

  it("knows byte metrics", () => {
    expect(isBytesMetric("files.storage_bytes")).toBe(true);
    expect(isBytesMetric("sales.records")).toBe(false);
  });
});

describe("preview allow-list", () => {
  it("accepts only image/png|jpeg|gif|webp and application/pdf (parameters and case ignored)", () => {
    for (const type of ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf"]) {
      expect(previewableType(type)).toBe(type);
    }
    expect(previewableType("Application/PDF; charset=binary")).toBe("application/pdf");
  });

  it("rejects everything else, in particular active content", () => {
    for (const type of [
      "image/svg+xml",
      "text/html",
      "application/octet-stream",
      "text/plain",
      "application/javascript",
      "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      "",
      undefined,
      null,
    ]) {
      expect(previewableType(type), String(type)).toBeUndefined();
    }
  });
});

describe("validateClientFile (pre-checks mirror the server limits)", () => {
  it("accepts a file within the limits", () => {
    expect(validateClientFile(makeFile("a.pdf", 10), FILE_LIMITS)).toBeNull();
    expect(validateClientFile(makeFileOfSize("A.PNG", FILE_LIMITS.maxFileBytes), FILE_LIMITS)).toBeNull();
  });

  it("rejects an empty file even before the limits are known", () => {
    expect(validateClientFile(makeFileOfSize("a.pdf", 0), undefined)).toEqual({ code: "file.empty" });
    expect(validateClientFile(new File([], "a.pdf"), FILE_LIMITS)).toEqual({ code: "file.empty" });
  });

  it("rejects one byte over the limit", () => {
    expect(validateClientFile(makeFileOfSize("a.pdf", FILE_LIMITS.maxFileBytes + 1), FILE_LIMITS)).toEqual({
      code: "file.too_large",
      args: { maxBytes: FILE_LIMITS.maxFileBytes },
    });
  });

  it("rejects extensions that are not allowed, and names without one", () => {
    expect(validateClientFile(makeFile("run.exe"), FILE_LIMITS)).toEqual({
      code: "file.type_not_allowed",
      args: { extension: "exe" },
    });
    expect(validateClientFile(makeFile("README"), FILE_LIMITS)?.code).toBe("file.type_not_allowed");
  });

  it("does not reject anything but emptiness without limits (the server decides)", () => {
    expect(validateClientFile(makeFile("run.exe"), undefined)).toBeNull();
  });

  it("builds the accept attribute from the allowed extensions", () => {
    expect(acceptAttribute(FILE_LIMITS)).toBe(".pdf,.png,.jpg,.jpeg,.gif,.webp,.docx,.xlsx,.txt,.csv");
    expect(acceptAttribute(undefined)).toBeUndefined();
  });
});
