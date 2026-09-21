import { describe, expect, it } from "vitest";
import { flatPrice } from "./pricebook";

/** `flat` reference vectors of docs/plan/m9c-envanter.md (shared with the backend `PriceResolution` tests). */
describe("flatPrice", () => {
  it.each([
    [19.99, -10, 17.991],
    [10.1, -33.33, 6.7337],
    [0.0001, 50, 0.0002],
    [100, 5.25, 105.25],
  ])("%s at %s%% -> %s", (catalog, percent, expected) => {
    expect(flatPrice(catalog, percent)).toBe(expected);
  });

  it("accepts text input and refuses garbage or more than two decimals", () => {
    expect(flatPrice("100", "-10")).toBe(90);
    expect(flatPrice(100, "abc")).toBeNull();
    expect(flatPrice(100, 1.234)).toBeNull();
  });

  it("stays at zero for a zero catalog price", () => {
    expect(flatPrice(0, 1000)).toBe(0);
  });
});
