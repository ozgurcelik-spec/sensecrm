import { describe, expect, it } from "vitest";
import { computeLineTotals, computeTotals, roundHalfUpDiv, toScaledInt } from "./commerce-totals";

/** Reference vectors of docs/plan/m6a-ticaret.md (shared with the backend `DocumentTotals` tests). */
const VECTORS = [
  {
    name: "1",
    line: { quantity: 3, unitPrice: 19.99, discountPercent: 10, taxRate: 20 },
    expected: { lineSubtotal: 59.97, lineDiscount: 6.0, lineTax: 10.79, lineTotal: 64.76 },
  },
  {
    name: "2 (0.025 rounds half up)",
    line: { quantity: 1, unitPrice: 0.05, discountPercent: 50, taxRate: 20 },
    expected: { lineSubtotal: 0.05, lineDiscount: 0.03, lineTax: 0.0, lineTotal: 0.02 },
  },
  {
    name: "3 (4.545 rounds half up)",
    line: { quantity: 2.5, unitPrice: 10.1, discountPercent: 0, taxRate: 18 },
    expected: { lineSubtotal: 25.25, lineDiscount: 0.0, lineTax: 4.55, lineTotal: 29.8 },
  },
  {
    name: "4 (100 % discount)",
    line: { quantity: 1, unitPrice: 100, discountPercent: 100, taxRate: 20 },
    expected: { lineSubtotal: 100.0, lineDiscount: 100.0, lineTax: 0.0, lineTotal: 0.0 },
  },
];

describe("computeTotals", () => {
  it.each(VECTORS)("reference vector $name", ({ line, expected }) => {
    expect(computeLineTotals(line)).toEqual(expected);
  });

  it("matches the reference document {1, 3}", () => {
    const totals = computeTotals([VECTORS[0]!.line, VECTORS[2]!.line]);
    expect(totals.subtotal).toBe(85.22);
    expect(totals.discountTotal).toBe(6.0);
    expect(totals.taxTotal).toBe(15.34);
    expect(totals.grandTotal).toBe(94.56);
    expect(totals.lines.map((l) => l.lineTotal)).toEqual([64.76, 29.8]);
  });

  it("is all zeros for a document without lines", () => {
    expect(computeTotals([])).toEqual({
      lines: [],
      subtotal: 0,
      discountTotal: 0,
      taxTotal: 0,
      grandTotal: 0,
    });
  });

  it("rounds half up per line, not on the sum (three 0.005 lines = 0.03, not 0.02)", () => {
    const line = { quantity: 1, unitPrice: 0.005, discountPercent: 0, taxRate: 0 };
    const totals = computeTotals([line, line, line]);
    expect(totals.subtotal).toBe(0.03);
    expect(totals.grandTotal).toBe(0.03);
  });

  it("has no floating point drift (1.005 is 1.01, 0.1 x 3 is exactly 0.30)", () => {
    expect(computeLineTotals({ quantity: 1, unitPrice: 1.005, discountPercent: 0, taxRate: 0 }))
      .toMatchObject({ lineSubtotal: 1.01 });
    expect(computeLineTotals({ quantity: 3, unitPrice: 0.1, discountPercent: 0, taxRate: 0 }))
      .toMatchObject({ lineSubtotal: 0.3, lineTotal: 0.3 });
    // 8.325 x 100 = 832.4999999999999 in floating point.
    expect(computeLineTotals({ quantity: 1, unitPrice: 8.325, discountPercent: 0, taxRate: 0 }).lineSubtotal).toBe(8.33);
  });

  it("keeps grandTotal equal to the sum of the line totals (invariant, pseudo-random inputs)", () => {
    let seed = 42;
    const next = () => {
      seed = (seed * 1664525 + 1013904223) % 4294967296;
      return seed / 4294967296;
    };
    for (let round = 0; round < 200; round++) {
      const lines = Array.from({ length: 1 + Math.floor(next() * 6) }, () => ({
        quantity: Math.round(next() * 1_000_000) / 10_000,
        unitPrice: Math.round(next() * 100_000_000) / 10_000,
        discountPercent: Math.round(next() * 10_000) / 100,
        taxRate: Math.round(next() * 10_000) / 100,
      }));
      const totals = computeTotals(lines);
      const sumOfLines = Math.round(totals.lines.reduce((s, l) => s + l.lineTotal * 100, 0));
      expect(Math.round(totals.grandTotal * 100)).toBe(sumOfLines);
      expect(Math.round((totals.subtotal - totals.discountTotal + totals.taxTotal) * 100)).toBe(
        Math.round(totals.grandTotal * 100)
      );
    }
  });

  it("handles the maximum values without overflow", () => {
    const totals = computeTotals([
      { quantity: 1_000_000, unitPrice: 1_000_000_000, discountPercent: 0, taxRate: 100 },
    ]);
    expect(totals.subtotal).toBe(1e15);
    expect(totals.taxTotal).toBe(1e15);
    expect(totals.grandTotal).toBe(2e15);
  });

  it("treats empty, invalid and negative input as zero", () => {
    expect(
      computeLineTotals({ quantity: "", unitPrice: "abc", discountPercent: -5, taxRate: undefined })
    ).toEqual({ lineSubtotal: 0, lineDiscount: 0, lineTax: 0, lineTotal: 0 });
  });

  it("accepts numeric strings (inputs mid-typing)", () => {
    expect(
      computeLineTotals({ quantity: "2", unitPrice: "10.5", discountPercent: "", taxRate: "10" })
    ).toEqual({ lineSubtotal: 21, lineDiscount: 0, lineTax: 2.1, lineTotal: 23.1 });
  });
});

describe("scaled integer helpers", () => {
  it("scales decimals without floating point noise", () => {
    expect(toScaledInt(19.99, 4)).toBe(199900n);
    expect(toScaledInt(0.1 + 0.2, 4)).toBe(3000n);
    expect(toScaledInt(12.5, 2)).toBe(1250n);
  });

  it("rounds half up with (2a + d) / 2d", () => {
    expect(roundHalfUpDiv(25n, 10n)).toBe(3n);
    expect(roundHalfUpDiv(24n, 10n)).toBe(2n);
    expect(roundHalfUpDiv(0n, 10n)).toBe(0n);
  });
});
