/**
 * Live total preview of a quote / order (docs/plan/m6a-ticaret.md, "Toplam hesabı").
 *
 * Same algorithm as the server's `DocumentTotals`, done with BigInt scaled integers so there is no
 * floating point drift: quantity and unit price are scaled by 1e4, percentages by 1e2, amounts are
 * kept in minor units (1e2). Rounding is half up (away from zero; every value here is >= 0) per line,
 * and the document totals are sums of the rounded line values. The preview is display only: after
 * saving, the values the server returns are the ones shown.
 */

const QTY_SCALE = 4;
const PRICE_SCALE = 4;
const PERCENT_SCALE = 2;

export interface TotalsLineInput {
  quantity: number | string | null | undefined;
  unitPrice: number | string | null | undefined;
  discountPercent: number | string | null | undefined;
  taxRate: number | string | null | undefined;
}

export interface LineTotals {
  lineSubtotal: number;
  lineDiscount: number;
  lineTax: number;
  lineTotal: number;
}

export interface DocumentTotals {
  lines: LineTotals[];
  subtotal: number;
  discountTotal: number;
  taxTotal: number;
  /** The signed rounding line (M9C), added after tax; 0 when unused or invalid. */
  adjustment: number;
  /** Always `sum of the line totals + adjustment` (`subtotal - discountTotal + taxTotal + adjustment`). */
  grandTotal: number;
}

/** Decimal value as a scaled integer (`19.99`, 4 -> 199900n). Invalid, negative or empty input is 0. */
export function toScaledInt(value: number | string | null | undefined, scale: number): bigint {
  const number = typeof value === "number" ? value : Number.parseFloat(value ?? "");
  if (!Number.isFinite(number) || number <= 0) return 0n;
  // toFixed rounds to `scale` decimals and never uses exponent notation below 1e21.
  const fixed = number < 1e21 ? number.toFixed(scale) : "0";
  return BigInt(fixed.replace(".", ""));
}

/** `a / d` rounded half up, for `a >= 0` and `d > 0`: `(2a + d) / 2d` with integer division. */
export function roundHalfUpDiv(a: bigint, d: bigint): bigint {
  return (2n * a + d) / (2n * d);
}

const pow10 = (n: number): bigint => 10n ** BigInt(n);

/** Upper bound of `|adjustment|` (contract: 1.000.000.000). */
export const MAX_ADJUSTMENT_MINOR = 100_000_000_000n;

/**
 * Signed decimal (`-0.56`, `"+0.44"`) as minor units (1e-2). `toScaledInt` turns a negative into 0, so the
 * adjustment has its own converter. Returns `null` for anything that is not a
 * plain decimal with at most two decimals (the server rejects 3 decimals with `validation.decimals`
 * instead of rounding, so the client never rounds either). Empty / blank input is 0.
 */
export function toSignedMinor(value: number | string | null | undefined): bigint | null {
  if (value === null || value === undefined) return 0n;
  let text = typeof value === "number" ? (Number.isFinite(value) ? String(value) : "") : value.trim();
  if (text === "") return typeof value === "number" ? null : 0n;
  // Exponent notation (1e-7, 1e21) never fits two decimals within the allowed range.
  if (/e/i.test(text)) {
    const number = Number(text);
    if (!Number.isFinite(number)) return null;
    if (Math.abs(number) >= 1e21 || number === 0) return number === 0 ? 0n : null;
    text = number.toFixed(20).replace(/0+$/, "").replace(/\.$/, "");
  }
  const match = /^([+-])?(\d*)(?:\.(\d*))?$/.exec(text);
  if (!match || (match[2] === "" && (match[3] ?? "") === "")) return null;
  const fraction = match[3] ?? "";
  if (fraction.length > 2 && /[1-9]/.test(fraction.slice(2))) return null;
  const digits = `${match[2] || "0"}${fraction.padEnd(2, "0").slice(0, 2)}`;
  const minor = BigInt(digits);
  return match[1] === "-" ? -minor : minor;
}

