import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import { STATUS_COLOR } from "@/lib/platform";
import type { PlatformTenantStatus } from "@/types";

/** Effective tenant status: trial blue, active green, trial over / suspended red, deletion pending grey-red. */
export function TenantStatusBadge({ status }: { status: PlatformTenantStatus }) {
  const { t } = useTranslation(["platform"]);
  return (
    <Badge variant="light" color={STATUS_COLOR[status] ?? "gray"} data-testid="status-badge">
      {t(`platform:status.${status}`, { defaultValue: status })}
    </Badge>
  );
}
