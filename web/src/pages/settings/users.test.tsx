import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  MEMBERS,
  clearSession,
  installApi,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { toast, toastApiError } from "@/hooks/use-toast";
import UsersPage from "./users";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ROLES = [
  { id: "r1", name: "Satış", isSystem: false, permissions: [], memberCount: 2 },
  { id: "r2", name: "Yönetici", isSystem: true, permissions: [], memberCount: 1 },
];

const PENDING = {
  status: "pending",
  email: "invited@example.com",
  roleId: "r1",
  roleName: "Satış",
};

function members(): unknown[] {
  return [{ ...MEMBERS[0], status: "active" }, { ...MEMBERS[1], status: "active" }, PENDING];
}

async function createMember(email: string) {
  await userEvent.click(await screen.findByRole("button", { name: "Kullanıcı ekle" }));
  const dialog = await screen.findByRole("dialog");
  await userEvent.type(within(dialog).getByLabelText(/^E-posta/), email);
  await userEvent.type(within(dialog).getByLabelText(/^Ad Soyad/), "Yeni Kişi");
  await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
}

describe("UsersPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.users.read", "org.users.manage"]);
    installApi(client, {
      "GET /organization/members": members,
      "GET /organization/roles": () => ROLES,
    });
  });
  afterEach(clearSession);

  describe("members table", () => {
    it("renders a pending invitation read-only with the 'Davet bekliyor' badge", async () => {
      renderWithProviders(<UsersPage />);

      const row = await screen.findByTestId("pending-member-row");
      expect(within(row).getByText("invited@example.com")).toBeInTheDocument();
      expect(within(row).getByText("Davet bekliyor")).toBeInTheDocument();
      expect(within(row).getByText("Satış")).toBeInTheDocument();
      // Display name and invitation date are absent: dashes, no crash.
      expect(within(row).getAllByText("-")).toHaveLength(2);
      // No role select, no activate/deactivate switch on a pending row.
      expect(within(row).queryByRole("textbox")).not.toBeInTheDocument();
      expect(within(row).queryByRole("switch")).not.toBeInTheDocument();
      expect(within(row).queryByRole("combobox")).not.toBeInTheDocument();
    });

    it("keeps role and status controls on active members", async () => {
      renderWithProviders(<UsersPage />);

      await screen.findByTestId("pending-member-row");
      expect(screen.getAllByRole("switch")).toHaveLength(2);
      expect(screen.getByText("Grace Hopper")).toBeInTheDocument();
    });

    it("searches pending invitations by e-mail", async () => {
      renderWithProviders(<UsersPage />);
      await screen.findByTestId("pending-member-row");

      await userEvent.type(screen.getByPlaceholderText("Ad veya e-posta ile ara"), "invited");

      expect(screen.getByTestId("pending-member-row")).toBeInTheDocument();
      expect(screen.queryByText("Grace Hopper")).not.toBeInTheDocument();
    });
  });

  describe("create member", () => {
    it("has no password field and posts only e-mail, name and role", async () => {
      installApi(client, {
        "GET /organization/members": members,
        "GET /organization/roles": () => ROLES,
        "POST /organization/members": () => ({
          email: "new@example.com",
          roleId: "r1",
          roleName: "Satış",
          status: "active",
          userId: "u9",
          temporaryPassword: "Tmp-Pass-123456",
        }),
      });
      renderWithProviders(<UsersPage />);

      await userEvent.click(await screen.findByRole("button", { name: "Kullanıcı ekle" }));
      const dialog = await screen.findByRole("dialog");
      expect(within(dialog).queryByLabelText(/parola/i)).not.toBeInTheDocument();
      await userEvent.type(within(dialog).getByLabelText(/^E-posta/), "  new@example.com ");
      await userEvent.type(within(dialog).getByLabelText(/^Ad Soyad/), "Yeni Kişi");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post).toHaveBeenCalledWith("/organization/members", {
        email: "new@example.com",
        displayName: "Yeni Kişi",
        roleId: "r1",
      });
    });

    it("shows a new account's temporary password once in a copyable dialog with a warning", async () => {
      installApi(client, {
        "GET /organization/members": members,
        "GET /organization/roles": () => ROLES,
        "POST /organization/members": () => ({
          email: "new@example.com",
          roleId: "r1",
          roleName: "Satış",
          status: "active",
          userId: "u9",
          temporaryPassword: "Tmp-Pass-123456",
        }),
      });
      renderWithProviders(<UsersPage />);

      await createMember("new@example.com");

      const dialog = await screen.findByRole("dialog", { name: "Geçici parola" });
      expect(within(dialog).getByDisplayValue("Tmp-Pass-123456")).toHaveAttribute("readonly");
      expect(within(dialog).getByRole("button", { name: "Kopyala" })).toBeInTheDocument();
      expect(
        within(dialog).getByText(
          /güvenli bir yolla paylaşın.*ilk girişte parolasını değiştirmek zorundadır/i
        )
      ).toBeInTheDocument();
      // The success toast is replaced by this dialog.
      expect(toast).not.toHaveBeenCalled();

      await userEvent.click(within(dialog).getByRole("button", { name: /Parolayı kaydettim/ }));

      await waitFor(() =>
        expect(screen.queryByDisplayValue("Tmp-Pass-123456")).not.toBeInTheDocument()
      );
      // The members list is refreshed after the creation.
      await waitFor(() =>
        expect(
          client.get.mock.calls.filter(([url]) => url === "/organization/members").length
        ).toBeGreaterThanOrEqual(2)
      );
    });

    it("shows the pending notice (no password dialog) when an existing account was invited", async () => {
      installApi(client, {
        "GET /organization/members": members,
        "GET /organization/roles": () => ROLES,
        "POST /organization/members": () => ({
          email: "existing@example.com",
          roleId: "r1",
          roleName: "Satış",
          status: "pending",
        }),
      });
      renderWithProviders(<UsersPage />);

      await createMember("existing@example.com");

      await waitFor(() =>
        expect(toast).toHaveBeenCalledWith(
          expect.objectContaining({ description: "Kullanıcı daveti kabul edince eklenir" })
        )
      );
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    it("maps member.exists to the error toast and keeps the dialog open", async () => {
      const error = problem(409, { status: 409, title: "Conflict", code: "member.exists" });
      installApi(client, {
        "GET /organization/members": members,
        "GET /organization/roles": () => ROLES,
        "POST /organization/members": () => error,
      });
      renderWithProviders(<UsersPage />);

      await createMember("grace@example.com");

      await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
      expect(screen.getByRole("dialog", { name: "Yeni kullanıcı" })).toBeInTheDocument();
    });

    it("maps validation field errors onto the form", async () => {
      installApi(client, {
        "GET /organization/members": members,
        "GET /organization/roles": () => ROLES,
        "POST /organization/members": () =>
          problem(400, {
            status: 400,
            title: "Validation",
            code: "validation",
            errors: { Email: ["Bu e-posta alan adı kabul edilmiyor"] },
          }),
      });
      renderWithProviders(<UsersPage />);

      await createMember("new@blocked.example");

      expect(await screen.findByText("Bu e-posta alan adı kabul edilmiyor")).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
    });
  });
});
