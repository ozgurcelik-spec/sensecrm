import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Code, Drawer, Group, Skeleton, Stack, Text } from "@mantine/core";
import { Info } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { useDelivery } from "@/hooks/use-notifications";
import { formatDateTime } from "@/lib/dates";
import { deliveryPayload } from "@/lib/notification-preferences";
import { deliveryReasonText, kindLabel } from "@/lib/notifications";
import { useAuthStore } from "@/store/auth.store";
import { DeliveryStatusBadge } from "./delivery-status-badge";

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Group justify="space-between" wrap="nowrap" align="flex-start" gap="lg">
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      <Text size="sm" ta="right">
        {children}
      </Text>
    </Group>
  );
}

/** Details of one delivery (the payload / error viewer of the delivery log). */
export function DeliveryDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation(["notifications"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { data, isLoading, error, refetch } = useDelivery(id);
  const when = (value?: string) => (value ? formatDateTime(value, timeZone) : "-");
  const reason = data ? deliveryReasonText(data) : undefined;

  return (
    <Drawer opened onClose={onClose} position="right" size="md" title={t("notifications:delivery.detail.title")}>
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Skeleton h={200} />
      ) : (
        <Stack gap="sm">
          <Group justify="space-between">
            <DeliveryStatusBadge status={data.status} />
            <Text size="sm">{kindLabel(data.kind)}</Text>
          </Group>
          {reason && (
            <Alert
              color={data.status === "dead" ? "red" : "yellow"}
              variant="light"
              title={t(data.skipReason ? "notifications:delivery.detail.skipReason" : "notifications:delivery.detail.errorCode")}
              role="alert"
            >
              {reason}
              {data.errorCode && (
                <Text size="xs" c="dimmed" ff="monospace">
                  {data.errorCode}
                </Text>
              )}
              {data.skipReason && (
                <Text size="xs" c="dimmed" ff="monospace">
                  {data.skipReason}
                </Text>
              )}
            </Alert>
          )}
          <Row label={t("notifications:delivery.columns.channel")}>{t(`notifications:channels.${data.channel}`)}</Row>
          <Row label={t("notifications:delivery.columns.attempts")}>{data.attempts}</Row>
          <Row label={t("notifications:delivery.columns.createdAt")}>{when(data.createdAt)}</Row>
          <Row label={t("notifications:delivery.columns.lastAttemptAt")}>{when(data.lastAttemptAt)}</Row>
          {data.nextAttemptAt && <Row label={t("notifications:delivery.detail.nextAttemptAt")}>{when(data.nextAttemptAt)}</Row>}
          {data.sentAt && <Row label={t("notifications:delivery.detail.sentAt")}>{when(data.sentAt)}</Row>}
          <Alert color="blue" variant="light" icon={<Info size={16} />}>
            {t("notifications:delivery.detail.noPersonalData")}
          </Alert>
          <Text size="sm" fw={600}>
            {t("notifications:delivery.detail.payload")}
          </Text>
          <Code block aria-label={t("notifications:delivery.detail.payload")} data-testid="delivery-payload">
            {JSON.stringify(deliveryPayload(data), null, 2)}
          </Code>
        </Stack>
      )}
    </Drawer>
  );
}
