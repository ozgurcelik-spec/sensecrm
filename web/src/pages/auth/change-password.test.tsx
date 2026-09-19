import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AxiosError, AxiosHeaders } from "axios";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient, handleResponseError } from "@/lib/api-client";
import { registerSessionRefreshHandlers } from "@/lib/http-interceptors";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { testI18n } from "@/test-utils";
import { installApi, meWith, type MockClient } from "@/test/crm";
import App from "@/App";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const NEW_PASSWORD = "a-brand-new-pass-77";

function api(meResponse = () => meWith(["crm.leads.read"])) {
  installApi(client, {
    "GET /me": meResponse,
    "GET /me/invitations": () => [],
    "GET /approvals/summary": () => ({ pendingCount: 0 }),
    "POST /me/password": () => ({
      accessToken: "new-access",
      refreshToken: "new-refresh",
      expiresAt: "2030-01-01T00:00:00Z",
    }),
  });
}

function renderApp(route: string, mustChangePassword: boolean) {
  act(() =>
    useAuthStore.setState({
      token: "t",
      refreshToken: "r",
      hasHydrated: true,
      me: meWith(["crm.leads.read"]),
      mustChangePassword,
    })
  );
  window.history.pushState({}, "", route);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <I18nextProvider i18n={testI18n}>
      <QueryClientProvider client={queryClient}>
        <MantineProvider theme={mantineTheme} env="test">
          <App />
        </MantineProvider>
      </QueryClientProvider>
    </I18nextProvider>
  );
}

function forcedError(): AxiosError {
  const headers = new AxiosHeaders({ Authorization: "Bearer t" });
  return new AxiosError("Forbidden", "ERR_BAD_REQUEST", { headers }, undefined, {
    status: 403,
    statusText: "",
    headers: {},
    config: { headers },
    data: { status: 403, title: "Forbidden", code: "auth.password_change_required" },
  });
}

async function submitNewPassword(password: string) {
  await userEvent.type(screen.getByLabelText(/^Mevcut parola/), "temporary-pass-1");
  await userEvent.type(screen.getByLabelText(/^Yeni parola(?! \()/), password);
  await userEvent.type(screen.getByLabelText(/^Yeni parola \(tekrar\)/), password);
  await userEvent.click(screen.getByRole("button", { name: "Parolayı değiştir" }));
}

describe("forced password change", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    registerSessionRefreshHandlers();
    api();
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
    window.history.pushState({}, "", "/");
  });

  it("routes a user with mustChangePassword to the full-page screen (no app shell) from any /app URL", async () => {
    renderApp("/app/leads", true);

    expect(
      await screen.findByRole("heading", { name: "Parolanızı değiştirin" })
    ).toBeInTheDocument();
    expect(window.location.pathname).toBe("/change-password");
    // No shell: no module navigation, no top bar.
    expect(screen.queryByRole("link", { name: "Potansiyeller" })).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation")).not.toBeInTheDocument();
  });

  it("redirects when GET /me reports mustChangePassword (persisted profile was stale)", async () => {
    api(() => ({ ...meWith(["crm.leads.read"]), mustChangePassword: true }));
    renderApp("/app", false);

    expect(
      await screen.findByRole("heading", { name: "Parolanızı değiştirin" })
    ).toBeInTheDocument();
    expect(useAuthStore.getState().mustChangePassword).toBe(true);
  });

  it("redirects on a 403 auth.password_change_required from any request", async () => {
    renderApp("/app", false);
    expect((await screen.findAllByRole("link", { name: "Potansiyeller" })).length).toBeGreaterThan(
      0
    );

    const error = forcedError();
    await act(async () => {
      await expect(handleResponseError(error)).rejects.toBe(error);
    });

    expect(
      await screen.findByRole("heading", { name: "Parolanızı değiştirin" })
    ).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Potansiyeller" })).not.toBeInTheDocument();
  });

  it("continues to /app after the password was changed and stores the new tokens", async () => {
    // After the change /me no longer reports the flag.
    renderApp("/app/leads", true);
    await screen.findByRole("heading", { name: "Parolanızı değiştirin" });

    await submitNewPassword(NEW_PASSWORD);

    expect((await screen.findAllByRole("link", { name: "Potansiyeller" })).length).toBeGreaterThan(
      0
    );
    await waitFor(() => expect(window.location.pathname).toBe("/app"));
    expect(client.post).toHaveBeenCalledWith(
      "/me/password",
      { currentPassword: "temporary-pass-1", newPassword: NEW_PASSWORD },
      { passthroughUnauthorized: true }
    );
    expect(localStorage.getItem("auth_token")).toBe("new-access");
    expect(localStorage.getItem("refresh_token")).toBe("new-refresh");
    expect(useAuthStore.getState().mustChangePassword).toBe(false);
  });

  it("keeps the user on the screen when the server rejects the password", async () => {
    installApi(client, {
      "GET /me": () => ({ ...meWith(["crm.leads.read"]), mustChangePassword: true }),
      "GET /me/invitations": () => [],
      "POST /me/password": () => {
        const headers = new AxiosHeaders();
        return new AxiosError("Bad", "ERR_BAD_REQUEST", { headers }, undefined, {
          status: 400,
          statusText: "",
          headers: {},
          config: { headers },
          data: {
            status: 400,
            title: "Validation",
            code: "validation",
            errors: { NewPassword: ["Bu parola çok yaygın"] },
          },
        });
      },
    });
    renderApp("/change-password", true);
    await screen.findByRole("heading", { name: "Parolanızı değiştirin" });

    await submitNewPassword(NEW_PASSWORD);

    expect(await screen.findByText("Bu parola çok yaygın")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Parolanızı değiştirin" })).toBeInTheDocument();
    expect(useAuthStore.getState().mustChangePassword).toBe(true);
  });

  it("sends a user without the flag away from /change-password", async () => {
    renderApp("/change-password", false);

    expect((await screen.findAllByRole("link", { name: "Potansiyeller" })).length).toBeGreaterThan(
      0
    );
    expect(window.location.pathname).toBe("/app");
  });
});
