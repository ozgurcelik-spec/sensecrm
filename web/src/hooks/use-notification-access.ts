import { useAccessLevel } from "@/hooks/use-module-enabled";
import { usePermission } from "@/hooks/use-permission";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

/** Notifications are for every signed-in member; a blocked tenant (`accessLevel: none`) makes no calls. */
export function useNotificationsAvailable(): boolean {
  const signedIn = useAuthStore((state) => !!state.me);
  const accessLevel = useAccessLevel();
  return signedIn && accessLevel !== "none";
}

/** `org.notifications.manage`: tenant settings, delivery log, recipient issues. */
export function useCanManageNotifications(): boolean {
  return usePermission(PERMISSIONS.orgNotificationsManage);
}

/**
 * Manage permission and full access: while the tenant is read-only the server answers settings,
 * retry, reset and test e-mail with `tenant.suspended`, so those actions are hidden. (The user's own
 * read / dismiss / preference commands stay open in read-only mode.)
 */
export function useCanChangeNotificationSettings(): boolean {
  const canManage = useCanManageNotifications();
  const accessLevel = useAccessLevel();
  return canManage && accessLevel === "full";
}
