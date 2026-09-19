/**
 * Wires the api-client's generic refresh / session-expiry hooks to the auth store and a toast.
 * Call once at startup (main.tsx).
 */
import { setRefreshHandler, setSessionExpiredHandler } from "@/lib/api-client";
import i18n from "@/i18n";
import { toast } from "@/hooks/use-toast";
import { useAuthStore } from "@/store/auth.store";

export function registerSessionRefreshHandlers(): void {
  setRefreshHandler(() => useAuthStore.getState().refresh());
  setSessionExpiredHandler(() => {
    toast({
      variant: "destructive",
      description: i18n.t("auth:sessionExpired"),
      duration: false,
    });
  });
}
