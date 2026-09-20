/**
 * Price book helpers (docs/plan/m9c-envanter.md, "Fiyat listeleri"). The server resolves the real
 * price; this mirrors its `flat` rule only for the "Örnek çözüm" preview on the price book page.
 */
import { roundHalfUpDiv, toScaledInt, toSignedMinor } from "@/lib/commerce-totals";

/** `Round4(catalogPrice x (1 + percent / 100))`, half up: 19.99 @ -10 -> 17.991, 0.0001 @ +50 -> 0.0002. */
export function flatPrice(catalogPrice: number | string, percent: number | string): number | null {
  const percentMinor = toSignedMinor(percent);
  if (percentMinor === null) return null;
  const catalog = toScaledInt(catalogPrice, 4);
  const factor = 10_000n + percentMinor; // 1 + percent / 100 in 1e-4 steps
  if (factor < 0n) return null;
  return Number(roundHalfUpDiv(catalog * factor, 10_000n)) / 10_000;
}
