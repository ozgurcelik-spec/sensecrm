import { accessLevelOf, isModuleOn } from "@/lib/entitlements";
import { useAuthStore } from "@/store/auth.store";
import type { GatedModule, MeSubscription, PlatformAccessLevel } from "@/types";

/** `false` only when the plan explicitly switches the module off (unknown / old server = on). */
export function useModuleEnabled(module: GatedModule): boolean {
  return useAuthStore((state) => isModuleOn(state.me?.subscription, module));
}

/** `GET /me` `subscription` of the active organization (undefined on servers without the platform module). */
export function useMeSubscription(): MeSubscription | undefined {
  return useAuthStore((state) => state.me?.subscription);
}

/** `full` unless the server says otherwise; `readOnly` refuses writes, `none` blocks the whole app. */
export function useAccessLevel(): PlatformAccessLevel {
  return useAuthStore((state) => accessLevelOf(state.me?.subscription));
}

export function useIsPlatformAdmin(): boolean {
  return useAuthStore((state) => state.me?.user.isPlatformAdmin === true);
}
