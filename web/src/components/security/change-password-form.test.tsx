import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AUTH_TOKEN_STORAGE_KEY, REFRESH_TOKEN_STORAGE_KEY, apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, meWith, problem, type MockClient } from "@/test/crm";
import { useAuthStore } from "@/store/auth.store";
import { ChangePasswordForm } from "./change-password-form";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const onChanged = vi.fn();

const CURRENT = "old-password-1";
const NEW_PASSWORD = "a-brand-new-pass-77";

async function fill(current: string, next: string, confirm: string) {
  await userEvent.type(screen.getByLabelText(/^Mevcut parola/), current);
  await userEvent.type(screen.getByLabelText(/^Yeni parola(?! \()/), next);
  await userEvent.type(screen.getByLabelText(/^Yeni parola \(tekrar\)/), confirm);
  await userEvent.click(screen.getByRole("button", { name: "Parolayı değiştir" }));
}

function passwordCalls() {
  return client.post.mock.calls.filter(([url]) => url === "/me/password");
}

describe("ChangePasswordForm", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    act(() =>
      useAuthStore.setState({
        token: "old-access",
        refreshToken: "old-refresh",
        me: meWith([]),
        mustChangePassword: false,
      })
    );
    installApi(client, {
      "POST /me/password": () => ({
        accessToken: "new-access",
        refreshToken: "new-refresh",
        expiresAt: "2030-01-01T00:00:00Z",
      }),
      "GET /me": () => meWith([]),
    });
  });
  afterEach(() => {
    clearSession();
    act(() =>
      useAuthStore.setState({ token: null, refreshToken: null, mustChangePassword: false })
    );
  });

  function renderForm() {
    return renderWithProviders(
      <ChangePasswordForm email="ada@example.com" onChanged={onChanged} />
    );
  }

  it("requires all three fields", async () => {
    renderForm();
    await userEvent.click(screen.getByRole("button", { name: "Parolayı değiştir" }));

    expect(await screen.findAllByText("Bu alan zorunludur")).toHaveLength(2);
    expect(screen.getByText("Parola en az 10 karakter olmalıdır")).toBeInTheDocument();
    expect(passwordCalls()).toHaveLength(0);
  });

  it("enforces the 10 character minimum", async () => {
    renderForm();
    await fill(CURRENT, "short-9-c", "short-9-c");

    expect(await screen.findByText("Parola en az 10 karakter olmalıdır")).toBeInTheDocument();
    expect(passwordCalls()).toHaveLength(0);
  });

  it("rejects a password longer than 128 characters", async () => {
    renderForm();
    const long = "x".repeat(129);
    await userEvent.type(screen.getByLabelText(/^Mevcut parola/), CURRENT);
    // Paste instead of typing 129 keystrokes twice.
    await userEvent.click(screen.getByLabelText(/^Yeni parola(?! \()/));
    await userEvent.paste(long);
    await userEvent.click(screen.getByLabelText(/^Yeni parola \(tekrar\)/));
    await userEvent.paste(long);
    await userEvent.click(screen.getByRole("button", { name: "Parolayı değiştir" }));

    expect(await screen.findByText("Parola en fazla 128 karakter olabilir")).toBeInTheDocument();
    expect(passwordCalls()).toHaveLength(0);
  });

  it("rejects a new password containing the e-mail local part", async () => {
    renderForm();
    await fill(CURRENT, "my-ADA-secret-1", "my-ADA-secret-1");

    expect(
      await screen.findByText("Parola e-posta adresinizin kullanıcı adı kısmını içermemelidir")
    ).toBeInTheDocument();
    expect(passwordCalls()).toHaveLength(0);
  });

  it("requires the confirmation to match", async () => {
    renderForm();
    await fill(CURRENT, NEW_PASSWORD, NEW_PASSWORD + "x");
    expect(await screen.findByText("Parolalar eşleşmiyor")).toBeInTheDocument();
    expect(passwordCalls()).toHaveLength(0);
  });

  it("requires the new password to differ from the current one", async () => {
    renderForm();
    await fill(CURRENT, CURRENT, CURRENT);
    expect(
      await screen.findByText("Yeni parola mevcut parolanızdan farklı olmalıdır")
    ).toBeInTheDocument();
    expect(passwordCalls()).toHaveLength(0);
  });

  it("sends the change, replaces the stored tokens and reports success", async () => {
    renderForm();
    await fill(CURRENT, NEW_PASSWORD, NEW_PASSWORD);

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(passwordCalls()).toEqual([
      [
        "/me/password",
        { currentPassword: CURRENT, newPassword: NEW_PASSWORD },
        // A 401 here means "wrong current password" and must not end the session.
        { passthroughUnauthorized: true },
      ],
    ]);
    expect(localStorage.getItem(AUTH_TOKEN_STORAGE_KEY)).toBe("new-access");
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe("new-refresh");
    expect(useAuthStore.getState().token).toBe("new-access");
    expect(useAuthStore.getState().refreshToken).toBe("new-refresh");
    // The profile is reloaded with the new session.
    expect(client.get).toHaveBeenCalledWith("/me");
  });

  it("maps a 401 auth.invalid_credentials onto the current password field and keeps the session", async () => {
    installApi(client, {
      "POST /me/password": () =>
        problem(401, { status: 401, title: "Unauthorized", code: "auth.invalid_credentials" }),
    });
    renderForm();
    await fill("wrong-password-1", NEW_PASSWORD, NEW_PASSWORD);

    expect(await screen.findByText("Mevcut parola hatalı")).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
    expect(useAuthStore.getState().token).toBe("old-access");
  });

  it("shows the server message of a newPassword validation error on the field (e.g. common password)", async () => {
    installApi(client, {
      "POST /me/password": () =>
        problem(400, {
          status: 400,
          title: "Validation",
          code: "validation",
          errors: { NewPassword: ["Bu parola çok yaygın, başka bir parola seçin"] },
        }),
    });
    renderForm();
    await fill(CURRENT, NEW_PASSWORD, NEW_PASSWORD);

    expect(
      await screen.findByText("Bu parola çok yaygın, başka bir parola seçin")
    ).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("shows other API errors in an alert", async () => {
    installApi(client, {
      "POST /me/password": () =>
        problem(429, { status: 429, title: "Too many requests", code: "rate_limited" }),
    });
    renderForm();
    await fill(CURRENT, NEW_PASSWORD, NEW_PASSWORD);

    expect(await screen.findByRole("alert")).toHaveTextContent("Too many requests");
  });
});
