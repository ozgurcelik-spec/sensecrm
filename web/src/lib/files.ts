/**
 * Pure helpers of the file attachments feature (M8C): record type -> permission mapping (the table of
 * the plan, one source of truth for the UI), byte formatting, icons, the preview allow-list and the
 * client side pre-checks. The pre-checks mirror the server limits to save a round trip; they are
 * never trusted - the server validates every upload again.
 */
import { intlLocale } from "@/lib/dates";
import { PERMISSIONS, type AttachmentRecordType, type FileLimits } from "@/types";

/**
 * Permission that lets the user read (list, meta, download, preview) and write (upload, rename,
 * delete) the attachments of each record type: always the record's own permission (plan D3).
 */
export const ATTACHMENT_PERMISSIONS: Record<AttachmentRecordType, { read: string; write: string }> = {
  account: { read: PERMISSIONS.crmAccountsRead, write: PERMISSIONS.crmAccountsWrite },
  contact: { read: PERMISSIONS.crmContactsRead, write: PERMISSIONS.crmContactsWrite },
  lead: { read: PERMISSIONS.crmLeadsRead, write: PERMISSIONS.crmLeadsWrite },
  deal: { read: PERMISSIONS.crmDealsRead, write: PERMISSIONS.crmDealsWrite },
  activity: { read: PERMISSIONS.crmActivitiesRead, write: PERMISSIONS.crmActivitiesWrite },
  case: { read: PERMISSIONS.crmCasesRead, write: PERMISSIONS.crmCasesWrite },
  quote: { read: PERMISSIONS.crmQuotesRead, write: PERMISSIONS.crmQuotesWrite },
  order: { read: PERMISSIONS.crmOrdersRead, write: PERMISSIONS.crmOrdersWrite },
  campaign: { read: PERMISSIONS.crmCampaignsRead, write: PERMISSIONS.crmCampaignsWrite },
};

/** Rows per page of the attachments list. */
export const FILES_PAGE_SIZE = 20;
/** Uploads that run at the same time (the rest waits in the queue). */
export const MAX_CONCURRENT_UPLOADS = 3;
/** Uploads of large files take a while: the default 30 s axios timeout is replaced by 5 minutes. */
export const UPLOAD_TIMEOUT_MS = 5 * 60_000;

const UNITS = ["B", "KB", "MB", "GB", "TB"] as const;

/** Binary size for people: `184223` -> "179,9 KB" (UI language separators; whole bytes below 1 KB). */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "-";
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const text = new Intl.NumberFormat(intlLocale(), {
    maximumFractionDigits: unit === 0 ? 0 : 1,
  }).format(value);
  return `${text} ${UNITS[unit]}`;
}

/** Usage metric keys (`files.storage_bytes`) whose values are bytes. */
export function isBytesMetric(key: string): boolean {
  return key.endsWith("_bytes");
}

/** MB (storage plan limit) -> bytes. */
export function mbToBytes(mb: number): number {
  return mb * 1024 * 1024;
}

/** Lower case extension without the dot ("" when there is none or the name only starts with a dot). */
export function extensionOf(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot <= 0 || dot === name.length - 1 ? "" : name.slice(dot + 1).toLowerCase();
}

export type FileIconKind = "pdf" | "image" | "document" | "sheet" | "presentation" | "text" | "other";

const ICON_KINDS: Record<string, FileIconKind> = {
  pdf: "pdf",
  jpg: "image",
  jpeg: "image",
  png: "image",
  gif: "image",
  webp: "image",
  doc: "document",
  docx: "document",
  odt: "document",
  xls: "sheet",
  xlsx: "sheet",
  ods: "sheet",
  csv: "sheet",
  ppt: "presentation",
  pptx: "presentation",
  txt: "text",
};

export function fileIconKind(extension: string): FileIconKind {
  return ICON_KINDS[extension.toLowerCase()] ?? "other";
}

/** Types the browser may show inline (blob URL in an `<img>` / `<iframe>`); the second client side lock next to the server's. */
export const PREVIEWABLE_TYPES = [
  "image/png",
  "image/jpeg",
  "image/gif",
  "image/webp",
  "application/pdf",
] as const;
export type PreviewableType = (typeof PREVIEWABLE_TYPES)[number];

/** The allow-listed media type of a blob type (parameters and case ignored), or undefined. */
export function previewableType(blobType: string | undefined | null): PreviewableType | undefined {
  const base = (blobType ?? "").split(";")[0]?.trim().toLowerCase();
  return (PREVIEWABLE_TYPES as readonly string[]).includes(base ?? "")
    ? (base as PreviewableType)
    : undefined;
}

/** A client side rejection: the same `code` / `args` shape as a server ProblemDetails. */
export interface FileRejection {
  code: string;
  args?: Record<string, unknown>;
}

/**
 * Pre-check of a picked file against `GET /files/limits`: empty, too large, extension not allowed.
 * Without limits (still loading / failed) nothing is rejected here - the server decides.
 */
export function validateClientFile(file: File, limits: FileLimits | undefined): FileRejection | null {
  if (file.size === 0) return { code: "file.empty" };
  if (!limits) return null;
  if (file.size > limits.maxFileBytes) {
    return { code: "file.too_large", args: { maxBytes: limits.maxFileBytes } };
  }
  const extension = extensionOf(file.name);
  if (!extension || !limits.allowedExtensions.some((e) => e.toLowerCase() === extension)) {
    return { code: "file.type_not_allowed", args: { extension } };
  }
  return null;
}

/** `accept` attribute of the file input (".pdf,.png"); empty without limits. */
export function acceptAttribute(limits: FileLimits | undefined): string | undefined {
  if (!limits || limits.allowedExtensions.length === 0) return undefined;
  return limits.allowedExtensions.map((e) => `.${e.toLowerCase()}`).join(",");
}

/**
 * Renaming keeps the extension: true when `next` ends with the same (case-insensitive) extension as
 * `current`. The server answers 400 `file.extension_change_not_allowed` otherwise.
 */
export function sameExtension(current: string, next: string): boolean {
  return extensionOf(current) === extensionOf(next.trim());
}

/** Length in characters of the base name (without the extension) - where the rename input selects. */
export function baseNameLength(name: string): number {
  const extension = extensionOf(name);
  return extension ? name.length - extension.length - 1 : name.length;
}
