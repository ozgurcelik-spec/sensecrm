/** Pure helpers of the notification preference matrix (M8A). */
import type {
  NotificationChannel,
  NotificationDelivery,
  NotificationPreferenceCell,
  NotificationPreferences,
} from "@/types";

/** Draft cell key. */
export const cellKey = (kind: string, channel: NotificationChannel) => `${kind}|${channel}`;

/** Draft value if the user touched the cell, otherwise what the server has. */
export const cellValue = (
  draft: Readonly<Record<string, boolean>>,
  kind: string,
  channel: NotificationChannel,
  cell: NotificationPreferenceCell
) => draft[cellKey(kind, channel)] ?? cell.enabled;

/** A cell the user can change: not locked, the kind uses the channel, the channel is usable. */
export function isCellEditable(
  preferences: NotificationPreferences,
  channel: NotificationChannel,
  cell: NotificationPreferenceCell
): boolean {
  return !cell.locked && cell.supported !== false && preferences.channels[channel].available;
}

/**
 * The raw delivery record as the viewer shows it. Everything that could identify a person (name,
 * masked address, user id) is left out, and the server sends no subject, body or full address, so
 * the JSON is safe to copy into a support ticket.
 */
export function deliveryPayload(delivery: NotificationDelivery): Record<string, unknown> {
  const payload: Record<string, unknown> = { ...delivery };
  delete payload.recipientName;
  delete payload.addressMasked;
  delete payload.recipientUserId;
  return payload;
}
