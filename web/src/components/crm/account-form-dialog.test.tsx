import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { getApiErrorMessage } from "@/lib/api-error";
import { renderWithProviders } from "@/test-utils";
import {
  ME_ID,
  MEMBERS,
  clearSession,
  installApi,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { toastApiError } from "@/hooks/use-toast";
import { AccountFormDialog } from "./account-form-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("AccountFormDialog", () => {
  const onClose = vi.fn();
  const onSaved = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["crm.accounts.read", "crm.accounts.write"]);
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /accounts": () => ({ id: "new-id", name: "Yeni Firma" }),
    });
  });
  afterEach(clearSession);

  function open(account?: Parameters<typeof AccountFormDialog>[0]["account"]) {
    return renderWithProviders(
      <AccountFormDialog account={account} onClose={onClose} onSaved={onSaved} />
    );
  }

  it("requires the account name and does not call the API when it is empty", async () => {
    open();

    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("rejects a malformed email", async () => {
    open();

    await userEvent.type(screen.getByLabelText(/Müşteri adı/), "Acme");
    await userEvent.type(screen.getByLabelText("E-posta"), "nope");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Geçerli bir e-posta adresi girin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("creates the account with trimmed values, the current user as owner and no empty fields", async () => {
    open();

    await userEvent.type(screen.getByLabelText(/Müşteri adı/), "  Acme Ltd ");
    await userEvent.type(screen.getByLabelText("Sektör"), "Perakende");
    await userEvent.type(screen.getByLabelText("İl / İlçe"), "İstanbul");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/accounts", {
      name: "Acme Ltd",
      industry: "Perakende",
      ownerUserId: ME_ID,
      billingAddress: { city: "İstanbul" },
    });
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith("new-id"));
    expect(onClose).toHaveBeenCalled();
  });

  it("maps server validation errors (including nested address fields) onto the fields", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /accounts": () =>
        problem(400, {
          status: 400,
          title: "Validation",
          code: "validation",
          errors: { Name: ["Bu ad çok uzun"], "BillingAddress.City": ["Şehir geçersiz"] },
        }),
    });
    open();

    await userEvent.type(screen.getByLabelText(/Müşteri adı/), "Acme");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bu ad çok uzun")).toBeInTheDocument();
    expect(screen.getByText("Şehir geçersiz")).toBeInTheDocument();
    // Field errors replace the generic toast, and the dialog stays open for correction.
    expect(toastApiError).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("falls back to the error toast when the server error matches no field", async () => {
    const error = problem(400, {
      status: 400,
      title: "Owner",
      code: "owner.not_member",
    });
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /accounts": () => error,
    });
    open();

    await userEvent.type(screen.getByLabelText(/Müşteri adı/), "Acme");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
    // The code is translated through common:errors.<code>.
    expect(getApiErrorMessage(error)).toBe("Seçilen sahip bu organizasyonun aktif üyesi değil");
  });

  it("edits an existing account through PUT with the loaded values", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "PUT /accounts/a1": () => undefined,
    });
    open({
      id: "a1",
      name: "Eski Ad",
      industry: "Gıda",
      ownerUserId: "user-2",
      ownerName: "Grace Hopper",
      createdAt: "2026-01-01T00:00:00Z",
    });

    const name = screen.getByLabelText(/Müşteri adı/);
    expect(name).toHaveValue("Eski Ad");
    await userEvent.clear(name);
    await userEvent.type(name, "Yeni Ad");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put).toHaveBeenCalledWith("/accounts/a1", {
      name: "Yeni Ad",
      industry: "Gıda",
      ownerUserId: "user-2",
    });
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith("a1"));
  });
});
