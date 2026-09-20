/** Fixtures of the file attachment tests (M8C). */
import type { FileAttachment, FileLimits, FilesUsage } from "@/types";

export function fileItem(id: string, overrides: Partial<FileAttachment> = {}): FileAttachment {
  return {
    id,
    recordType: "account",
    recordId: "a1",
    name: `Sozlesme-${id}.pdf`,
    extension: "pdf",
    contentType: "application/pdf",
    sizeBytes: 184223,
    sha256: "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
    state: "ready",
    uploadedByUserId: "user-1",
    uploadedByName: "Ayşe Yılmaz",
    uploadedAt: "2026-09-20T09:00:00Z",
    canPreview: true,
    ...overrides,
  };
}

export const FILE_LIMITS: FileLimits = {
  maxFileBytes: 26214400,
  maxFilesPerRequest: 10,
  allowedExtensions: ["pdf", "png", "jpg", "jpeg", "gif", "webp", "docx", "xlsx", "txt", "csv"],
  previewableExtensions: ["pdf", "png", "jpg", "jpeg", "gif", "webp"],
};

export const FILES_USAGE: FilesUsage = {
  usedBytes: 3221225472,
  fileCount: 214,
  maxBytes: 26843545600,
  quarantinedCount: 1,
  missingCount: 2,
  byRecordType: [
    { recordType: "account", fileCount: 80, sizeBytes: 1200000000 },
    { recordType: "quote", fileCount: 134, sizeBytes: 2021225472 },
  ],
  asOf: "2026-09-20T09:00:00Z",
};

/** A tiny `File` that reports `size` bytes (no real allocation), for the size limit checks. */
export function makeFileOfSize(name: string, size: number): File {
  const file = new File(["x"], name);
  Object.defineProperty(file, "size", { value: size });
  return file;
}

/** A `File` of `bytes` bytes (the content is zeros; the size is what the pre-checks read). */
export function makeFile(name: string, bytes = 1024, type = "application/octet-stream"): File {
  return new File([new Uint8Array(bytes)], name, { type });
}
