import { useAuthStore } from "@/store/auth.store";
import type { NavItem } from "@/config/navigation";

/** Items the current user may see: no permission list, or at least one listed permission granted. */
export function useVisibleItems(items: readonly NavItem[]): NavItem[] {
  const permissions = useAuthStore((state) => state.me?.permissions);
  const granted = new Set(permissions ?? []);
  return items.filter(
    (item) => !item.permissions?.length || item.permissions.some((p) => granted.has(p))
  );
}
