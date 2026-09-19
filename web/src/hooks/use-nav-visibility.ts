import { useAuthStore } from "@/store/auth.store";
import type { NavItem } from "@/config/navigation";

/**
 * Items the current user may see: no permission list, at least one listed permission granted, or
 * (for items with `visibleWhen`) the named flag is set, for example the caller has pending approvals.
 */
export function useVisibleItems(
  items: readonly NavItem[],
  flags: Readonly<Record<string, boolean>> = {}
): NavItem[] {
  const permissions = useAuthStore((state) => state.me?.permissions);
  const granted = new Set(permissions ?? []);
  return items.filter(
    (item) =>
      !item.permissions?.length ||
      item.permissions.some((p) => granted.has(p)) ||
      (!!item.visibleWhen && !!flags[item.visibleWhen])
  );
}
