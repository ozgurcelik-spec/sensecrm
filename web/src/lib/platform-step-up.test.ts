import { describe, expect, it, vi } from "vitest";
import { getApiErrorMessage } from "@/lib/api-error";
import { stepUpFieldError } from "@/lib/platform";
import { problem } from "@/test/crm";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

describe("stepUpFieldError", () => {
  it("puts a missing or wrong password on the password field", () => {
    for (const code of ["platform.step_up_required", "platform.step_up_failed"]) {
      const result = stepUpFieldError(problem(422, { code }));
      expect(result?.field, code).toBe("password");
      expect(result?.message, code).toBeTruthy();
    }
    expect(stepUpFieldError(problem(422, { code: "platform.step_up_failed" }))?.message).toBe("Parola hatalı");
  });

  it("puts a mismatching typed organization name on the confirmation field", () => {
    const result = stepUpFieldError(problem(422, { code: "platform.confirmation_mismatch" }));
    expect(result).toEqual({
      field: "confirmTenantName",
      message: "Yazılan organizasyon adı eşleşmiyor",
    });
  });

  it("leaves everything else to the toast path", () => {
    for (const [status, code] of [
      [429, "platform.step_up_rate_limited"],
      [409, "platform.last_platform_admin"],
      [409, "platform.deletion_not_retryable"],
      [422, "platform.system_tenant_protected"],
    ] as const) {
      expect(stepUpFieldError(problem(status, { code })), code).toBeUndefined();
    }
    expect(stepUpFieldError(new Error("network"))).toBeUndefined();
  });

  it("translates every step-up code for the toast path", () => {
    for (const [status, code] of [
      [429, "platform.step_up_rate_limited"],
      [409, "platform.last_platform_admin"],
      [409, "platform.deletion_not_retryable"],
    ] as const) {
      const message = getApiErrorMessage(problem(status, { code }));
      expect(message, code).not.toBe("");
      expect(message, code).not.toContain("errors.platform");
    }
  });
});
