/**
 * Plan entitlements as the UI sees them (`GET /me` `subscription`): which gated modules are on and
 * whether writes are refused. A missing `subscription` (older server / no platform module) means
 * "everything on, full access". The server enforces all of this; the UI only avoids dead ends.
 */
import type { GatedModule, Me, MeSubscription, PlatformAccessLevel } from "@/types";

/** Permission key -> the gated module the permission belongs to (undefined for core permissions). */
export function permissionModule(permission: string): GatedModule | undefined {
  if (permission.startsWith("crm.campaigns.")) return "marketing";
  if (
    permission.startsWith("crm.products.") ||
    permission.startsWith("crm.quotes.") ||
    permission.startsWith("crm.orders.")
  ) {
    return "commerce";
  }
  if (permission.startsWith("crm.cases.")) return "service";
  if (permission === "org.workflows.manage" || permission.startsWith("crm.approvals.")) {
    return "workflows";
  }
  return undefined;
}

export function accessLevelOf(subscription: MeSubscription | undefined): PlatformAccessLevel {
  return subscription?.accessLevel ?? "full";
}

/** Only an explicit `false` switches a module off. */
export function isModuleOn(subscription: MeSubscription | undefined, module: GatedModule): boolean {
  return subscription?.modules[module] !== false;
}

export type UsageLevel = "ok" | "warn" | "full";

/** Share of a limit that is used, in whole percent (not capped: over the limit shows > 100). 0 when the limit is 0 and unused. */
export function usagePercent(used: number, max: number): number {
  if (max <= 0) return used > 0 ? 100 : 0;
  return Math.round((used / max) * 100);
}

/** Bars turn orange from 80 % and red at 100 % of the limit. */
export function usageLevel(used: number, max: number): UsageLevel {
  const percent = max <= 0 ? (used >= 0 ? 100 : 0) : (used / max) * 100;
  if (percent >= 100) return "full";
  if (percent >= 80) return "warn";
  return "ok";
}

/** Permissions that change data: refused by the server while the tenant is read-only. */
export function isWritePermission(permission: string): boolean {
  return permission.endsWith(".write") || permission.endsWith(".decide");
}

/**
 * True when the role grants `permission` **and** the plan state allows using it: a disabled
 * module withdraws all its permissions; read-only access withdraws `.write` / `.decide`
 * (`.manage` and `.read` stay, so settings pages remain visible and the server answers writes).
 */
export function isPermissionEffective(me: Me | null | undefined, permission: string): boolean {
  if (!me?.permissions.includes(permission)) return false;
  const subscription = me.subscription;
  if (!subscription) return true;
  const module = permissionModule(permission);
  if (module && !isModuleOn(subscription, module)) return false;
  if (subscription.accessLevel !== "full" && isWritePermission(permission)) return false;
  return true;
}
