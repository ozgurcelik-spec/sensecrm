import { isPermissionEffective } from "@/lib/entitlements";
import { useAuthStore } from "@/store/auth.store";

/**
 * True when the signed-in user's role grants `permission` in the active organization and the plan
 * state allows using it: `.write` / `.decide` keys are false while the tenant is read-only (trial
 * over, suspended) and every key of a module the plan switches off is false. Use it to hide or
 * disable actions; the server enforces every permission and the plan regardless.
 */
export function usePermission(permission: string): boolean {
  return useAuthStore((state) => isPermissionEffective(state.me, permission));
}

/** True when at least one of `permissions` is granted (see `usePermission`). */
export function useAnyPermission(permissions: readonly string[]): boolean {
  return useAuthStore((state) => permissions.some((p) => isPermissionEffective(state.me, p)));
}
