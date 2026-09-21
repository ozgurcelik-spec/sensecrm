/**
 * Small pure helpers of the notification screens (M8A): safe deep links, badge text, relative time,
 * polling jitter and the friendly text of the `notification.*` error codes.
 */
import axios from "axios";
import i18n from "@/i18n";
import { getApiErrorMessage, getApiProblem } from "@/lib/api-error";
import { intlLocale } from "@/lib/dates";
import {
  NOTIFICATION_KINDS,
  type NotificationChannel,
  type NotificationDelivery,
  type NotificationPreferenceKind,
} from "@/types";

export const NOTIFICATION_MANDATORY_PREFERENCE = "notification.mandatory_preference";
export const NOTIFICATION_EMAIL_NOT_CONFIGURED = "notification.email_not_configured";
export const NOTIFICATION_DELIVERY_NOT_RETRYABLE = "notification.delivery_not_retryable";
export const RATE_LIMIT_EXCEEDED = "general.rate_limit_exceeded";

/** Unread badge: the server cuts the count at 1000, the badge shows `99+` from 100. */
export function badgeLabel(count: number): string {
  return count > 99 ? "99+" : String(count);
}

/**
 * A notification's `link` is only followed when it is a relative in-app path (`/app/...`): anything
 * else (absolute URL, protocol-relative `//host`, other paths, backslashes) is ignored, so a bad
 * server value can never turn into an open redirect.
 */
export function safeNotificationLink(link: string | undefined | null): string | undefined {
  if (!link || (!link.startsWith("/app/") && link !== "/app")) return undefined;
  if (link.includes("\\") || link.includes("//")) return undefined;
  for (let i = 0; i < link.length; i++) {
    if (link.charCodeAt(i) < 0x20) return undefined;
  }
  return link;
}

/** Poll every 60 s with a +-10 % random offset so many open tabs do not hit the API in lockstep. */
export const NOTIFICATION_POLL_INTERVAL_MS = 60_000;
export function notificationPollDelay(random: () => number = Math.random): number {
  return Math.round(NOTIFICATION_POLL_INTERVAL_MS * (1 + (random() - 0.5) * 0.2));
}

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ["day", 86_400_000],
  ["hour", 3_600_000],
  ["minute", 60_000],
];

/** "5 dakika önce" / "5 minutes ago" (whole units, falls back to the date for old items). */
export function formatRelativeTime(value: string, now: number = Date.now()): string {
  const time = new Date(value).getTime();
  if (Number.isNaN(time)) return "";
  const diff = time - now;
  const formatter = new Intl.RelativeTimeFormat(intlLocale(), { numeric: "auto" });
  const abs = Math.abs(diff);
  if (abs < 60_000) return formatter.format(0, "second");
  for (const [unit, size] of RELATIVE_UNITS) {
    if (abs >= size) {
      const amount = Math.trunc(diff / size);
      if (unit === "day" && Math.abs(amount) > 30) break;
      return formatter.format(amount, unit);
    }
  }
  return new Intl.DateTimeFormat(intlLocale(), { dateStyle: "medium" }).format(new Date(time));
}

/** Label of a kind in the user's language (unknown kinds show their raw key). */
export function kindLabel(kind: string): string {
  return i18n.t(`notifications:kinds.${kind}.label`, { defaultValue: kind });
}

export function isKnownKind(kind: string): boolean {
  return (NOTIFICATION_KINDS as readonly string[]).includes(kind);
}

export function channelLabel(channel: NotificationChannel | string): string {
  return i18n.t(`notifications:channels.${channel}`, { defaultValue: channel });
}

/** Groups (in catalog order) of the preference kinds; a group appears once. */
export function groupPreferenceKinds(
  kinds: readonly NotificationPreferenceKind[]
): { group: string; kinds: NotificationPreferenceKind[] }[] {
  const groups: { group: string; kinds: NotificationPreferenceKind[] }[] = [];
  for (const kind of kinds) {
    const existing = groups.find((entry) => entry.group === kind.group);
    if (existing) existing.kinds.push(kind);
    else groups.push({ group: kind.group, kinds: [kind] });
  }
  return groups;
}

/**
 * Text of an error of the notification endpoints. `notification.mandatory_preference` names the kind
 * and channel the server refused; everything else goes through the shared translation.
 */
export function notificationErrorMessage(error: unknown): string {
  const problem = getApiProblem(error);
  if (problem?.code === NOTIFICATION_MANDATORY_PREFERENCE) {
    const kind = typeof problem.args?.kind === "string" ? problem.args.kind : undefined;
    const channel = typeof problem.args?.channel === "string" ? problem.args.channel : undefined;
    if (kind && channel) {
      return i18n.t("notifications:errors.mandatoryNamed", {
        kind: kindLabel(kind),
        channel: channelLabel(channel),
      });
    }
  }
  return getApiErrorMessage(error);
}

/** Seconds a rate-limited caller should wait (`Retry-After` header), or `fallback` when the server sent none. */
export function retryAfterSeconds(error: unknown, fallback = 60): number {
  if (axios.isAxiosError(error)) {
    const header = error.response?.headers?.["retry-after"] as unknown;
    const seconds = typeof header === "string" || typeof header === "number" ? Number(header) : NaN;
    if (Number.isFinite(seconds) && seconds > 0) return Math.ceil(seconds);
  }
  return fallback;
}

/** Catalog group of a kind (`case.assigned` -> `service`); used to group the kind filter. */
export function kindGroup(kind: string): string {
  switch (kind.split(".")[0]) {
    case "approval":
      return "approvals";
    case "case":
      return "service";
    case "activity":
      return "activities";
    case "lead":
      return "sales";
    case "quote":
      return "commerce";
    default:
      return "account";
  }
}

export function groupLabel(group: string): string {
  return i18n.t(`notifications:groups.${group}`, { defaultValue: group });
}

/** Error / skip code of a delivery as words. */
export function deliveryReasonText(delivery: Pick<NotificationDelivery, "errorCode" | "skipReason">): string | undefined {
  if (delivery.skipReason) {
    return i18n.t(`notifications:delivery.skipReasons.${delivery.skipReason}`, {
      defaultValue: delivery.skipReason,
    });
  }
  if (delivery.errorCode) {
    return i18n.t(`notifications:delivery.errorCodes.${delivery.errorCode}`, {
      defaultValue: delivery.errorCode,
    });
  }
  return undefined;
}
