import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Card, Group, Text } from "@mantine/core";
import { Zap } from "lucide-react";
import { usePermission } from "@/hooks/use-permission";
import { useSubjectExecution } from "@/hooks/use-workflows";
import { EXECUTION_STATUS_COLOR } from "@/lib/workflow";
import { PERMISSIONS, type WorkflowSubjectType } from "@/types";
import { EXECUTION_PARAM } from "./executions-tab";

interface WorkflowStatusStripProps {
  subjectType: WorkflowSubjectType;
  subjectId: string;
}

/**
 * "İş akışı: <kural> — çalışıyor / tamamlandı / hata" on the "Genel" tab of a lead or deal: the
 * newest execution for that record. Only for `org.workflows.manage` (the executions endpoint needs
 * it); while loading, when there is none and on any error it renders nothing. It links to the
 * execution drawer of the workflows settings page.
 */
export function WorkflowStatusStrip({ subjectType, subjectId }: WorkflowStatusStripProps) {
  const { t } = useTranslation(["workflows"]);
  const canManage = usePermission(PERMISSIONS.orgWorkflowsManage);
  const { data: execution } = useSubjectExecution(subjectType, subjectId, canManage);
  if (!canManage || !execution) return null;

  const to = `/app/settings/workflows?tab=executions&${EXECUTION_PARAM}=${encodeURIComponent(execution.id)}`;
  return (
    <Card
      withBorder
      padding="sm"
      role="status"
      aria-label={t("workflows:strip.label")}
      data-testid="workflow-strip"
    >
      <Group gap="xs" wrap="nowrap">
        <Zap
          size={16}
          color={`var(--mantine-color-${EXECUTION_STATUS_COLOR[execution.status] ?? "gray"}-6)`}
          aria-hidden="true"
        />
        <Text size="sm">
          {t("workflows:strip.prefix")}:{" "}
          <Anchor
            component={Link}
            to={to}
            size="sm"
            aria-label={`${t("workflows:strip.open")}: ${execution.ruleName}`}
          >
            {execution.ruleName}
          </Anchor>{" "}
          {"— "}
          <Text span fw={600} c={EXECUTION_STATUS_COLOR[execution.status] ?? "gray"}>
            {t(`workflows:strip.${execution.status}`, { defaultValue: execution.status })}
          </Text>
        </Text>
      </Group>
    </Card>
  );
}
