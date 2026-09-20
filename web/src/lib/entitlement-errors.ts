/**
 * Friendly texts for the plan / tenant-state error codes (`tenant.suspended`, `plan.module_disabled`,
 * `plan.limit_exceeded`, `platform.*`). The server sends the numbers and names in `args`; this turns
 * them into interpolation values for `subscription:errors.<code>` / `platform:errors.<code>`.
 */
import i18n from "@/i18n";
import type { ApiProblem } from "@/lib/api-error";

export const TENANT_SUSPENDED = "tenant.suspended";
export const PLAN_MODULE_DISABLED = "plan.module_disabled";
export const PLAN_LIMIT_EXCEEDED = "plan.limit_exceeded";

/** Codes after which the cached `/me` (plan state) is stale and worth re-reading. */
export function isPlanStateCode(code: string | undefined): boolean {
  return code === TENANT_SUSPENDED || code === PLAN_MODULE_DISABLED || code === PLAN_LIMIT_EXCEEDED;
}

const str = (value: unknown): string | undefined =>
  typeof value === "string" || typeof value === "number" ? String(value) : undefined;

/** "kullanıcı" for the user limit, the module name for a record limit, "kayıt" otherwise. */
export function limitLabel(limit: string | undefined, module: string | undefined): string {
  if (limit === "users") return i18n.t("subscription:limits.users");
  if (module) {
    return i18n.t("subscription:limits.recordsOf", {
      module: i18n.t(`subscription:modules.${module}`, { defaultValue: module }),
    });
  }
  return i18n.t("subscription:limits.records");
}

/** Interpolation values for the message of `problem.code`; unknown codes get the raw `args`. */
export function problemArgs(problem: ApiProblem): Record<string, unknown> {
  const args = { ...(problem.args ?? {}) };
  switch (problem.code) {
    case TENANT_SUSPENDED: {
      const reason = str(args.reason) ?? "suspended";
      args.reasonText = i18n.t(`subscription:reasons.${reason}`, {
        defaultValue: i18n.t("subscription:reasons.suspended"),
      });
      break;
    }
    case PLAN_MODULE_DISABLED: {
      const module = str(args.module) ?? "";
      args.moduleLabel = i18n.t(`subscription:modules.${module}`, { defaultValue: module });
      break;
    }
    case PLAN_LIMIT_EXCEEDED:
      args.label = limitLabel(str(args.limit), str(args.module));
      break;
    case "platform.invalid_transition":
      for (const key of ["from", "to"] as const) {
        const value = str(args[key]);
        if (value) args[key] = i18n.t(`platform:status.${value}`, { defaultValue: value });
      }
      break;
    default:
      break;
  }
  return args;
}
