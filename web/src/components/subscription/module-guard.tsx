import type { ReactNode } from "react";
import { useModuleEnabled } from "@/hooks/use-module-enabled";
import type { GatedModule } from "@/types";
import ModuleDisabled from "./module-disabled";

/**
 * Route guard for a gated module: when the plan switches the module off, the "module disabled"
 * page is shown instead and the module's page (and its requests) is never mounted.
 */
export function ModuleGuard({ module, children }: { module: GatedModule; children: ReactNode }) {
  const enabled = useModuleEnabled(module);
  return enabled ? <>{children}</> : <ModuleDisabled module={module} />;
}
