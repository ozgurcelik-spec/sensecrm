import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AxiosError, AxiosHeaders } from "axios";
import { renderWithProviders } from "@/test-utils";
import { useAuthStore } from "@/store/auth.store";
import { getAuthConfig } from "@/services/auth.service";
import LoginPage from "./login";

// Non-React code (getApiErrorMessage) translates through `@/i18n`; use the in-memory test instance.
vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

// LoginPage only reads `login` / `isAuthenticated` through selectors - a selector-applying mock is enough.
vi.mock("@/store/auth.store", () => ({ useAuthStore: vi.fn() }));

// The sign-up link depends on GET /auth/config (Registration:Mode); the service is mocked per test.
vi.mock("@/services/auth.service", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/services/auth.service")>()),
  getAuthConfig: vi.fn(),
}));
const getAuthConfigMock = getAuthConfig as unknown as ReturnType<typeof vi.fn>;

const navigateMock = vi.fn();
vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return { ...actual, useNavigate: () => navigateMock };
});

const loginMock = vi.fn();
const mockUseAuthStore = useAuthStore as unknown as ReturnType<typeof vi.fn>;

function problem(status: number, data: Record<string, unknown>): AxiosError {
  const headers = new AxiosHeaders();
  return new AxiosError("Request failed", "ERR_BAD_REQUEST", { headers }, undefined, {
    status,
    statusText: "",
    headers: {},
    config: { headers },
    data,
  });
}

describe("LoginPage", () => {
  beforeEach(() => {
    loginMock.mockReset();
    getAuthConfigMock.mockReset();
    getAuthConfigMock.mockResolvedValue({ signupEnabled: true });
    navigateMock.mockReset();
    const state = { login: loginMock, isAuthenticated: () => false, me: null };
    mockUseAuthStore.mockImplementation((selector: (s: typeof state) => unknown) =>
      selector(state)
    );
  });

  it("shows required-field errors and does not call login when the form is empty", async () => {
    renderWithProviders(<LoginPage />, { route: "/login" });

    await userEvent.click(screen.getByRole("button", { name: "Giriş yap" }));

    expect(await screen.findAllByText("Bu alan zorunludur")).toHaveLength(2);
    expect(loginMock).not.toHaveBeenCalled();
  });

  it("rejects a malformed email", async () => {
    renderWithProviders(<LoginPage />, { route: "/login" });

    await userEvent.type(screen.getByLabelText(/E-posta/), "not-an-email");
    await userEvent.type(screen.getByLabelText(/Parola/), "secret123");
    await userEvent.click(screen.getByRole("button", { name: "Giriş yap" }));

    expect(await screen.findByText("Geçerli bir e-posta adresi girin")).toBeInTheDocument();
    expect(loginMock).not.toHaveBeenCalled();
  });

  it("logs in with trimmed credentials and navigates to the app", async () => {
    loginMock.mockResolvedValue(undefined);
    renderWithProviders(<LoginPage />, { route: "/login" });

    await userEvent.type(screen.getByLabelText(/E-posta/), "  ada@example.com ");
    await userEvent.type(screen.getByLabelText(/Parola/), "secret123");
    await userEvent.click(screen.getByRole("button", { name: "Giriş yap" }));

    await waitFor(() => expect(loginMock).toHaveBeenCalledWith("ada@example.com", "secret123"));
    expect(navigateMock).toHaveBeenCalledWith("/app", { replace: true });
  });

  it("translates the backend error code on failed login", async () => {
    loginMock.mockRejectedValue(
      problem(401, { status: 401, title: "Unauthorized", code: "auth.invalid_credentials" })
    );
    renderWithProviders(<LoginPage />, { route: "/login" });

    await userEvent.type(screen.getByLabelText(/E-posta/), "ada@example.com");
    await userEvent.type(screen.getByLabelText(/Parola/), "wrong-password");
    await userEvent.click(screen.getByRole("button", { name: "Giriş yap" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("E-posta veya parola hatalı");
    expect(navigateMock).not.toHaveBeenCalled();
  });
});

describe("LoginPage sign-up link", () => {
  beforeEach(() => {
    loginMock.mockReset();
    getAuthConfigMock.mockReset();
    const state = { login: loginMock, isAuthenticated: () => false, me: null };
    mockUseAuthStore.mockImplementation((selector: (s: typeof state) => unknown) =>
      selector(state)
    );
  });

  it("shows the sign-up link when the API reports sign-up as enabled", async () => {
    getAuthConfigMock.mockResolvedValue({ signupEnabled: true });
    renderWithProviders(<LoginPage />, { route: "/login" });

    const link = await screen.findByRole("link", { name: "Ücretsiz kaydolun" });
    expect(link).toHaveAttribute("href", "/signup");
  });

  it("hides the sign-up link when the API reports sign-up as disabled", async () => {
    getAuthConfigMock.mockResolvedValue({ signupEnabled: false });
    renderWithProviders(<LoginPage />, { route: "/login" });

    await waitFor(() => expect(getAuthConfigMock).toHaveBeenCalled());
    // Let the query settle, then make sure neither the link nor its prompt is rendered.
    await screen.findByRole("button", { name: "Giriş yap" });
    await waitFor(() => expect(screen.queryByRole("link", { name: "Ücretsiz kaydolun" })).toBeNull());
    expect(screen.queryByText("Hesabınız yok mu?")).toBeNull();
  });

  it("keeps the link hidden when the config request fails (closed by default)", async () => {
    getAuthConfigMock.mockRejectedValue(new Error("network"));
    renderWithProviders(<LoginPage />, { route: "/login" });

    await waitFor(() => expect(getAuthConfigMock).toHaveBeenCalled());
    expect(screen.queryByRole("link", { name: "Ücretsiz kaydolun" })).toBeNull();
  });
});
