import { useAuthStore } from "@/store/auth.store";
import type { NavItem } from "@/config/navigation";
import { isModuleOn, isPermissionEffective } from "@/lib/entitlements";

/**
 * Items the current user may see: no permission list, at least one listed permission granted, or
 * (for items with `visibleWhen`) the named flag is set, for example the caller has pending approvals.
 * Items of a module the plan switches off (`module`, `me.subscription.modules`) are always hidden,
 * and platform items (`platformAdminOnly`) are only for platform admins.
 */
export function useVisibleItems(
  items: readonly NavItem[],
  flags: Readonly<Record<string, boolean>> = {}
): NavItem[] {
  const me = useAuthStore((state) => state.me);
  return items.filter((item) => {
    if (item.platformAdminOnly && !me?.user.isPlatformAdmin) return false;
    if (item.module && !isModuleOn(me?.subscription, item.module)) return false;
    return (
      !item.permissions?.length ||
      item.permissions.some((p) => isPermissionEffective(me, p)) ||
      (!!item.visibleWhen && !!flags[item.visibleWhen])
    );
  });
}
