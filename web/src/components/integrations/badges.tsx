import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import {
  API_KEY_STATUS_COLOR,
  DELIVERY_STATUS_COLOR,
  HEALTH_COLOR,
} from "@/lib/integrations";
import type { ApiKeyStatus, WebhookDeliveryStatus, WebhookHealth } from "@/types";

/** healthy / degraded / failing / disabled of a webhook subscription. */
export function HealthBadge({ health }: { health: WebhookHealth }) {
  const { t } = useTranslation(["integrations"]);
  return (
    <Badge variant="light" color={HEALTH_COLOR[health] ?? "gray"} data-testid={`health-${health}`}>
      {t(`integrations:health.${health}`, { defaultValue: health })}
    </Badge>
  );
}

/** pending / delivering / succeeded / failed of a delivery. */
export function DeliveryStatusBadge({ status }: { status: WebhookDeliveryStatus }) {
  const { t } = useTranslation(["integrations"]);
  return (
    <Badge variant="light" color={DELIVERY_STATUS_COLOR[status] ?? "gray"} data-testid={`delivery-status-${status}`}>
      {t(`integrations:deliveries.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** active / expired / revoked of an API key. */
export function ApiKeyStatusBadge({ status }: { status: ApiKeyStatus }) {
  const { t } = useTranslation(["integrations"]);
  return (
    <Badge variant="light" color={API_KEY_STATUS_COLOR[status] ?? "gray"} data-testid={`key-status-${status}`}>
      {t(`integrations:apiKeys.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** "API anahtarıyla" marker on an audit row that was written with an API key (`apiKeyId`). */
export function ApiKeyAuditBadge({ apiKeyId }: { apiKeyId: string | null | undefined }) {
  const { t } = useTranslation(["integrations"]);
  if (!apiKeyId) return null;
  return (
    <Badge variant="outline" color="grape" size="sm" data-testid="audit-api-key-badge">
      {t("integrations:audit.viaApiKey")}
    </Badge>
  );
}
