import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import type { OrderStatus, QuoteStatus } from "@/types";

const QUOTE_COLOR: Record<QuoteStatus, string> = {
  draft: "gray",
  sent: "blue",
  accepted: "green",
  rejected: "red",
  // Own colour: an expired quote needs attention, it is neither open nor lost.
  expired: "orange",
};

const ORDER_COLOR: Record<OrderStatus, string> = {
  draft: "gray",
  confirmed: "blue",
  fulfilled: "green",
  cancelled: "red",
};

export function QuoteStatusBadge({ status }: { status: QuoteStatus }) {
  const { t } = useTranslation(["commerce"]);
  return (
    <Badge variant="light" color={QUOTE_COLOR[status] ?? "gray"}>
      {t(`commerce:quoteStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  const { t } = useTranslation(["commerce"]);
  return (
    <Badge variant="light" color={ORDER_COLOR[status] ?? "gray"}>
      {t(`commerce:orderStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}
