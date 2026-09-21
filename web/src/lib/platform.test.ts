import { describe, expect, it } from "vitest";
import { problem } from "@/test/crm";
import { orgDetail } from "@/test/platform";
import {
  dayDiff,
  daysAgo,
  metricKeys,
  organizationActions,
  serverFieldErrors,
  toOverridesBody,
  toOverridesDraft,
} from "./platform";

describe("organizationActions", () => {
  const now = Date.parse("2026-09-20T10:00:00Z");

  it("active and trial organizations can be suspended, re-planned and deleted, not reactivated", () => {
    for (const status of ["trial", "active", "trial_expired"] as const) {
      const a = organizationActions(orgDetail("a", { status }), now);
      expect(a).toMatchObject({
        editPlan: true,
        suspend: true,
        reactivate: false,
        requestDeletion: true,
        cancelDeletion: false,
      });
    }
  });

  it("a suspended organization can only be reactivated, re-planned or deleted", () => {
    expect(organizationActions(orgDetail("a", { status: "suspended" }), now)).toMatchObject({
      editPlan: true,
      suspend: false,
      reactivate: true,
      requestDeletion: true,
    });
  });

  it("an open deletion request blocks a second one; a scheduled one can be cancelled before its date", () => {
    const deletion = {
      requestId: "d1",
      status: "scheduled" as const,
      requestedAt: "2026-09-19T10:00:00Z",
      reason: "KVKK",
      retentionDays: 30,
      scheduledFor: "2026-10-19T10:00:00Z",
      attempts: 0,
    };
    const pending = organizationActions(orgDetail("a", { status: "pending_deletion", deletion }), now);
    expect(pending).toMatchObject({ cancelDeletion: true, suspend: false, reactivate: false, requestDeletion: false, editPlan: false });
    // After the scheduled date the server refuses the cancel.
    expect(
      organizationActions(orgDetail("a", { status: "pending_deletion", deletion }), Date.parse("2026-10-20T00:00:00Z"))
        .cancelDeletion
    ).toBe(false);
    // A running or failed request cannot be cancelled either.
    expect(
      organizationActions(
        orgDetail("a", { status: "pending_deletion", deletion: { ...deletion, status: "failed" } }),
        now
      ).cancelDeletion
    ).toBe(false);
  });

  it("the system organization gets no action at all", () => {
    expect(organizationActions(orgDetail("a", { status: "active", isSystem: true }), now)).toMatchObject({
      systemProtected: true,
      editPlan: false,
      suspend: false,
      reactivate: false,
      requestDeletion: false,
      cancelDeletion: false,
    });
  });
});

describe("overrides <-> draft", () => {
  it("round-trips: absent = plan, null = unlimited, number = custom", () => {
    const draft = toOverridesDraft({
      maxUsers: null,
      maxRecords: { sales: 200000, commerce: null },
      modules: { workflows: true, marketing: false },
    });
    expect(draft.maxUsers.mode).toBe("unlimited");
    expect(draft.maxRecords.sales).toEqual({ mode: "custom", value: 200000 });
    expect(draft.maxRecords.commerce?.mode).toBe("unlimited");
    expect(draft.maxRecords.activities?.mode).toBe("plan");
    expect(draft.modules).toEqual({
      workflows: "on",
      commerce: "plan",
      service: "plan",
      marketing: "off",
      integrations: "plan",
    });
    expect(toOverridesBody(draft)).toEqual({
      maxUsers: null,
      maxRecords: { sales: 200000, commerce: null },
      modules: { workflows: true, marketing: false },
    });
  });

  it("an empty draft sends no overrides at all (the PUT then clears them)", () => {
    expect(toOverridesBody(toOverridesDraft(undefined))).toBeUndefined();
  });

  it("reads keys case-insensitively (the server stores the JSON as sent)", () => {
    const draft = toOverridesDraft({ MaxUsers: 10 } as never);
    expect(draft.maxUsers).toEqual({ mode: "custom", value: 10 });
  });
});

describe("server field errors", () => {
  it("lower-camel-cases every path segment", () => {
    const error = problem(400, {
      code: "validation",
      errors: {
        PlanCode: ["Bilinmeyen plan"],
        "Overrides.MaxUsers": ["Negatif olamaz"],
        "Overrides.MaxRecords.sales": ["Geçersiz"],
      },
    });
    expect(serverFieldErrors(error)).toEqual({
      planCode: "Bilinmeyen plan",
      "overrides.maxUsers": "Negatif olamaz",
      "overrides.maxRecords.sales": "Geçersiz",
    });
  });

  it("is empty for anything without validation errors", () => {
    expect(serverFieldErrors(new Error("x"))).toEqual({});
    expect(serverFieldErrors(problem(409, { code: "platform.invalid_transition" }))).toEqual({});
  });
});

describe("days and usage helpers", () => {
  it("computes UTC days", () => {
    const now = new Date("2026-09-20T23:30:00Z");
    expect(daysAgo(0, now)).toBe("2026-09-20");
    expect(daysAgo(89, now)).toBe("2026-06-23");
    expect(dayDiff("2026-01-01", "2026-01-31")).toBe(30);
    expect(dayDiff("2026-02-01", "2026-01-31")).toBe(-1);
  });

  it("lists metric keys with record totals first", () => {
    expect(
      metricKeys([
        { metrics: { "sales.accounts": 1, "sales.records": 5 } },
        { metrics: { "commerce.records": 2, "commerce.quotes": 1 } },
      ])
    ).toEqual(["commerce.records", "sales.records", "commerce.quotes", "sales.accounts"]);
  });
});

describe("storage limit override (M8C)", () => {
  it("round-trips maxStorageMb: absent = plan, null = unlimited, number = custom (0 = uploads off)", () => {
    expect(toOverridesDraft(undefined).maxStorageMb.mode).toBe("plan");
    expect(toOverridesDraft({ maxStorageMb: null }).maxStorageMb).toEqual({ mode: "unlimited", value: "" });
    expect(toOverridesDraft({ maxStorageMb: 2048 }).maxStorageMb).toEqual({ mode: "custom", value: 2048 });
    expect(toOverridesDraft({ maxStorageMb: 0 }).maxStorageMb).toEqual({ mode: "custom", value: 0 });

    expect(toOverridesBody(toOverridesDraft({ maxStorageMb: null }))).toEqual({ maxStorageMb: null });
    expect(toOverridesBody(toOverridesDraft({ maxStorageMb: 2048 }))).toEqual({ maxStorageMb: 2048 });
    expect(toOverridesBody(toOverridesDraft({ maxStorageMb: 0 }))).toEqual({ maxStorageMb: 0 });
    // Following the plan sends nothing.
    expect(toOverridesBody(toOverridesDraft({ maxUsers: 3 }))).toEqual({ maxUsers: 3 });
  });

  it("reads the key case-insensitively like the other limits", () => {
    expect(toOverridesDraft({ MaxStorageMb: 10 } as never).maxStorageMb).toEqual({ mode: "custom", value: 10 });
  });

  it("maps the server's Overrides.MaxStorageMb error path", () => {
    const error = problem(400, { code: "validation", errors: { "Overrides.MaxStorageMb": ["Geçersiz"] } });
    expect(serverFieldErrors(error)).toEqual({ "overrides.maxStorageMb": "Geçersiz" });
  });
});
