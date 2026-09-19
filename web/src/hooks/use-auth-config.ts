import { useQuery } from "@tanstack/react-query";
import { authConfigKey, getAuthConfig } from "@/services/auth.service";

const FIVE_MINUTES = 5 * 60 * 1000;

/**
 * Whether public sign-up is open. Anything but an explicit `signupEnabled: true` counts as closed (loading, network error),
 * so the "sign up" link never flashes for a deployment that disabled registration. `isKnown` tells the sign-up page when
 * the server has answered, so it only redirects on a real "disabled" answer.
 */
export function useSignupEnabled() {
  const query = useQuery({
    queryKey: authConfigKey,
    queryFn: getAuthConfig,
    staleTime: FIVE_MINUTES,
    retry: 1,
  });
  return {
    signupEnabled: query.data?.signupEnabled === true,
    isKnown: query.data !== undefined,
  };
}
