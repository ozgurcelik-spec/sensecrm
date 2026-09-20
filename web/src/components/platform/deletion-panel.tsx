import { useTranslation } from "react-i18next";
import { Alert, Badge, Button, Card, Stack, Text } from "@mantine/core";
import { TriangleAlert } from "lucide-react";
import { InfoPanel } from "@/components/crm/record-detail-shell";
import { formatDateTime } from "@/lib/dates";
import { orDash } from "@/lib/format";
import type { PlatformDeletion } from "@/types";

const STATUS_COLOR: Record<string, string> = {
  scheduled: "orange",
  cancelled: "gray",
  running: "blue",
  completed: "dark",
  failed: "red",
};

interface DeletionPanelProps {
  deletion: PlatformDeletion;
  /** Present only while the request can still be cancelled. */
  onCancel?: () => void;
}

/** "Silme" tab: the latest KVKK deletion request with its retention window and, on failure, the error. */
export function DeletionPanel({ deletion, onCancel }: DeletionPanelProps) {
  const { t } = useTranslation(["platform"]);
  return (
    <Stack gap="md">
      {deletion.status === "failed" && (
        <Alert
          color="red"
          variant="light"
          icon={<TriangleAlert size={16} />}
          title={t("platform:deletion.failedTitle")}
          role="alert"
        >
          <Text size="sm">
            {t("platform:deletion.attempts", { count: deletion.attempts })}
            {deletion.lastError ? ` - ${deletion.lastError}` : ""}
          </Text>
        </Alert>
      )}
      <Card withBorder padding="md" data-testid="deletion-panel">
        <Stack gap="sm">
          <Badge variant="light" color={STATUS_COLOR[deletion.status] ?? "gray"} w="fit-content">
            {t(`platform:deletion.status.${deletion.status}`, { defaultValue: deletion.status })}
          </Badge>
          <Text size="sm">
            {t("platform:deletion.retentionSummary", {
              days: deletion.retentionDays,
              date: formatDateTime(deletion.scheduledFor),
            })}
          </Text>
          {onCancel && (
            <Button color="orange" variant="light" w="fit-content" onClick={onCancel}>
              {t("platform:actions.cancelDeletion")}
            </Button>
          )}
        </Stack>
      </Card>
      <InfoPanel
        title={t("platform:deletion.details")}
        rows={[
          { label: t("platform:deletion.requestedAt"), value: formatDateTime(deletion.requestedAt) },
          { label: t("platform:deletion.requestedBy"), value: orDash(deletion.requestedByEmail) },
          { label: t("platform:deletion.reasonLabel"), value: deletion.reason },
          { label: t("platform:deletion.scheduledFor"), value: formatDateTime(deletion.scheduledFor) },
          ...(deletion.cancelledAt
            ? [{ label: t("platform:deletion.cancelledAt"), value: formatDateTime(deletion.cancelledAt) }]
            : []),
          ...(deletion.completedAt
            ? [{ label: t("platform:deletion.completedAt"), value: formatDateTime(deletion.completedAt) }]
            : []),
          { label: t("platform:deletion.attemptsLabel"), value: String(deletion.attempts) },
        ]}
      />
    </Stack>
  );
}
