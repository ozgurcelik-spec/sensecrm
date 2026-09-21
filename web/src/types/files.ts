/**
 * File attachments (M8C, `docs/plan/m8c-dosya-ekleri.md`): `/files` DTOs. JSON is camelCase; `null`
 * fields are absent. Exported names are prefixed (`FileAttachment`, `AttachmentRecordType`) so they
 * never clash with the DOM `File` or the record types of other modules.
 */

/** Record types that can carry attachments (`recordType` on the wire). */
export const ATTACHMENT_RECORD_TYPES = [
  "account",
  "contact",
  "lead",
  "deal",
  "activity",
  "case",
  "quote",
  "order",
  "campaign",
] as const;
export type AttachmentRecordType = (typeof ATTACHMENT_RECORD_TYPES)[number];

/** `deleted` is never listed. `quarantined`: infected, cannot be downloaded. `missing`: object lost, cannot be downloaded. */
export type FileAttachmentState = "ready" | "quarantined" | "missing";

export interface FileAttachment {
  id: string;
  recordType: AttachmentRecordType;
  recordId: string;
  name: string;
  /** Lower case, without the dot. */
  extension: string;
  /** Canonical type detected by the server (never the client's claim). */
  contentType: string;
  sizeBytes: number;
  sha256: string;
  state: FileAttachmentState;
  uploadedByUserId: string;
  /** Absent when the member cannot be resolved. */
  uploadedByName?: string;
  uploadedAt: string;
  /** Only when renamed. */
  updatedAt?: string;
  /** Previewable type and `ready`. */
  canPreview: boolean;
}

/** One rejected file of a partially successful upload (HTTP 200 `failed[]`). */
export interface FileUploadFailure {
  fileName: string;
  code: string;
  args?: Record<string, unknown>;
}

export interface FileUploadResult {
  items: FileAttachment[];
  failed: FileUploadFailure[];
}

export interface FileListQuery {
  recordType: AttachmentRecordType;
  recordId: string;
  q?: string;
  sort?: string;
  page?: number;
  pageSize?: number;
}

/** `GET /files/limits`: tenant independent configuration used for client side pre-checks (the server is authoritative). */
export interface FileLimits {
  maxFileBytes: number;
  maxFilesPerRequest: number;
  allowedExtensions: string[];
  previewableExtensions: string[];
}

export interface FilesUsageByType {
  recordType: string;
  fileCount: number;
  sizeBytes: number;
}

/** `GET /files/usage` (`org.settings.manage`): live, uncached. */
export interface FilesUsage {
  usedBytes: number;
  fileCount: number;
  /** Only with a finite limit. */
  maxBytes?: number;
  quarantinedCount: number;
  missingCount: number;
  byRecordType: FilesUsageByType[];
  asOf: string;
}

export type FileDisposition = "attachment" | "inline";
