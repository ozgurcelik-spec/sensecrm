import type { TFunction } from "i18next";
import { formatCalendarDate } from "@/lib/format";
import type {
  AddMembersResult,
  CampaignStatus,
  CampaignType,
  CampaignMemberStatus,
  SetMembersStatusResult,
} from "@/types";

export const CAMPAIGN_STATUS_COLOR: Record<CampaignStatus, string> = {
  planned: "gray",
  active: "green",
  completed: "blue",
  cancelled: "red",
};

export const CAMPAIGN_TYPE_COLOR: Record<CampaignType, string> = {
  email: "blue",
  event: "grape",
  webinar: "cyan",
  advertising: "orange",
  other: "gray",
};

export const MEMBER_STATUS_COLOR: Record<CampaignMemberStatus, string> = {
  added: "gray",
  sent: "blue",
  responded: "teal",
  converted: "violet",
  unsubscribed: "red",
};

/** Start - end of a campaign, or a single date when only one is set ("-" when none). */
export function formatCampaignDates(campaign: {
  startDate?: string;
  endDate?: string;
}): string {
  const { startDate, endDate } = campaign;
  if (startDate && endDate) {
    return `${formatCalendarDate(startDate)} - ${formatCalendarDate(endDate)}`;
  }
  return formatCalendarDate(startDate ?? endDate);
}

/** Comma separated URL value -> list (empty entries dropped). */
export function splitList(value: string | undefined | null): string[] {
  return value ? value.split(",").filter(Boolean) : [];
}

/** Result toast of a bulk add: "3 added, 1 was already a member, 2 skipped (1 converted, 1 not found)". */
export function summarizeAddResult(result: AddMembersResult, t: TFunction): string {
  const parts: string[] = [];
  if (result.addedCount > 0) parts.push(t("campaigns:result.added", { count: result.addedCount }));
  if (result.alreadyMemberCount > 0) {
    parts.push(t("campaigns:result.already", { count: result.alreadyMemberCount }));
  }
  if (result.skipped.length > 0) {
    const converted = result.skipped.filter((s) => s.reason === "lead_converted").length;
    const missing = result.skipped.filter((s) => s.reason === "not_found").length;
    const reasons = [
      converted > 0 ? t("campaigns:result.skippedConverted", { count: converted }) : "",
      missing > 0 ? t("campaigns:result.skippedNotFound", { count: missing }) : "",
    ].filter(Boolean);
    parts.push(
      t("campaigns:result.skipped", { count: result.skipped.length, reasons: reasons.join(", ") })
    );
  }
  return parts.length > 0 ? parts.join(", ") : t("campaigns:result.nothing");
}

/** Result toast of a bulk status change. */
export function summarizeStatusResult(result: SetMembersStatusResult, t: TFunction): string {
  const parts = [t("campaigns:result.updated", { count: result.updatedCount })];
  if (result.skippedCount > 0) {
    parts.push(t("campaigns:result.lockedSkipped", { count: result.skippedCount }));
  }
  return parts.join(", ");
}
