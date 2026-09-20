import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon, Indicator } from "@mantine/core";
import { ClipboardCheck } from "lucide-react";
import { usePendingApprovalCount } from "@/hooks/use-approvals";
import { useModuleEnabled } from "@/hooks/use-module-enabled";
import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";

/**
 * Top bar shortcut to "Onaylarım" with the number of the caller's pending approvals. Shown to
 * approvers (`crm.approvals.decide`) and to anyone who currently has a pending approval.
 */
export function ApprovalsBell() {
  const { t } = useTranslation(["workflows"]);
  const canDecide = usePermission(PERMISSIONS.crmApprovalsDecide);
  // Approvals are part of the workflows module: no request (and no bell) while the plan lacks it.
  const workflowsOn = useModuleEnabled("workflows");
  const { data: count = 0 } = usePendingApprovalCount(workflowsOn);
  if (!workflowsOn || (!canDecide && count === 0)) return null;

  return (
    <Indicator label={count > 99 ? "99+" : count} size={16} color="red" disabled={count === 0}>
      <ActionIcon
        component={Link}
        to="/app/approvals"
        variant="subtle"
        color="gray"
        size="lg"
        aria-label={
          count > 0 ? t("workflows:approvals.bellCount", { count }) : t("workflows:approvals.bell")
        }
      >
        <ClipboardCheck size={18} />
      </ActionIcon>
    </Indicator>
  );
}
