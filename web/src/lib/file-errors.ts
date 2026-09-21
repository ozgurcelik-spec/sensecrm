/**
 * Error texts of the file attachment endpoints (M8C). Server ProblemDetails, the `failed[]` entries
 * of a partly successful upload and the client side pre-checks all end up as `{ code, args }` and are
 * translated with `files:errors.<code>` (`getApiErrorMessage` knows the same namespace for toasts).
 */
import i18n from "@/i18n";
import { getApiErrorMessage, getApiErrorStatus, getApiProblem } from "@/lib/api-error";
import { problemArgs } from "@/lib/entitlement-errors";

export const FILE_QUOTA_EXCEEDED = "file.quota_exceeded";

export interface FileErrorInfo {
  /** ProblemDetails `code` when the server sent one. */
  code?: string;
  args?: Record<string, unknown>;
  message: string;
}

/** Text of a file error code; unknown codes fall back to the generic message. */
export function fileErrorText(code: string, args?: Record<string, unknown>): string {
  const key = `files:errors.${code}`;
  if (i18n.exists(key)) return i18n.t(key, problemArgs({ code, args }));
  for (const other of [`common:errors.${code}`, `subscription:errors.${code}`]) {
    if (i18n.exists(other)) return i18n.t(other, problemArgs({ code, args }));
  }
  return i18n.t("common:errors.unknown");
}

/** Code, args and the translated message of whatever an upload / rename / delete call threw. */
export function describeFileError(error: unknown): FileErrorInfo {
  const problem = getApiProblem(error);
  // A proxy (nginx `client_max_body_size`) answers an oversized body with a bare 413, no ProblemDetails.
  if (!problem?.code && getApiErrorStatus(error) === 413) {
    return { code: "file.too_large", message: i18n.t("files:errors.tooLargeUnknown") };
  }
  return {
    code: problem?.code,
    args: problem?.args,
    message: getApiErrorMessage(error),
  };
}
