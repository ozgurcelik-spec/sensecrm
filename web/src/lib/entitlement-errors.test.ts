import { describe, expect, it, vi } from "vitest";
import { getApiErrorMessage } from "@/lib/api-error";
import { createTestI18n, testI18n } from "@/test-utils";
import { problem } from "@/test/crm";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

describe("friendly plan / tenant-state error messages", () => {
  it("tenant.suspended names the reason", () => {
    const message = (reason: string) =>
      getApiErrorMessage(problem(403, { code: "tenant.suspended", args: { reason } }));
    expect(message("suspended")).toContain("askıya alındı");
    expect(message("trial_expired")).toContain("deneme süresi doldu");
    expect(message("pending_deletion")).toContain("silinmek üzere");
    expect(message("deleted")).toContain("silindi");
    // Unknown reasons still read fine.
    expect(message("something_else")).toContain("askıya alındı");
  });

  it("plan.limit_exceeded reads 'Plan limitine ulaşıldı: Kullanıcı 5/5' for users and names the module for records", () => {
    expect(
      getApiErrorMessage(
        problem(402, { code: "plan.limit_exceeded", args: { limit: "users", max: 5, used: 5 } })
      )
    ).toBe("Plan limitine ulaşıldı: Kullanıcı 5/5");
    expect(
      getApiErrorMessage(
        problem(402, {
          code: "plan.limit_exceeded",
          args: { limit: "records", module: "sales", max: 5000, used: 5000 },
        })
      )
    ).toBe("Plan limitine ulaşıldı: Satış kayıtları 5000/5000");
  });

  it("plan.module_disabled names the module", () => {
    expect(
      getApiErrorMessage(problem(403, { code: "plan.module_disabled", args: { module: "marketing" } }))
    ).toBe("Bu modül planınıza dahil değil: Pazarlama");
  });

  it("platform lifecycle codes are translated, with the state names in invalid_transition", () => {
    expect(getApiErrorMessage(problem(404, { code: "platform.plan_not_found" }))).toBe(
      "Plan bulunamadı veya pasif"
    );
    expect(
      getApiErrorMessage(
        problem(409, { code: "platform.invalid_transition", args: { from: "suspended", to: "suspended" } })
      )
    ).toContain("Askıda");
    expect(getApiErrorMessage(problem(409, { code: "platform.deletion_not_cancellable" }))).toContain(
      "iptal edilemez"
    );
    expect(getApiErrorMessage(problem(422, { code: "platform.system_tenant_protected" }))).toContain(
      "Sistem organizasyonu"
    );
  });

  it("speaks English with the English resources", async () => {
    await testI18n.changeLanguage("en");
    try {
      expect(
        getApiErrorMessage(
          problem(402, { code: "plan.limit_exceeded", args: { limit: "users", max: 5, used: 5 } })
        )
      ).toBe("Plan limit reached: Users 5/5");
      expect(
        getApiErrorMessage(problem(403, { code: "tenant.suspended", args: { reason: "trial_expired" } }))
      ).toContain("trial period has ended");
    } finally {
      await testI18n.changeLanguage("tr");
    }
  });

  it("does not disturb existing common codes", () => {
    expect(getApiErrorMessage(problem(403, { code: "forbidden" }))).toBe(
      createTestI18n("tr").t("common:errors.forbidden")
    );
  });
});
