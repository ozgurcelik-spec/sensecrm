import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Badge, Button, Drawer, Group, Skeleton, Stack, Table, Text } from "@mantine/core";
import { RotateCcw } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { useWebhookDelivery } from "@/hooks/use-integrations";
import { formatDateTime } from "@/lib/dates";
import { canRedeliver, eventTypeLabel, failureReasonText, isSnippetTruncated } from "@/lib/integrations";
import { useAuthStore } from "@/store/auth.store";
import { DeliveryStatusBadge } from "./badges";
import { CodeBlock } from "./code-block";
import { RedeliverConfirm } from "./redeliver-confirm";

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Group justify="space-between" wrap="nowrap" align="flex-start" gap="lg">
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      <Text size="sm" ta="right" style={{ wordBreak: "break-all" }}>
        {children}
      </Text>
    </Group>
  );
}

/**
 * Detail of one delivery: the signed envelope (redacted JSON viewer), the sent headers
 * (`X-Crm-Signature` is `[redacted]` by the server), the attempt timeline with the (truncated)
 * response snippet and "Yeniden gönder" for finished deliveries.
 */
export function DeliveryDetailDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation(["integrations"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { data, isLoading, error, refetch } = useWebhookDelivery(id);
  const [redeliverId, setRedeliverId] = useState<string | undefined>();
  const when = (value?: string) => (value ? formatDateTime(value, timeZone) : "-");
  const reason = data ? failureReasonText(data.failureReason) : undefined;

  return (
    <Drawer opened onClose={onClose} position="right" size="lg" title={t("integrations:deliveries.detail.title")}>
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading || !data ? (
        <Skeleton h={240} />
      ) : (
        <Stack gap="sm" data-testid="delivery-detail">
          <Group justify="space-between">
            <Group gap="xs">
              <DeliveryStatusBadge status={data.status} />
              <Badge variant="outline" color="gray" size="sm">
                {t(`integrations:deliveries.kinds.${data.kind}`, { defaultValue: data.kind })}
              </Badge>
            </Group>
            <Text size="sm">{eventTypeLabel(data.eventType)}</Text>
          </Group>
          {data.status === "failed" && (
            <Alert color="red" variant="light" title={t("integrations:deliveries.detail.failure")} role="alert">
              {reason ?? "-"}
              {data.failureReason && (
                <Text size="xs" c="dimmed" ff="monospace">
                  {data.failureReason}
                </Text>
              )}
            </Alert>
          )}
          <Row label={t("integrations:deliveries.columns.subscription")}>{data.subscriptionName}</Row>
          <Row label={t("integrations:deliveries.detail.host")}>{data.host}</Row>
          <Row label={t("integrations:deliveries.detail.eventId")}>
            <Text span ff="monospace" size="xs">
              {data.eventId}
            </Text>
          </Row>
          <Row label={t("integrations:deliveries.columns.attempts")}>
            {data.attempts}/{data.maxAttempts}
          </Row>
          <Row label={t("integrations:deliveries.columns.createdAt")}>{when(data.createdAt)}</Row>
          <Row label={t("integrations:deliveries.detail.lastAttemptAt")}>{when(data.lastAttemptAt)}</Row>
          {data.nextAttemptAt && <Row label={t("integrations:deliveries.columns.nextAttempt")}>{when(data.nextAttemptAt)}</Row>}
          {data.completedAt && <Row label={t("integrations:deliveries.detail.completedAt")}>{when(data.completedAt)}</Row>}
          <Row label={t("integrations:deliveries.columns.http")}>{data.responseStatus ?? "-"}</Row>

          {canRedeliver(data.status) && (
            <Group>
              <Button
                variant="default"
                leftSection={<RotateCcw size={16} />}
                onClick={() => setRedeliverId(data.id)}
              >
                {t("integrations:deliveries.redeliver")}
              </Button>
            </Group>
          )}

          <CodeBlock
            code={JSON.stringify(data.payload, null, 2)}
            label={t("integrations:deliveries.detail.payload")}
            testId="delivery-payload"
          />

          <Text size="sm" fw={600} mt="xs">
            {t("integrations:deliveries.detail.headers")}
          </Text>
          <Table fz="xs" verticalSpacing={4} data-testid="delivery-headers">
            <Table.Tbody>
              {Object.entries(data.requestHeaders).map(([name, value]) => (
                <Table.Tr key={name}>
                  <Table.Td fw={600} style={{ whiteSpace: "nowrap" }}>
                    {name}
                  </Table.Td>
                  <Table.Td ff="monospace" style={{ wordBreak: "break-all" }}>
                    {value}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>

          <Text size="sm" fw={600} mt="xs">
            {t("integrations:deliveries.detail.attemptLog")}
          </Text>
          {data.attemptLog.length === 0 ? (
            <Text size="sm" c="dimmed">
              {t("integrations:deliveries.detail.noAttempts")}
            </Text>
          ) : (
            <Stack gap="sm" data-testid="attempt-log">
              {data.attemptLog.map((attempt) => (
                <Stack key={attempt.attemptNo} gap={4} data-testid={`attempt-${attempt.attemptNo}`}>
                  <Group gap="xs" wrap="wrap">
                    <Badge size="sm" variant="light" color={attempt.failureReason ? "red" : "green"}>
                      {t("integrations:deliveries.detail.attemptNo", { n: attempt.attemptNo })}
                    </Badge>
                    <Text size="xs" c="dimmed">
                      {when(attempt.startedAt)}
                    </Text>
                    {attempt.responseStatus !== undefined && (
                      <Text size="xs">
                        {t("integrations:deliveries.columns.http")}: {attempt.responseStatus}
                      </Text>
                    )}
                    {attempt.durationMs !== undefined && <Text size="xs">{attempt.durationMs} ms</Text>}
                    {attempt.failureReason && (
                      <Text size="xs" c="red">
                        {failureReasonText(attempt.failureReason)}
                      </Text>
                    )}
                  </Group>
                  {attempt.errorDetail && (
                    <Text size="xs" c="dimmed">
                      {attempt.errorDetail}
                    </Text>
                  )}
                  {attempt.responseSnippet && (
                    <>
                      <CodeBlock
                        code={attempt.responseSnippet}
                        label={t("integrations:deliveries.detail.responseSnippet")}
                        testId={`snippet-${attempt.attemptNo}`}
                      />
                      {isSnippetTruncated(attempt.responseSnippet) && (
                        <Text size="xs" c="dimmed">
                          {t("integrations:deliveries.detail.truncated")}
                        </Text>
                      )}
                    </>
                  )}
                </Stack>
              ))}
            </Stack>
          )}
        </Stack>
      )}
      <RedeliverConfirm deliveryId={redeliverId} onClose={() => setRedeliverId(undefined)} />
    </Drawer>
  );
}
