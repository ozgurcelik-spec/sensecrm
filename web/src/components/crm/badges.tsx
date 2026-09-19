import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import { stageColor } from "@/lib/stage";
import type { LeadRating, LeadStatus, StageKind } from "@/types";

const LEAD_STATUS_COLOR: Record<LeadStatus, string> = {
  new: "blue",
  contacted: "cyan",
  qualified: "green",
  unqualified: "gray",
  converted: "violet",
};

export function LeadStatusBadge({ status }: { status: LeadStatus }) {
  const { t } = useTranslation(["crm"]);
  return (
    <Badge variant="light" color={LEAD_STATUS_COLOR[status] ?? "gray"}>
      {t(`crm:leads.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

const RATING_COLOR: Record<LeadRating, string> = { hot: "red", warm: "orange", cold: "blue" };

export function LeadRatingBadge({ rating }: { rating?: LeadRating }) {
  const { t } = useTranslation(["crm"]);
  if (!rating) return <>-</>;
  return (
    <Badge variant="outline" color={RATING_COLOR[rating] ?? "gray"}>
      {t(`crm:leads.ratings.${rating}`, { defaultValue: rating })}
    </Badge>
  );
}

export function StageBadge({ name, kind }: { name: string; kind: StageKind }) {
  return (
    <Badge variant="light" color={stageColor(kind)}>
      {name}
    </Badge>
  );
}
