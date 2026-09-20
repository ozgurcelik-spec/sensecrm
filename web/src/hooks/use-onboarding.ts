import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { dismissOnboarding, getOnboarding, subscriptionKeys } from "@/services/subscription.service";

/** The first-run checklist; needs `org.settings.manage`, so callers pass `enabled`. */
export function useOnboarding(enabled = true) {
  return useQuery({
    queryKey: subscriptionKeys.onboarding,
    queryFn: getOnboarding,
    enabled,
    // A stale checklist is harmless; do not retry a permission or plan error.
    retry: false,
  });
}

export function useDismissOnboarding() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: dismissOnboarding,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: subscriptionKeys.onboarding }),
  });
}
