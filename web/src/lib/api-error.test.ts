import { describe, expect, it, vi } from "vitest";
import { problem } from "@/test/crm";
import { applyValidationErrors, getApiErrorMessage } from "./api-error";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

describe("applyValidationErrors", () => {
  it("lower-camel-cases every segment of a dotted server field name", () => {
    const setError = vi.fn();
    const error = problem(400, {
      code: "validation",
      errors: { Name: ["required"], "BillingAddress.City": ["too long"], Unknown: ["x"] },
    });

    const matched = applyValidationErrors(error, setError, ["name", "billingAddress.city"]);

    expect(matched).toBe(true);
    expect(setError).toHaveBeenCalledWith("name", { type: "server", message: "required" });
    expect(setError).toHaveBeenCalledWith("billingAddress.city", {
      type: "server",
      message: "too long",
    });
    expect(setError).toHaveBeenCalledTimes(2);
  });

  it("returns false when nothing matches so the caller can toast", () => {
    const setError = vi.fn();
    const error = problem(400, { code: "validation", errors: { Other: ["x"] } });

    expect(applyValidationErrors(error, setError, ["name"])).toBe(false);
    expect(setError).not.toHaveBeenCalled();
  });
});

describe("getApiErrorMessage for the sales codes", () => {
  it.each([
    ["lead.already_converted", "Bu potansiyel zaten dönüştürülmüş"],
    ["deal.lost_reason_required", "Kaybedilen fırsat için kayıp nedeni girilmelidir"],
    ["pipeline.stage_in_use", "Bu aşamada fırsatlar var; önce fırsatları başka aşamaya taşıyın"],
    ["pipeline.default_required", "En az bir varsayılan satış hunisi olmalıdır"],
    ["pipeline.stage_not_found", "Aşama bulunamadı"],
    ["owner.not_member", "Seçilen sahip bu organizasyonun aktif üyesi değil"],
  ])("translates %s", (code, text) => {
    expect(getApiErrorMessage(problem(409, { code, title: "server title" }))).toBe(text);
  });

  it("translates account.has_dependents", () => {
    expect(getApiErrorMessage(problem(409, { code: "account.has_dependents" }))).toMatch(
      /bağlı kişi veya fırsatlar var/
    );
  });
});
