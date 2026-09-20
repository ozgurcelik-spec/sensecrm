import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, type MockClient } from "@/test/crm";
import { toast, toastApiError } from "@/hooks/use-toast";
import type { PlatformAdmin } from "@/types";
import PlatformAdminsPage from "./platform-admins";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ADMINS: PlatformAdmin[] = [
  { userId: "u1", email: "ops@sense.com", displayName: "Ops Lead", isActive: true, lastLoginAt: "2026-09-19T10:00:00Z" },
  { userId: "u2", email: "old@sense.com", displayName: "Old Admin", isActive: false },
];

describe("Platform administrators", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });
  afterEach(clearSession);

  it("lists the administrators with status and last sign-in", async () => {
    installApi(client, { "GET /platform/admins": () => ADMINS });
    renderWithProviders(<PlatformAdminsPage />);
    const rows = await screen.findAllByTestId("admin-row");
    expect(rows).toHaveLength(2);
    expect(within(rows[0] as HTMLElement).getByText("ops@sense.com")).toBeInTheDocument();
    expect(within(rows[0] as HTMLElement).getByText("Etkin")).toBeInTheDocument();
    expect(within(rows[1] as HTMLElement).getByText("Pasif")).toBeInTheDocument();
    expect(within(rows[1] as HTMLElement).getByText("Hiç giriş yapmadı")).toBeInTheDocument();
  });

  it("revokes after the caller's own password, optionally deactivating the account", async () => {
    installApi(client, {
      "GET /platform/admins": () => ADMINS,
      "POST /platform/admins/u1/revoke": () => undefined,
    });
    renderWithProviders(<PlatformAdminsPage />);
    await userEvent.click(await screen.findByRole("button", { name: "ops@sense.com için yetkiyi kaldır" }));
    const dialog = await screen.findByRole("dialog");

    const confirm = within(dialog).getByRole("button", { name: "Yetkiyi kaldır" });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "Op3rator!pw");
    await userEvent.click(within(dialog).getByRole("checkbox", { name: /Hesabı da pasifleştir/ }));
    await userEvent.click(confirm);

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/platform/admins/u1/revoke", {
        currentPassword: "Op3rator!pw",
        deactivate: true,
      })
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
  });

  it("sends no deactivate flag unless the box is ticked", async () => {
    installApi(client, {
      "GET /platform/admins": () => ADMINS,
      "POST /platform/admins/u2/revoke": () => undefined,
    });
    renderWithProviders(<PlatformAdminsPage />);
    await userEvent.click(await screen.findByRole("button", { name: "old@sense.com için yetkiyi kaldır" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "pw");
    await userEvent.click(within(dialog).getByRole("button", { name: "Yetkiyi kaldır" }));
    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/platform/admins/u2/revoke", { currentPassword: "pw" })
    );
  });

  it("shows a wrong password inline and toasts the last-platform-admin guard", async () => {
    let code = "platform.step_up_failed";
    installApi(client, {
      "GET /platform/admins": () => ADMINS,
      "POST /platform/admins/u1/revoke": () =>
        problem(code === "platform.last_platform_admin" ? 409 : 422, { code }),
    });
    renderWithProviders(<PlatformAdminsPage />);
    await userEvent.click(await screen.findByRole("button", { name: "ops@sense.com için yetkiyi kaldır" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "yanlis");
    const confirm = within(dialog).getByRole("button", { name: "Yetkiyi kaldır" });

    await userEvent.click(confirm);
    expect(await within(dialog).findByText("Parola hatalı")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();

    code = "platform.last_platform_admin";
    await userEvent.click(confirm);
    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });
});
