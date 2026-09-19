import axios from "axios";
import type { FieldValues, Path, UseFormSetError } from "react-hook-form";
import i18n from "@/i18n";

/** Backend ProblemDetails: `code` is a stable key translated on the client (`common:errors.<code>`). */
export interface ApiProblem {
  title?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
}

export function getApiErrorStatus(error: unknown): number | undefined {
  return axios.isAxiosError(error) ? error.response?.status : undefined;
}

export function getApiProblem(error: unknown): ApiProblem | undefined {
  if (!axios.isAxiosError(error)) return undefined;
  const data = error.response?.data;
  return data && typeof data === "object" ? (data as ApiProblem) : undefined;
}

/**
 * User-facing message for an API error: translated `code`, then the server `title`, then a
 * generic (network / unknown) message.
 */
export function getApiErrorMessage(error: unknown): string {
  const problem = getApiProblem(error);
  if (problem?.code) {
    // C-SEC codes live in the `security` namespace (`security:errors.<code>`) to keep common.json stable.
    for (const key of [`common:errors.${problem.code}`, `security:errors.${problem.code}`]) {
      if (i18n.exists(key)) return i18n.t(key);
    }
  }
  if (problem?.title) return problem.title;
  if (axios.isAxiosError(error) && !error.response) return i18n.t("common:errors.network");
  return i18n.t("common:errors.unknown");
}

/**
 * Copies `validation` field errors onto a react-hook-form instance. Returns true when at least one
 * field matched (the caller can then skip the generic toast).
 */
export function applyValidationErrors<T extends FieldValues>(
  error: unknown,
  setError: UseFormSetError<T>,
  fields: readonly Path<T>[]
): boolean {
  const errors = getApiProblem(error)?.errors;
  if (!errors) return false;
  let matched = false;
  for (const [rawField, messages] of Object.entries(errors)) {
    // "BillingAddress.City" -> "billingAddress.city": every path segment is lower-camel-cased.
    const field = rawField
      .split(".")
      .map((segment) => segment.charAt(0).toLowerCase() + segment.slice(1))
      .join(".");
    const target = fields.find((f) => f === field);
    if (target && messages[0]) {
      setError(target, { type: "server", message: messages[0] });
      matched = true;
    }
  }
  return matched;
}
