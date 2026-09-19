import { describe, expect, it } from "vitest";
import { lastSixMonthsRange, resolveRange } from "./report-range";
import { formatPeriod, ratio } from "./report-format";
import {
  fromZonedInput,
  startOfMonthBack,
  todayBoundsIso,
  toZonedInput,
  ymdInZone,
} from "./zoned-time";

const ISTANBUL = "Europe/Istanbul";

describe("todayBoundsIso", () => {
  it("returns the organization's calendar day as UTC instants, not the UTC day", () => {
    // 01:00 on 20 Sep in Istanbul (UTC+3) is still 19 Sep in UTC.
    const bounds = todayBoundsIso(ISTANBUL, new Date("2026-09-19T22:00:00Z"));
    expect(bounds).toEqual({
      from: "2026-09-19T21:00:00.000Z",
      to: "2026-09-20T20:59:59.999Z",
    });
  });

  it("handles a 23 hour day at a daylight saving change", () => {
    const bounds = todayBoundsIso("America/New_York", new Date("2026-03-08T18:00:00Z"));
    expect(bounds).toEqual({
      from: "2026-03-08T05:00:00.000Z",
      to: "2026-03-09T03:59:59.999Z",
    });
  });

  it("falls back to the browser zone for an unknown zone id", () => {
    const bounds = todayBoundsIso("Mars/Olympus", new Date("2026-09-19T12:00:00Z"));
    expect(new Date(bounds.to).getTime()).toBeGreaterThan(new Date(bounds.from).getTime());
  });
});

describe("datetime-local conversion", () => {
  it("round-trips a wall-clock value through UTC in the organization's zone", () => {
    const iso = fromZonedInput("2026-09-21T10:00", ISTANBUL);
    expect(iso).toBe("2026-09-21T07:00:00.000Z");
    expect(toZonedInput(iso, ISTANBUL)).toBe("2026-09-21T10:00");
  });

  it("returns empty / undefined for missing or malformed values", () => {
    expect(toZonedInput(undefined, ISTANBUL)).toBe("");
    expect(toZonedInput("not a date", ISTANBUL)).toBe("");
    expect(fromZonedInput("", ISTANBUL)).toBeUndefined();
    expect(fromZonedInput("21.09.2026", ISTANBUL)).toBeUndefined();
  });
});

describe("date helpers", () => {
  it("goes back whole months across a year boundary", () => {
    expect(startOfMonthBack({ year: 2026, month: 2, day: 10 }, 3)).toEqual({
      year: 2025,
      month: 11,
      day: 1,
    });
    expect(startOfMonthBack({ year: 2026, month: 9, day: 19 }, 0)).toEqual({
      year: 2026,
      month: 9,
      day: 1,
    });
  });

  it("reads the calendar date in a zone", () => {
    expect(ymdInZone(new Date("2026-12-31T22:30:00Z"), ISTANBUL)).toEqual({
      year: 2027,
      month: 1,
      day: 1,
    });
  });
});

describe("resolveRange", () => {
  const now = new Date("2026-09-19T10:00:00Z");

  it("covers whole calendar months up to today for each preset", () => {
    expect(resolveRange("thisMonth", ISTANBUL, {}, now)).toEqual({
      from: "2026-09-01",
      to: "2026-09-19",
    });
    expect(resolveRange("last3Months", ISTANBUL, {}, now)).toEqual({
      from: "2026-07-01",
      to: "2026-09-19",
    });
    expect(resolveRange("last12Months", ISTANBUL, {}, now)).toEqual({
      from: "2025-10-01",
      to: "2026-09-19",
    });
    expect(lastSixMonthsRange(ISTANBUL, now)).toEqual({ from: "2026-04-01", to: "2026-09-19" });
  });

  it("takes a custom range as given, and is null while incomplete or reversed", () => {
    expect(resolveRange("custom", ISTANBUL, { from: "2026-01-05", to: "2026-02-10" }, now)).toEqual(
      {
        from: "2026-01-05",
        to: "2026-02-10",
      }
    );
    expect(resolveRange("custom", ISTANBUL, { from: "2026-01-05", to: "" }, now)).toBeNull();
    expect(
      resolveRange("custom", ISTANBUL, { from: "2026-03-01", to: "2026-02-01" }, now)
    ).toBeNull();
    expect(resolveRange("custom", ISTANBUL, { from: "garbage", to: "2026-02-01" }, now)).toBeNull();
  });
});

describe("report formatting", () => {
  it("formats month periods and leaves week periods alone", () => {
    expect(formatPeriod("2026-W38")).toBe("2026-W38");
    expect(formatPeriod("2026-09")).toMatch(/2026/);
  });

  it("guards the conversion ratio against a zero total", () => {
    expect(ratio(1, 4)).toBe(0.25);
    expect(ratio(3, 0)).toBe(0);
  });
});
