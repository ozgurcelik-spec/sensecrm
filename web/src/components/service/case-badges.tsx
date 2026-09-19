import { useTranslation } from "react-i18next";
import { Badge, Stack, Text, Tooltip } from "@mantine/core";
import { useSlaLines, type SlaSource } from "@/hooks/use-sla-lines";
import { PRIORITY_BADGE_COLOR, SLA_COLOR, STATUS_BADGE_COLOR, isActiveStatus } from "@/lib/case";
import type { CasePriority, CaseStatus } from "@/types";

export function CaseStatusBadge({ status }: { status: CaseStatus }) {
  const { t } = useTranslation(["service"]);
  return (
    <Badge variant="light" color={STATUS_BADGE_COLOR[status] ?? "gray"}>
      {t(`service:statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function CasePriorityBadge({ priority }: { priority: CasePriority }) {
  const { t } = useTranslation(["service"]);
  return (
    <Badge variant="outline" color={PRIORITY_BADGE_COLOR[priority] ?? "gray"}>
      {t(`service:priorities.${priority}`, { defaultValue: priority })}
    </Badge>
  );
}

/**
 * SLA state of a case: green ok, yellow at risk, red breached; the tooltip lists the targets and the
 * time left / past. A finished (resolved or closed) case only shows a badge when its SLA was breached.
 */
export function SlaBadge({ item }: { item: SlaSource }) {
  const { t } = useTranslation(["service"]);
  const lines = useSlaLines(item);
  if (!isActiveStatus(item.status) && !item.isSlaBreached) {
    return (
      <Text size="sm" c="dimmed">
        -
      </Text>
    );
  }
  return (
    <Tooltip
      multiline
      w={300}
      label={
        <Stack gap={2}>
          {lines.map((line) => (
            <Text key={line.key} size="xs" c="white">
              {line.label}: {line.text}
            </Text>
          ))}
        </Stack>
      }
    >
      <Badge
        variant="light"
        color={SLA_COLOR[item.slaState] ?? "gray"}
        data-sla-state={item.slaState}
        style={{ cursor: "default" }}
      >
        {t(`service:sla.states.${item.slaState}`, { defaultValue: item.slaState })}
      </Badge>
    </Tooltip>
  );
}
