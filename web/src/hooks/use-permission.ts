import { useAuthStore } from "@/store/auth.store";

/**
 * True when the signed-in user's role grants `permission` in the active organization. Use it to
 * hide or disable actions; the server enforces every permission regardless.
 */
export function usePermission(permission: string): boolean {
  return useAuthStore((state) => state.me?.permissions.includes(permission) ?? false);
}

/** True when at least one of `permissions` is granted. */
export function useAnyPermission(permissions: readonly string[]): boolean {
  return useAuthStore((state) =>
    permissions.some((p) => state.me?.permissions.includes(p) ?? false)
  );
}
