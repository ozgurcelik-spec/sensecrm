import type { Locale } from "@/types";

export const SUPPORTED_LOCALES: readonly Locale[] = ["tr", "en"];
export const DEFAULT_LOCALE: Locale = "tr";

/** Narrows any language tag ("en-US", "tr", undefined) to a supported locale. */
export function toLocale(value: string | null | undefined): Locale {
  const short = (value ?? "").slice(0, 2).toLowerCase();
  return (SUPPORTED_LOCALES as readonly string[]).includes(short)
    ? (short as Locale)
    : DEFAULT_LOCALE;
}
