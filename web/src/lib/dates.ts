import i18n from "@/i18n";

/** BCP 47 tag used for Intl formatting of the active UI language. */
export function intlLocale(language: string = i18n.language): string {
  return language?.startsWith("en") ? "en-US" : "tr-TR";
}

function toDate(value: string | Date): Date | null {
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** Date + time in the UI language, optionally in the organization's time zone. */
export function formatDateTime(value: string | Date, timeZone?: string): string {
  const date = toDate(value);
  if (!date) return "";
  try {
    return new Intl.DateTimeFormat(intlLocale(), {
      dateStyle: "medium",
      timeStyle: "short",
      timeZone,
    }).format(date);
  } catch {
    // Unknown time zone id from the server - fall back to the browser's zone.
    return new Intl.DateTimeFormat(intlLocale(), {
      dateStyle: "medium",
      timeStyle: "short",
    }).format(date);
  }
}

/** Date only in the UI language. */
export function formatDate(value: string | Date, timeZone?: string): string {
  const date = toDate(value);
  if (!date) return "";
  try {
    return new Intl.DateTimeFormat(intlLocale(), { dateStyle: "medium", timeZone }).format(date);
  } catch {
    return new Intl.DateTimeFormat(intlLocale(), { dateStyle: "medium" }).format(date);
  }
}
