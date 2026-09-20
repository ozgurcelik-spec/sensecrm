import type { ReactNode } from "react";
import NoAccess from "@/components/no-access";
import { useIsPlatformAdmin } from "@/hooks/use-module-enabled";

/**
 * Platform console routes are for platform admins only (`me.user.isPlatformAdmin`). Nobody else
 * gets past this, whatever tenant permissions they hold; the server checks the flag again on every call.
 */
export function PlatformGuard({ children }: { children: ReactNode }) {
  const isPlatformAdmin = useIsPlatformAdmin();
  return isPlatformAdmin ? <>{children}</> : <NoAccess />;
}
