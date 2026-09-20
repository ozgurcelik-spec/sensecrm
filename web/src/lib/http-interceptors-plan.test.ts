import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { registerSessionRefreshHandlers } from "./http-interceptors";

const mocks = vi.hoisted(() => ({
  planState: null as null | ((code: string) => void),
  refreshMe: vi.fn(() => Promise.resolve()),
  invalidateQueries: vi.fn(() => Promise.resolve()),
}));

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/store/auth.store", () => ({
  useAuthStore: { getState: () => ({ refreshMe: mocks.refreshMe }) },
}));
vi.mock("@/lib/query-client", () => ({ queryClient: { invalidateQueries: mocks.invalidateQueries } }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  return {
    ...actual,
    setPlanStateErrorHandler: (handler: (code: string) => void) => {
      mocks.planState = handler;
    },
  };
});

describe("plan-state errors re-read /me and the subscription", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-09-20T10:00:00Z"));
    mocks.refreshMe.mockClear();
    mocks.invalidateQueries.mockClear();
    registerSessionRefreshHandlers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("refreshes /me and invalidates the subscription queries, at most once per burst", () => {
    mocks.planState?.("tenant.suspended");
    expect(mocks.refreshMe).toHaveBeenCalledTimes(1);
    expect(mocks.invalidateQueries).toHaveBeenCalledWith({ queryKey: ["subscription"] });

    // A page firing several requests answers with several errors: no refetch storm.
    mocks.planState?.("plan.module_disabled");
    mocks.planState?.("plan.limit_exceeded");
    expect(mocks.refreshMe).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(11_000);
    mocks.planState?.("plan.limit_exceeded");
    expect(mocks.refreshMe).toHaveBeenCalledTimes(2);
  });
});
