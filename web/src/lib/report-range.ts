import { formatYmd, startOfMonthBack, ymdInZone } from "@/lib/zoned-time";

export const RANGE_PRESETS = ["thisMonth", "last3Months", "last12Months", "custom"] as const;
export type RangePreset = (typeof RANGE_PRESETS)[number];

export const DEFAULT_RANGE_PRESET: RangePreset = "last12Months";

export interface DateRange {
  from: string;
  to: string;
}

const MONTHS_BACK: Record<Exclude<RangePreset, "custom">, number> = {
  thisMonth: 0,
  last3Months: 2,
  last12Months: 11,
};

const YMD = /^\d{4}-\d{2}-\d{2}$/;

export function isRangePreset(value: string | null | undefined): value is RangePreset {
  return !!value && (RANGE_PRESETS as readonly string[]).includes(value);
}

/**
 * Inclusive `from`/`to` (`YYYY-MM-DD`, the organization's calendar) of a preset. Presets cover whole
 * calendar months up to today: "last 3 months" is the current month plus the two before it.
 * `custom` uses the given dates; it returns null until both are valid and ordered.
 */
export function resolveRange(
  preset: RangePreset,
  timeZone: string | undefined,
  custom: { from?: string | null; to?: string | null } = {},
  now: Date = new Date()
): DateRange | null {
  if (preset === "custom") {
    const { from, to } = custom;
    if (!from || !to || !YMD.test(from) || !YMD.test(to) || from > to) return null;
    return { from, to };
  }
  const today = ymdInZone(now, timeZone);
  return { from: formatYmd(startOfMonthBack(today, MONTHS_BACK[preset])), to: formatYmd(today) };
}

/** First day of the month five months ago through today: the dashboard's "last 6 months". */
export function lastSixMonthsRange(
  timeZone: string | undefined,
  now: Date = new Date()
): DateRange {
  const today = ymdInZone(now, timeZone);
  return { from: formatYmd(startOfMonthBack(today, 5)), to: formatYmd(today) };
}
