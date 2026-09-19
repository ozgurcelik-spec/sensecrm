import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
const signupMock = vi.fn();

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
    signupMock.mockReset();
    const state = { signup: signupMock, isAuthenticated: () => false, me: null };
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
  describe("password policy (10-128 characters, no e-mail local part)", () => {
    async function fillAndSubmit(password: string, email = "ada@example.com") {
      getAuthConfigMock.mockResolvedValue({ signupEnabled: true });
      renderSignup();
      await userEvent.type(await screen.findByLabelText(/Şirket adı/), "Acme");
      await userEvent.type(screen.getByLabelText(/Ad Soyad/), "Ada Lovelace");
      await userEvent.type(screen.getByLabelText(/E-posta/), email);
      await userEvent.click(screen.getByLabelText(/^Parola/));
      await userEvent.paste(password);
      await userEvent.click(screen.getByRole("button", { name: "Hesap oluştur" }));
    }

    it("states the policy next to the field", async () => {
      getAuthConfigMock.mockResolvedValue({ signupEnabled: true });
      renderSignup();
      expect(await screen.findByText(/En az 10, en fazla 128 karakter/)).toBeInTheDocument();
    });

    it("rejects a 9 character password", async () => {
      await fillAndSubmit("abcdefghi");
      expect(await screen.findByText("Parola en az 10 karakter olmalıdır")).toBeInTheDocument();
      expect(signupMock).not.toHaveBeenCalled();
    });

    it("rejects a password over 128 characters", async () => {
      await fillAndSubmit("x".repeat(129));
      expect(await screen.findByText("Parola en fazla 128 karakter olabilir")).toBeInTheDocument();
      expect(signupMock).not.toHaveBeenCalled();
    });

    it("rejects a password containing the e-mail local part", async () => {
      await fillAndSubmit("my-ada-secret-99");
      expect(
        await screen.findByText("Parola e-posta adresinizin kullanıcı adı kısmını içermemelidir")
      ).toBeInTheDocument();
      expect(signupMock).not.toHaveBeenCalled();
    });

    it("accepts a 10 character password", async () => {
      signupMock.mockResolvedValue(undefined);
      await fillAndSubmit("qwertyuiop");
      await waitFor(() => expect(signupMock).toHaveBeenCalledTimes(1));
      expect(signupMock).toHaveBeenCalledWith(expect.objectContaining({ password: "qwertyuiop" }));
    });
  });
});
