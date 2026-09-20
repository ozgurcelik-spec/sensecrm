import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { getSubscription, subscriptionKeys } from "@/services/subscription.service";
import { useAuthStore } from "@/store/auth.store";

/** How often the shell re-reads `/me` so trial end, suspension and plan changes show up without a reload. */
export const SUBSCRIPTION_SYNC_INTERVAL_MS = 5 * 60_000;

/** `GET /subscription` (own plan, limits, usage) for the "Plan ve kullanım" page. */
export function useSubscription(enabled = true) {
  return useQuery({
    queryKey: subscriptionKeys.current,
    queryFn: getSubscription,
    enabled,
  });
}

/**
 * Re-reads `/me` every 5 minutes while the tab is visible (the subscription state lives on it).
 * Mounted by the app shell, including while the "blocked" screen is shown, so a reactivation is noticed.
 */
export function useSubscriptionSync(intervalMs = SUBSCRIPTION_SYNC_INTERVAL_MS): void {
  const refreshMe = useAuthStore((state) => state.refreshMe);
  useEffect(() => {
    const id = window.setInterval(() => {
      if (document.visibilityState !== "hidden") void refreshMe();
    }, intervalMs);
    return () => window.clearInterval(id);
  }, [refreshMe, intervalMs]);
}
