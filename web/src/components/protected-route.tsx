import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { Center, Loader } from "@mantine/core";
import { useAuthStore } from "@/store/auth.store";

/** Full-page route that forces users with a temporary password to choose a new one first. */
export const CHANGE_PASSWORD_PATH = "/change-password";

interface ProtectedRouteProps {
  children: ReactNode;
  /** Set on the forced password-change page itself so it is not redirected to itself. */
  allowPasswordChange?: boolean;
}

/**
 * Waits for the persisted profile to rehydrate (otherwise permission-gated UI flashes as absent),
 * then redirects anonymous visitors to /login, remembering where they wanted to go. Users who must
 * change their password (`me.mustChangePassword` or a 403 `auth.password_change_required`) are sent
 * to the forced change screen instead of the app shell.
 */
export default function ProtectedRoute({
  children,
  allowPasswordChange = false,
}: ProtectedRouteProps) {
  const hasHydrated = useAuthStore((state) => state.hasHydrated);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated());
  const mustChangePassword = useAuthStore((state) => state.mustChangePassword);
  const location = useLocation();

  if (!hasHydrated) {
    return (
      <Center h="100vh" data-testid="protected-route-loading">
        <Loader />
      </Center>
    );
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  if (mustChangePassword && !allowPasswordChange) {
    return <Navigate to={CHANGE_PASSWORD_PATH} replace />;
  }

  return <>{children}</>;
}
