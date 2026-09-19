import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Code,
  Drawer,
  Group,
  Skeleton,
  Stack,
  Text,
  Timeline,
} from "@mantine/core";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { usePermission } from "@/hooks/use-permission";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useExecution, useRetryExecution, useTerminateExecution } from "@/hooks/use-workflows";
import { formatDateTime } from "@/lib/dates";
import { subjectPath, subjectReadPermission } from "@/lib/workflow";
import { useAuthStore } from "@/store/auth.store";
import type { ExecutionDetail, ExecutionStep } from "@/types";
import { ApprovalStatusBadge, ExecutionStatusBadge, KindBadge } from "./badges";

const STEP_COLOR: Record<string, string> = {
  COMPLETED: "green",
  IN_PROGRESS: "blue",
  SCHEDULED: "gray",
  FAILED: "red",
  FAILED_WITH_TERMINAL_ERROR: "red",
  CANCELED: "gray",
  SKIPPED: "gray",
  TIMED_OUT: "orange",
};

/** Error text of an execution: a known machine code is translated, anything else is shown as sent. */
function ExecutionError({ error }: { error: string }) {
  const { t } = useTranslation(["workflows"]);
  const text = /^[a-z_]+$/.test(error)
    ? t(`workflows:executions.errors.${error}`, { defaultValue: error })
    : error;
  return (
    <Alert color="red" variant="light" title={t("workflows:executions.error")} role="alert">
      {text}
    </Alert>
  );
}

function StepOutput({ output }: { output: unknown }) {
  const { t } = useTranslation(["workflows"]);
  if (output === undefined || output === null) return null;
  const text = typeof output === "string" ? output : JSON.stringify(output, null, 2);
  if (!text || text === "{}") return null;
  return (
    <Code block mt={4} aria-label={t("workflows:executions.stepOutput")} style={{ maxHeight: 160 }}>
      {text.length > 800 ? `${text.slice(0, 800)}...` : text}
    </Code>
  );
}

