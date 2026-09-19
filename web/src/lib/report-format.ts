import { intlLocale } from "@/lib/dates";
import type { FunnelStage } from "@/types";

const MONTH = /^(\d{4})-(\d{2})$/;

/** "2026-09" -> "Eyl 2026" (UI language); a week period such as "2026-W38" is shown as is. */
export function formatPeriod(period: string): string {
  const match = MONTH.exec(period);
  if (!match) return period;
  const [, year, month] = match;
  return new Intl.DateTimeFormat(intlLocale(), {
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(Date.UTC(Number(year), Number(month) - 1, 1)));
}

/** Share of `part` in `total` as a 0..1 fraction (0 when there is nothing to divide by). */
export function ratio(part: number, total: number): number {
  return total > 0 ? part / total : 0;
}

/** 0.256 -> "%25,6" / "25.6%" through Intl. */
export function formatPercent(fraction: number): string {
  return new Intl.NumberFormat(intlLocale(), {
    style: "percent",
    maximumFractionDigits: 1,
  }).format(fraction);
}

const OPEN_STAGE_SHADES = ["blue.9", "blue.8", "blue.7", "blue.6", "blue.5", "blue.4", "blue.3"];

export interface FunnelCell {
  key: string;
  name: string;
  value: number;
  color: string;
}

/** Funnel segments: the open stages (darker to lighter blue) followed by won; lost is not a funnel step. */
export function toFunnelCells(stages: readonly FunnelStage[]): FunnelCell[] {
  return stages
    .filter((s) => s.kind !== "lost")
    .map((s, index) => ({
      key: s.id,
      name: s.name,
      value: s.count,
      color:
        s.kind === "won"
          ? "green.6"
          : (OPEN_STAGE_SHADES[index % OPEN_STAGE_SHADES.length] as string),
    }));
}

/** Colors of the lead source segments. */
export const SOURCE_COLORS = [
  "blue.6",
  "teal.6",
  "violet.6",
  "orange.6",
  "gray.6",
  "pink.6",
  "cyan.6",
];
