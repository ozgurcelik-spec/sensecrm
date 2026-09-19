import { intlLocale } from "@/lib/dates";

/** Trims a form string; empty becomes `undefined` so it is left out of the request body. */
export function blankToUndefined(value: string | undefined | null): string | undefined {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

/** Amount with its ISO 4217 currency in the UI language (falls back to "1,234.00 XXX" for unknown codes). */
export function formatMoney(amount: number | undefined | null, currency = "TRY"): string {
  if (amount === undefined || amount === null) return "-";
  try {
    return new Intl.NumberFormat(intlLocale(), {
      style: "currency",
      currency,
      maximumFractionDigits: 2,
    }).format(amount);
  } catch {
    return `${new Intl.NumberFormat(intlLocale()).format(amount)} ${currency}`;
  }
}

/** Plain number in the UI language (counts, percentages). */
export function formatNumber(value: number): string {
  return new Intl.NumberFormat(intlLocale()).format(value);
}

/** `YYYY-MM-DD` (calendar date without time zone) shown as a medium date, without any zone shift. */
export function formatCalendarDate(value: string | undefined | null): string {
  if (!value) return "-";
  const [year, month, day] = value.slice(0, 10).split("-").map(Number);
  if (!year || !month || !day) return value;
  return new Intl.DateTimeFormat(intlLocale(), { dateStyle: "medium", timeZone: "UTC" }).format(
    new Date(Date.UTC(year, month - 1, day))
  );
}

/** Text of an optional field for read-only panels. */
export function orDash(value: string | undefined | null): string {
  return value?.trim() ? value : "-";
}

/** Formats `address` on one line (street, city, state, postal code, country). */
export function formatAddress(
  address:
    | { street?: string; city?: string; state?: string; postalCode?: string; country?: string }
    | undefined
): string {
  if (!address) return "-";
  const parts = [address.street, address.city, address.state, address.postalCode, address.country]
    .map((p) => p?.trim())
    .filter(Boolean);
  return parts.length > 0 ? parts.join(", ") : "-";
}
