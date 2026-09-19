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
  /** Always `subtotal - discountTotal + taxTotal`, which equals the sum of the line totals. */
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

export function computeTotals(lines: readonly TotalsLineInput[]): DocumentTotals {
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
  return {
    lines: perLine,
    subtotal: fromMinor(subtotal),
    discountTotal: fromMinor(discountTotal),
    taxTotal: fromMinor(taxTotal),
    grandTotal: fromMinor(subtotal - discountTotal + taxTotal),
  };
}
