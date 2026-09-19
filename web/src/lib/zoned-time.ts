/**
 * Calendar maths in an IANA time zone (the organization's), independent of the browser's zone:
 * "today" boundaries for activity filters, `datetime-local` <-> ISO conversion and report ranges.
 */

export interface Ymd {
  year: number;
  month: number;
  day: number;
}

function safeZone(timeZone: string | undefined): string {
  const fallback = Intl.DateTimeFormat().resolvedOptions().timeZone;
  if (!timeZone) return fallback;
  try {
    new Intl.DateTimeFormat("en-US", { timeZone });
    return timeZone;
  } catch {
    return fallback;
  }
}

interface ZonedParts extends Ymd {
  hour: number;
  minute: number;
  second: number;
}

function partsInZone(date: Date, timeZone: string): ZonedParts {
  const formatted = new Intl.DateTimeFormat("en-US", {
    timeZone,
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "numeric",
    second: "numeric",
    hourCycle: "h23",
  }).formatToParts(date);
  const get = (type: string) => Number(formatted.find((p) => p.type === type)?.value ?? 0);
  return {
    year: get("year"),
    month: get("month"),
    day: get("day"),
    hour: get("hour"),
    minute: get("minute"),
    second: get("second"),
  };
}

/** Offset of the zone from UTC at `date`, in ms (positive east of Greenwich). */
function offsetMs(date: Date, timeZone: string): number {
  const p = partsInZone(date, timeZone);
  const asUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second);
  return asUtc - Math.floor(date.getTime() / 1000) * 1000;
}

/** The instant at which the wall clock of `timeZone` reads the given date and time. */
export function zonedInstant(
  { year, month, day }: Ymd,
  hour: number,
  minute: number,
  timeZone: string | undefined
): Date {
  const zone = safeZone(timeZone);
  const wall = Date.UTC(year, month - 1, day, hour, minute, 0, 0);
  let result = wall - offsetMs(new Date(wall), zone);
  // Around a DST change the first guess can be an hour off; one correction settles it.
  const corrected = wall - offsetMs(new Date(result), zone);
  if (corrected !== result) result = corrected;
  return new Date(result);
}

/** Calendar date of `date` in `timeZone`. */
export function ymdInZone(date: Date, timeZone: string | undefined): Ymd {
  const { year, month, day } = partsInZone(date, safeZone(timeZone));
  return { year, month, day };
}

const pad = (n: number, width = 2) => String(n).padStart(width, "0");

/** `YYYY-MM-DD`. */
export function formatYmd({ year, month, day }: Ymd): string {
  return `${pad(year, 4)}-${pad(month)}-${pad(day)}`;
}

/** First day of the month `monthsBack` months before the month of `ymd`. */
export function startOfMonthBack(ymd: Ymd, monthsBack: number): Ymd {
  const index = ymd.year * 12 + (ymd.month - 1) - monthsBack;
  return { year: Math.floor(index / 12), month: (index % 12) + 1, day: 1 };
}

/** UTC ISO bounds of the calendar day of `now` in `timeZone` (`to` is the last millisecond). */
export function todayBoundsIso(
  timeZone: string | undefined,
  now: Date = new Date()
): { from: string; to: string } {
  const today = ymdInZone(now, timeZone);
  const start = zonedInstant(today, 0, 0, timeZone);
  const nextDay = new Date(Date.UTC(today.year, today.month - 1, today.day + 1));
  const nextStart = zonedInstant(
    { year: nextDay.getUTCFullYear(), month: nextDay.getUTCMonth() + 1, day: nextDay.getUTCDate() },
    0,
    0,
    timeZone
  );
  return { from: start.toISOString(), to: new Date(nextStart.getTime() - 1).toISOString() };
}

/** ISO instant -> `YYYY-MM-DDTHH:mm` (the value of an `<input type="datetime-local">`) in `timeZone`. */
export function toZonedInput(iso: string | undefined, timeZone: string | undefined): string {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const p = partsInZone(date, safeZone(timeZone));
  return `${formatYmd(p)}T${pad(p.hour)}:${pad(p.minute)}`;
}

/** `YYYY-MM-DDTHH:mm` wall-clock value in `timeZone` -> UTC ISO string; empty/invalid -> undefined. */
export function fromZonedInput(value: string, timeZone: string | undefined): string | undefined {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value.trim());
  if (!match) return undefined;
  const [, y, mo, d, h, mi] = match.map(Number) as [number, number, number, number, number, number];
  return zonedInstant({ year: y, month: mo, day: d }, h, mi, timeZone).toISOString();
}
