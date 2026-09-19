import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { installApi, meWith, problem, type MockClient } from "@/test/crm";
import { useAuthStore } from "./auth.store";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const TOKENS = { accessToken: "a", refreshToken: "r", expiresAt: "2030-01-01T00:00:00Z" };

describe("auth store (C-SEC)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    act(() =>
      useAuthStore.setState({
        token: null,
        refreshToken: null,
        me: null,
        mustChangePassword: false,
      })
    );
  });
  afterEach(() => {
    act(() =>
      useAuthStore.setState({
        token: null,
        refreshToken: null,
        me: null,
        mustChangePassword: false,
      })
    );
  });

  it("marks the session as password-change-required from the login tokens", async () => {
    installApi(client, {
      "POST /auth/login": () => ({ ...TOKENS, mustChangePassword: true }),
      "GET /me": () => meWith([]),
    });
    await useAuthStore.getState().login("ada@example.com", "temp-password-1");
    expect(useAuthStore.getState().mustChangePassword).toBe(true);
  });

  it("marks the session as password-change-required from GET /me", async () => {
    installApi(client, {
      "POST /auth/login": () => TOKENS,
      "GET /me": () => ({ ...meWith([]), mustChangePassword: true }),
    });
    await useAuthStore.getState().login("ada@example.com", "temp-password-1");
    expect(useAuthStore.getState().mustChangePassword).toBe(true);
  });

  it("does not require a change for a regular login", async () => {
    installApi(client, {
      "POST /auth/login": () => ({ ...TOKENS, mustChangePassword: false }),
      "GET /me": () => ({ ...meWith([]), mustChangePassword: false }),
    });
    await useAuthStore.getState().login("ada@example.com", "a-good-password");
    expect(useAuthStore.getState().mustChangePassword).toBe(false);
  });

  it("resolves false (so the api-client ends the session) when the refresh endpoint answers 401", async () => {
    act(() => useAuthStore.setState({ token: "old", refreshToken: "expired-absolute" }));
    installApi(client, {
      "POST /auth/refresh": () =>
        problem(401, { status: 401, title: "Unauthorized", code: "auth.invalid_refresh_token" }),
    });

    await expect(useAuthStore.getState().refresh()).resolves.toBe(false);
    expect(client.post).toHaveBeenCalledWith(
      "/auth/refresh",
      { refreshToken: "expired-absolute" },
      { skipAuthRetry: true }
    );
  });

  it("adopts a rotated token pair and the flag from a refresh response", async () => {
    act(() => useAuthStore.setState({ token: "old", refreshToken: "old-r" }));
    installApi(client, {
      "POST /auth/refresh": () => ({ ...TOKENS, mustChangePassword: true }),
    });

    await expect(useAuthStore.getState().refresh()).resolves.toBe(true);
    expect(useAuthStore.getState().refreshToken).toBe("r");
    expect(useAuthStore.getState().mustChangePassword).toBe(true);
  });

  it("keeps a forced-change flag set by the 403 handler when /me does not carry the field", async () => {
    act(() => useAuthStore.setState({ token: "t", refreshToken: "r", mustChangePassword: false }));
    useAuthStore.getState().setMustChangePassword(true);
    installApi(client, { "GET /me": () => meWith([]) });

    await useAuthStore.getState().refreshMe();
    expect(useAuthStore.getState().mustChangePassword).toBe(true);
  });

  it("clears the flag on logout", async () => {
    act(() =>
      useAuthStore.setState({
        token: "t",
        refreshToken: "r",
        me: meWith([]),
        mustChangePassword: true,
      })
    );
    installApi(client, { "POST /auth/logout": () => undefined });

    await useAuthStore.getState().logout();
    expect(useAuthStore.getState().mustChangePassword).toBe(false);
    expect(useAuthStore.getState().me).toBeNull();
  });
});