/** Whole-currency rounding line: `round(total) - total`, half up ("Yuvarla" button): 94.56 -> +0.44, 94.49 -> -0.49. */
export function roundingAdjustment(linesTotal: number): number {
  const minor = BigInt(Math.round(linesTotal * 100));
  const rounded = ((minor + 50n) / 100n) * 100n;
  return fromMinor(rounded - minor);
}

/** Minor units (bigint) as a plain number with two decimals (`6476n` -> 64.76). */
function fromMinor(minor: bigint): number {
  return Number(minor) / 100;
}

interface MinorLine {
  subtotal: bigint;
  discount: bigint;
  tax: bigint;
}

function computeLineMinor(line: TotalsLineInput): MinorLine {
  const quantity = toScaledInt(line.quantity, QTY_SCALE);
  const unitPrice = toScaledInt(line.unitPrice, PRICE_SCALE);
  const discountPercent = toScaledInt(line.discountPercent, PERCENT_SCALE);
  const taxRate = toScaledInt(line.taxRate, PERCENT_SCALE);

  // quantity x price has 8 decimals; minor units have 2.
  const subtotal = roundHalfUpDiv(quantity * unitPrice, pow10(QTY_SCALE + PRICE_SCALE - 2));
  // percent is scaled by 1e2 and divides by 100 more.
  const percentDivisor = pow10(PERCENT_SCALE + 2);
  const discount = roundHalfUpDiv(subtotal * discountPercent, percentDivisor);
  const net = subtotal - discount;
  const tax = roundHalfUpDiv(net * taxRate, percentDivisor);
  return { subtotal, discount, tax };
}

export function computeLineTotals(line: TotalsLineInput): LineTotals {
  const { subtotal, discount, tax } = computeLineMinor(line);
  return {
    lineSubtotal: fromMinor(subtotal),
    lineDiscount: fromMinor(discount),
    lineTax: fromMinor(tax),
    lineTotal: fromMinor(subtotal - discount + tax),
  };
}

export type AdjustmentIssue =
  | "decimals"
  | "max"
  | "requiresLines"
  | "negativeTotal";

/**
 * Client-side mirror of the adjustment rules (plan D3, vectors A4, A5, A7, A8): at most two decimals
 * (`validation.decimals`), `|x| <= 1.000.000.000`, not on a document without lines
 * (`validation.adjustment_requires_lines`) and never a negative grand total
 * (`validation.adjustment_negative_total`). Returns the first broken rule, or `undefined` when valid.
 */
export function checkAdjustment(
  adjustment: number | string | null | undefined,
  lines: readonly TotalsLineInput[]
): AdjustmentIssue | undefined {
  const minor = toSignedMinor(adjustment);
  if (minor === null) return "decimals";
  if (minor === 0n) return undefined;
  if (minor > MAX_ADJUSTMENT_MINOR || minor < -MAX_ADJUSTMENT_MINOR) return "max";
  if (lines.length === 0) return "requiresLines";
  const base = computeTotals(lines, 0);
  if (BigInt(Math.round(base.grandTotal * 100)) + minor < 0n) return "negativeTotal";
  return undefined;
}

export function computeTotals(lines: readonly TotalsLineInput[], adjustment: number | string = 0): DocumentTotals {
  let subtotal = 0n;
  let discountTotal = 0n;
  let taxTotal = 0n;
  const perLine = lines.map((line) => {
    const minor = computeLineMinor(line);
    subtotal += minor.subtotal;
    discountTotal += minor.discount;
    taxTotal += minor.tax;
    return {
      lineSubtotal: fromMinor(minor.subtotal),
      lineDiscount: fromMinor(minor.discount),
      lineTax: fromMinor(minor.tax),
      lineTotal: fromMinor(minor.subtotal - minor.discount + minor.tax),
    };
  });
  // An invalid adjustment (3 decimals, garbage) previews as 0: the form shows the error next to the field.
  const adjustmentMinor = lines.length === 0 ? 0n : (toSignedMinor(adjustment) ?? 0n);
  return {
    lines: perLine,
    subtotal: fromMinor(subtotal),
    discountTotal: fromMinor(discountTotal),
    taxTotal: fromMinor(taxTotal),
    adjustment: fromMinor(adjustmentMinor),
    grandTotal: fromMinor(subtotal - discountTotal + taxTotal + adjustmentMinor),
  };
}
