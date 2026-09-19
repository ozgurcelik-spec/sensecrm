import type { ReactNode } from "react";
import { useAuthStore } from "@/store/auth.store";

interface PermissionGuardProps {
  /** Permission key, e.g. "org.users.manage". */
  permission?: string;
  /** Any-of list of permission keys - passes if the user has at least one. */
  anyOf?: string[];
  children: ReactNode;
  /** Rendered instead of children when the check fails (default: nothing). */
  fallback?: ReactNode;
}

/**
 * Hides (or swaps) UI based on the current user's permissions. Backed by
 * `authStore.hasPermission` - the single source of truth for permission checks. The server still
 * enforces every permission; this only keeps the UI honest.
 */
export function PermissionGuard({
  permission,
  anyOf,
  children,
  fallback = null,
}: PermissionGuardProps) {
  const permissions = useAuthStore((state) => state.me?.permissions);
  const granted = permissions ?? [];
  const allowed = permission
    ? granted.includes(permission)
    : anyOf
      ? anyOf.some((p) => granted.includes(p))
      : true;

  return allowed ? <>{children}</> : <>{fallback}</>;
}
