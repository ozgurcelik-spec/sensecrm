/**
 * Wires the api-client's generic refresh / session-expiry hooks to the auth store and a toast.
 * Call once at startup (main.tsx).
 */
import {
  setPasswordChangeRequiredHandler,
  setPlanStateErrorHandler,
  setRefreshHandler,
  setSessionExpiredHandler,
} from "@/lib/api-client";
import i18n from "@/i18n";
import { toast } from "@/hooks/use-toast";
import { queryClient } from "@/lib/query-client";
import { subscriptionKeys } from "@/services/subscription.service";
import { useAuthStore } from "@/store/auth.store";

/** Plan-state errors can come in bursts (a page firing several requests); re-read at most this often. */
const PLAN_STATE_REFETCH_MS = 10_000;
let lastPlanStateRefetch = 0;

export function registerSessionRefreshHandlers(): void {
  // 403 tenant.suspended / plan.module_disabled, 402 plan.limit_exceeded: the cached plan state is stale.
  setPlanStateErrorHandler(() => {
    const now = Date.now();
    if (now - lastPlanStateRefetch < PLAN_STATE_REFETCH_MS) return;
    lastPlanStateRefetch = now;
    void useAuthStore.getState().refreshMe();
    void queryClient.invalidateQueries({ queryKey: subscriptionKeys.all });
  });
  setRefreshHandler(() => useAuthStore.getState().refresh());
  // 403 auth.password_change_required anywhere: the protected routes redirect to the forced screen.
  setPasswordChangeRequiredHandler(() => useAuthStore.getState().setMustChangePassword(true));
  setSessionExpiredHandler(() => {
    toast({
      variant: "destructive",
      description: i18n.t("auth:sessionExpired"),
      duration: false,
    });
  });
}
