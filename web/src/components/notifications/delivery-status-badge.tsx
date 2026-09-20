import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import type { NotificationDeliveryStatus } from "@/types";

const COLOR: Record<NotificationDeliveryStatus, string> = {
  pending: "gray",
  sending: "blue",
  sent: "green",
  dead: "red",
  skipped: "yellow",
};

/** Delivery status: waiting, sending, sent, dead (gave up) or skipped. */
export function DeliveryStatusBadge({ status }: { status: NotificationDeliveryStatus | string }) {
  const { t } = useTranslation(["notifications"]);
  return (
    <Badge variant="light" color={COLOR[status as NotificationDeliveryStatus] ?? "gray"}>
      {t(`notifications:delivery.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}
