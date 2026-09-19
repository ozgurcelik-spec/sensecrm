import { describe, expect, it, vi } from "vitest";
import { CASE_STATUSES, type CaseStatus } from "@/types";
import {
  allowedTransitions,
  canReopenClosed,
  formatDuration,
  formatSlaDelta,
  isActiveStatus,
  isReopen,
  minutesUntil,
  needsResolutionNote,
} from "./case";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

// docs/plan/m6b-servis.md §3.2, row = current status, column = target status.
const TABLE: Record<CaseStatus, Record<CaseStatus, boolean>> = {
  new: { new: false, open: true, pending: true, resolved: true, closed: true },
  open: { new: false, open: false, pending: true, resolved: true, closed: true },
  pending: { new: false, open: true, pending: false, resolved: true, closed: true },
  resolved: { new: false, open: true, pending: false, resolved: false, closed: true },
  closed: { new: false, open: true, pending: false, resolved: false, closed: false },
};

describe("case state machine", () => {
  for (const from of CASE_STATUSES) {
    for (const to of CASE_STATUSES) {
      it(`${from} -> ${to} is ${TABLE[from][to] ? "allowed" : "not offered"}`, () => {
        expect(allowedTransitions(from).includes(to)).toBe(TABLE[from][to]);
      });
    }
  }

  it("requires a resolution note to resolve and to close an unresolved case only", () => {
    expect(needsResolutionNote("open", "resolved")).toBe(true);
    expect(needsResolutionNote("new", "closed")).toBe(true);
    expect(needsResolutionNote("pending", "closed")).toBe(true);
    expect(needsResolutionNote("resolved", "closed")).toBe(false);
    expect(needsResolutionNote("open", "pending")).toBe(false);
    expect(needsResolutionNote("closed", "open")).toBe(false);
  });

  it("recognises a reopen and the active statuses", () => {
    expect(isReopen("resolved", "open")).toBe(true);
    expect(isReopen("closed", "open")).toBe(true);
    expect(isReopen("pending", "open")).toBe(false);
    expect(isActiveStatus("new")).toBe(true);
    expect(isActiveStatus("pending")).toBe(true);
    expect(isActiveStatus("resolved")).toBe(false);
    expect(isActiveStatus("closed")).toBe(false);
  });

  it("hides the reopen of a closed case after the 14 day window (day 14 still passes)", () => {
    const closed = "2026-09-01T10:00:00Z";
    expect(canReopenClosed(closed, new Date("2026-09-15T10:00:00Z"))).toBe(true);
    expect(canReopenClosed(closed, new Date("2026-09-15T10:00:01Z"))).toBe(false);
    expect(canReopenClosed(undefined)).toBe(true);
  });
});

describe("durations", () => {
  it("formats minutes as days, hours and minutes without zero parts", () => {
    expect(formatDuration(135)).toBe("2 sa 15 dk");
    expect(formatDuration(480)).toBe("8 sa");
    expect(formatDuration(45)).toBe("45 dk");
    expect(formatDuration(0)).toBe("0 dk");
    expect(formatDuration(1440)).toBe("1 gün");
    expect(formatDuration(10080)).toBe("7 gün");
    expect(formatDuration(1500)).toBe("1 gün 1 sa");
  });

  it("describes the time left or past a target", () => {
    const now = new Date("2026-09-19T10:00:00Z");
    expect(minutesUntil("2026-09-19T12:15:00Z", now)).toBe(135);
    expect(formatSlaDelta("2026-09-19T12:15:00Z", now)).toBe("2 sa 15 dk kaldı");
    expect(formatSlaDelta("2026-09-19T09:20:00Z", now)).toBe("40 dk geçti");
  });
});
