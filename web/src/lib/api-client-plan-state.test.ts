import { beforeEach, describe, expect, it, vi, type Mock } from "vitest";
import type { AxiosError } from "axios";

// Same technique as api-client-refresh.test.ts: drive the REAL `handleResponseError` against a fake HTTP layer.
const mockInstance = vi.hoisted(() => ({
  interceptors: {
    request: { use: vi.fn() },
    response: { use: vi.fn() },
  },
  request: vi.fn(),
}));

vi.mock("axios", async () => {
  const actual = await vi.importActual<typeof import("axios")>("axios");
  return { ...actual, default: { ...actual.default, create: () => mockInstance } };
});

type ApiClientModule = typeof import("./api-client");

function problemError(status: number, data: Record<string, unknown>): AxiosError {
  return {
    response: { status, data },
    config: { headers: { Authorization: "Bearer t" } },
  } as unknown as AxiosError;
}

describe("api-client plan / tenant-state errors", () => {
  let client: ApiClientModule;
  let handler: Mock<(code: string) => void>;

  beforeEach(async () => {
    vi.clearAllMocks();
    localStorage.clear();
    vi.resetModules();
    client = await import("./api-client");
    handler = vi.fn<(code: string) => void>();
    client.setPlanStateErrorHandler(handler);
  });

  it.each([
    [403, "tenant.suspended"],
    [403, "plan.module_disabled"],
    [402, "plan.limit_exceeded"],
  ])("tells the app about a %s %s so it can re-read /me and the subscription, and still rejects", async (status, code) => {
    const error = problemError(status, { code });
    await expect(client.handleResponseError(error)).rejects.toBe(error);
    expect(handler).toHaveBeenCalledWith(code);
  });

  it.each([
    [403, "forbidden"],
    [403, "auth.password_change_required"],
    [409, "platform.invalid_transition"],
    [404, "platform.plan_not_found"],
    [400, "validation"],
    [403, undefined],
  ])("ignores %s %s", async (status, code) => {
    const error = problemError(status, code ? { code } : {});
    await expect(client.handleResponseError(error)).rejects.toBe(error);
    expect(handler).not.toHaveBeenCalled();
  });

  it("does not treat a same-named code on another status as a plan error", async () => {
    const error = problemError(500, { code: "tenant.suspended" });
    await expect(client.handleResponseError(error)).rejects.toBe(error);
    expect(handler).not.toHaveBeenCalled();
  });

  it("works without a handler", async () => {
    client.setPlanStateErrorHandler(null);
    const error = problemError(403, { code: "tenant.suspended" });
    await expect(client.handleResponseError(error)).rejects.toBe(error);
  });
});
