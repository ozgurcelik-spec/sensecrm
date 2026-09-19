import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AxiosError } from "axios";

// The api-client creates its axios instance at module load via `axios.create(...)`. Returning this
// controlled instance lets the tests drive the REAL `handleResponseError` against a fake HTTP layer.
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

function authError(status: number, extraConfig: Record<string, unknown> = {}): AxiosError {
  return {
    response: { status },
    config: { headers: { Authorization: "Bearer stale" }, ...extraConfig },
  } as unknown as AxiosError;
}

describe("api-client 401 handling", () => {
  let client: ApiClientModule;

  beforeEach(async () => {
    vi.clearAllMocks();
    localStorage.clear();
    // Fresh module per test: `refreshInFlight` / `handlingSessionExpiry` are module-level state.
    vi.resetModules();
    client = await import("./api-client");
  });

  it("passes non-401 errors straight through", async () => {
    const refresh = vi.fn();
    client.setRefreshHandler(refresh);
    const error = authError(403);

    await expect(client.handleResponseError(error)).rejects.toBe(error);
    expect(refresh).not.toHaveBeenCalled();
  });

  it("does not refresh for anonymous auth calls (e.g. wrong password on login)", async () => {
    const refresh = vi.fn();
    client.setRefreshHandler(refresh);
    const anonymous = {
      response: { status: 401 },
      config: { headers: {} },
    } as unknown as AxiosError;
    const skipped = authError(401, { skipAuthRetry: true });

    await expect(client.handleResponseError(anonymous)).rejects.toBe(anonymous);
    await expect(client.handleResponseError(skipped)).rejects.toBe(skipped);
    expect(refresh).not.toHaveBeenCalled();
  });

  it("refreshes once and retries the original request", async () => {
    client.setRefreshHandler(async () => {
      localStorage.setItem(client.AUTH_TOKEN_STORAGE_KEY, "new-token");
      return true;
    });
    mockInstance.request.mockResolvedValue({ status: 200, data: { ok: true } });

    const response = await client.handleResponseError(authError(401));

    expect(response).toEqual({ status: 200, data: { ok: true } });
    expect(mockInstance.request).toHaveBeenCalledTimes(1);
    expect(mockInstance.request).toHaveBeenCalledWith(
      expect.objectContaining({ _retriedAfterRefresh: true })
    );
  });

  it("shares a single refresh between concurrent 401s (the refresh token rotates)", async () => {
    let resolveRefresh: (ok: boolean) => void = () => {};
    const refresh = vi.fn(
      () =>
        new Promise<boolean>((resolve) => {
          resolveRefresh = resolve;
        })
    );
    client.setRefreshHandler(refresh);
    mockInstance.request.mockResolvedValue({ status: 200, data: {} });

    const first = client.handleResponseError(authError(401));
    const second = client.handleResponseError(authError(401));
    resolveRefresh(true);
    await Promise.all([first, second]);

    expect(refresh).toHaveBeenCalledTimes(1);
    expect(mockInstance.request).toHaveBeenCalledTimes(2);
  });

  it("ends the session once when the refresh fails", async () => {
    const expired = vi.fn();
    client.setRefreshHandler(async () => false);
    client.setSessionExpiredHandler(expired);
    localStorage.setItem(client.AUTH_TOKEN_STORAGE_KEY, "stale");
    localStorage.setItem(client.REFRESH_TOKEN_STORAGE_KEY, "revoked");

    const first = authError(401);
    const second = authError(401);
    await expect(client.handleResponseError(first)).rejects.toBe(first);
    await expect(client.handleResponseError(second)).rejects.toBe(second);

    expect(expired).toHaveBeenCalledTimes(1);
    expect(localStorage.getItem(client.AUTH_TOKEN_STORAGE_KEY)).toBeNull();
    expect(localStorage.getItem(client.REFRESH_TOKEN_STORAGE_KEY)).toBeNull();
    expect(mockInstance.request).not.toHaveBeenCalled();
  });

  it("does not refresh again when the retried request 401s", async () => {
    const refresh = vi.fn(async () => true);
    const expired = vi.fn();
    client.setRefreshHandler(refresh);
    client.setSessionExpiredHandler(expired);
    const retried = authError(401, { _retriedAfterRefresh: true });

    await expect(client.handleResponseError(retried)).rejects.toBe(retried);
    expect(refresh).not.toHaveBeenCalled();
    expect(expired).toHaveBeenCalledTimes(1);
  });
});
