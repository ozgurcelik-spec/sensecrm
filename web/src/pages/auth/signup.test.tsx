import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { renderWithProviders } from "@/test-utils";
import { useAuthStore } from "@/store/auth.store";
import { getAuthConfig } from "@/services/auth.service";
import SignupPage from "./signup";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/store/auth.store", () => ({ useAuthStore: vi.fn() }));
vi.mock("@/services/auth.service", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/services/auth.service")>()),
  getAuthConfig: vi.fn(),
}));

const getAuthConfigMock = getAuthConfig as unknown as ReturnType<typeof vi.fn>;
const mockUseAuthStore = useAuthStore as unknown as ReturnType<typeof vi.fn>;

function renderSignup() {
  return renderWithProviders(
    <Routes>
      <Route path="/signup" element={<SignupPage />} />
      <Route path="/login" element={<div>login page</div>} />
    </Routes>,
    { route: "/signup" }
  );
}

describe("SignupPage", () => {
  beforeEach(() => {
    getAuthConfigMock.mockReset();
    const state = { signup: vi.fn(), isAuthenticated: () => false, me: null };
    mockUseAuthStore.mockImplementation((selector: (s: typeof state) => unknown) =>
      selector(state)
    );
  });

  it("redirects to the login page when public sign-up is disabled", async () => {
    getAuthConfigMock.mockResolvedValue({ signupEnabled: false });
    renderSignup();

    expect(await screen.findByText("login page")).toBeInTheDocument();
  });

  it("renders the form when public sign-up is enabled", async () => {
    getAuthConfigMock.mockResolvedValue({ signupEnabled: true });
    renderSignup();

    expect(await screen.findByLabelText(/Şirket adı/)).toBeInTheDocument();
    await waitFor(() => expect(getAuthConfigMock).toHaveBeenCalled());
    expect(screen.queryByText("login page")).toBeNull();
  });
});