function StepTimeline({ steps }: { steps: ExecutionStep[] }) {
  const { t } = useTranslation(["workflows"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  if (steps.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        {t("workflows:executions.noSteps")}
      </Text>
    );
  }
  return (
    <Timeline bulletSize={16} lineWidth={2} data-testid="step-timeline">
      {steps.map((step, index) => (
        <Timeline.Item
          key={`${step.name}-${index}`}
          color={STEP_COLOR[step.status] ?? "gray"}
          title={
            <Group gap="xs">
              <Text size="sm" fw={500}>
                {step.name}
              </Text>
              <Badge size="xs" variant="light" color={STEP_COLOR[step.status] ?? "gray"}>
                {t(`workflows:executions.stepStatuses.${step.status}`, {
                  defaultValue: step.status,
                })}
              </Badge>
            </Group>
          }
        >
          {(step.startedAt || step.endedAt) && (
            <Text size="xs" c="dimmed">
              {[step.startedAt, step.endedAt]
                .filter((v): v is string => !!v)
                .map((v) => formatDateTime(v, timeZone))
                .join(" - ")}
            </Text>
          )}
          <StepOutput output={step.output} />
        </Timeline.Item>
      ))}
    </Timeline>
  );
}

function ExecutionBody({ execution }: { execution: ExecutionDetail }) {
  const { t } = useTranslation(["workflows", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const terminate = useTerminateExecution();
  const retry = useRetryExecution();
  const [confirm, setConfirm] = useState<"terminate" | "retry" | null>(null);
  const readPermission = subjectReadPermission(execution.subjectType);
  const canOpenSubject = usePermission(readPermission ?? "");
  const path = subjectPath(execution.subjectType, execution.subjectId);
  const subjectLabel = execution.subjectName ?? execution.subjectId;

  async function run(action: "terminate" | "retry") {
    try {
      if (action === "terminate") await terminate.mutateAsync(execution.id);
      else await retry.mutateAsync(execution.id);
      toast({
        variant: "success",
        description:
          action === "terminate"
            ? t("workflows:executions.terminated")
            : t("workflows:executions.retried"),
      });
    } catch (error) {
      // workflow.not_running (409): somebody else finished it in the meantime; the lists refetch anyway.
      toastApiError(error);
    }
    setConfirm(null);
  }

  return (
    <Stack gap="md">
      <Group gap="xs">
        <KindBadge kind={execution.kind} />
        <ExecutionStatusBadge status={execution.status} />
      </Group>

      <Stack gap={4}>
        <Text size="xs" c="dimmed">
          {t("workflows:executions.subject")}
        </Text>
        <Text size="sm">
          {t(`workflows:executions.subjectTypes.${execution.subjectType}`, {
            defaultValue: execution.subjectType,
          })}
          {": "}
          {path && readPermission && canOpenSubject ? (
            <Anchor component={Link} to={path} size="sm">
              {subjectLabel}
            </Anchor>
          ) : (
            subjectLabel
          )}
        </Text>
        <Text size="xs" c="dimmed">
          {t("workflows:executions.startedAt")}: {formatDateTime(execution.startedAt, timeZone)}
          {execution.endedAt &&
            ` · ${t("workflows:executions.endedAt")}: ${formatDateTime(execution.endedAt, timeZone)}`}
        </Text>
      </Stack>

      {execution.error && <ExecutionError error={execution.error} />}

      <Group gap="sm">
        {execution.status === "running" && (
          <Button color="red" variant="light" onClick={() => setConfirm("terminate")}>
            {t("workflows:executions.terminate")}
          </Button>
        )}
        {execution.status === "failed" && (
          <Button variant="light" onClick={() => setConfirm("retry")}>
            {t("workflows:executions.retry")}
          </Button>
        )}
      </Group>

      <div>
        <Text fw={600} mb="xs">
          {t("workflows:executions.steps")}
        </Text>
        <StepTimeline steps={execution.steps ?? []} />
      </div>

      <div>
        <Text fw={600} mb="xs">
          {t("workflows:executions.approvals")}
        </Text>
        {execution.approvals && execution.approvals.length > 0 ? (
          <Stack gap="xs" component="ul" p={0} m={0} style={{ listStyle: "none" }}>
            {execution.approvals.map((approval) => (
              <Group key={approval.id} component="li" gap="xs" justify="space-between">
                <Text size="sm">{approval.approverName}</Text>
                <Group gap="xs">
                  {approval.decidedAt && (
                    <Text size="xs" c="dimmed">
                      {formatDateTime(approval.decidedAt, timeZone)}
                    </Text>
                  )}
                  <ApprovalStatusBadge status={approval.status} />
                </Group>
              </Group>
            ))}
          </Stack>
        ) : (
          <Text size="sm" c="dimmed">
            {t("workflows:executions.noApprovals")}
          </Text>
        )}
      </div>

      <ConfirmDialog
        opened={confirm === "terminate"}
        title={t("workflows:executions.terminateTitle")}
        message={t("workflows:executions.terminateMessage", { name: execution.ruleName })}
        confirmLabel={t("workflows:executions.terminate")}
        destructive
        loading={terminate.isPending}
        onConfirm={() => void run("terminate")}
        onClose={() => setConfirm(null)}
      />
      <ConfirmDialog
        opened={confirm === "retry"}
        title={t("workflows:executions.retryTitle")}
        message={t("workflows:executions.retryMessage", { name: execution.ruleName })}
        confirmLabel={t("workflows:executions.retry")}
        loading={retry.isPending}
        onConfirm={() => void run("retry")}
        onClose={() => setConfirm(null)}
      />
    </Stack>
  );
}

interface ExecutionDrawerProps {
  executionId: string;
  onClose: () => void;
}

/** Side panel with one execution: state, error, step timeline, approvals and the terminate / retry actions. */
export function ExecutionDrawer({ executionId, onClose }: ExecutionDrawerProps) {
  const { t } = useTranslation(["workflows", "common"]);
  const { data, isLoading, error, refetch } = useExecution(executionId);
  return (
    <Drawer
      opened
      onClose={onClose}
      position="right"
      size="lg"
      closeButtonProps={{ "aria-label": t("common:close") }}
      title={data ? data.ruleName : t("workflows:executions.detailTitle")}
    >
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Stack gap="sm">
          <Skeleton h={24} w={200} />
          <Skeleton h={160} />
        </Stack>
      ) : (
        <ExecutionBody execution={data} />
      )}
    </Drawer>
  );
}
