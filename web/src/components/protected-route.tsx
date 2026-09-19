import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router";
import { Center, Loader } from "@mantine/core";
import { useAuthStore } from "@/store/auth.store";

/**
 * Waits for the persisted profile to rehydrate (otherwise permission-gated UI flashes as absent),
 * then redirects anonymous visitors to /login, remembering where they wanted to go.
 */
export default function ProtectedRoute({ children }: { children: ReactNode }) {
  const hasHydrated = useAuthStore((state) => state.hasHydrated);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated());
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

  return <>{children}</>;
}
