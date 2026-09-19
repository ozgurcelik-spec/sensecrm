import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import { APPROVAL_STATUS_COLOR, EXECUTION_STATUS_COLOR, KIND_COLOR } from "@/lib/workflow";
import type { ApprovalStatus, ExecutionStatus, WorkflowKind } from "@/types";

export function KindBadge({ kind }: { kind: WorkflowKind }) {
  const { t } = useTranslation(["workflows"]);
  return (
    <Badge variant="light" color={KIND_COLOR[kind] ?? "gray"}>
      {t(`workflows:kinds.${kind}`, { defaultValue: kind })}
    </Badge>
  );
}

export function ExecutionStatusBadge({ status }: { status: ExecutionStatus }) {
  const { t } = useTranslation(["workflows"]);
  return (
    <Badge variant="light" color={EXECUTION_STATUS_COLOR[status] ?? "gray"}>
      {t(`workflows:executions.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function ApprovalStatusBadge({ status }: { status: ApprovalStatus }) {
  const { t } = useTranslation(["workflows"]);
  return (
    <Badge variant="light" color={APPROVAL_STATUS_COLOR[status] ?? "gray"}>
      {t(`workflows:approvals.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}
