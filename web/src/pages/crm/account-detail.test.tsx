import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  MEMBERS,
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import AccountDetailPage from "./account-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ACCOUNT = {
  id: "a1",
  name: "Acme Ltd",
  industry: "Perakende",
  website: "acme.example",
  billingAddress: { city: "İstanbul", country: "TR" },
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  contactCount: 1,
  dealCount: 1,
  createdAt: "2026-05-01T10:00:00Z",
};

function renderPage(route = "/app/accounts/a1") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/accounts/:id" element={<AccountDetailPage />} />
        <Route path="/app/accounts" element={<div>list</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("AccountDetailPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /accounts/a1": () => ACCOUNT,
      "GET /accounts/a1/contacts": () => [
        {
          id: "c1",
          fullName: "Ayşe Yılmaz",
          lastName: "Yılmaz",
          email: "ayse@acme.example",
          ownerUserId: "user-1",
          createdAt: "x",
        },
      ],
      "GET /accounts/a1/deals": () => [
        {
          id: "d1",
          name: "Alfa",
          accountId: "a1",
          accountName: "Acme Ltd",
          stageName: "Teklif",
          stageKind: "open",
          amount: 1000,
          currency: "TRY",
          closingDate: "2026-12-31",
        },
      ],
      "GET /audit": () => ({
        total: 1,
        items: [
          {
            id: "e1",
            entityType: "Account",
            entityId: "a1",
            action: "created",
            occurredAt: "2026-05-01T10:00:00Z",
            changes: { name: { old: null, new: "Acme Ltd" } },
          },
        ],
      }),
      "GET /organization/members": () => MEMBERS,
      "GET /pipelines": () => [],
      "GET /contacts": () => page([]),
      "GET /accounts": () => page([]),
    });
  });
  afterEach(clearSession);

  it("shows the info panel and General tab, then related records and audit on their tabs", async () => {
    setPermissions(["crm.accounts.read", "crm.contacts.read", "crm.deals.read"]);
    renderPage();

    expect(await screen.findByRole("heading", { name: "Acme Ltd" })).toBeInTheDocument();
    expect(screen.getByText("İstanbul, TR")).toBeInTheDocument();
    expect(screen.getByText("1 kişi")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("tab", { name: "İlişkili kayıtlar" }));
    expect(await screen.findByRole("link", { name: "Ayşe Yılmaz" })).toHaveAttribute(
      "href",
      "/app/contacts/c1"
    );
    expect(screen.getByRole("link", { name: "Alfa" })).toHaveAttribute("href", "/app/deals/d1");
    expect(screen.getByTestId("location")).toHaveTextContent("?tab=related");

    await userEvent.click(screen.getByRole("tab", { name: "Denetim" }));
    expect(await screen.findByTestId("audit-entry")).toBeInTheDocument();
    expect(client.get).toHaveBeenCalledWith("/audit", {
      params: { entityType: "Account", entityId: "a1", page: 1, pageSize: 20 },
    });
  });

  it("restores the active tab from the URL", async () => {
    setPermissions(["crm.accounts.read"]);
    renderPage("/app/accounts/a1?tab=audit");

    expect(await screen.findByTestId("audit-entry")).toBeInTheDocument();
  });

  it("hides edit and delete without write permission", async () => {
    setPermissions(["crm.accounts.read"]);
    renderPage();

    await screen.findByRole("heading", { name: "Acme Ltd" });
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
  });

  it("deletes and returns to the list", async () => {
    setPermissions(["crm.accounts.read", "crm.accounts.write"]);
    installApi(client, {
      "GET /accounts/a1": () => ACCOUNT,
      "DELETE /accounts/a1": () => undefined,
      "GET /audit": () => ({ items: [], total: 0 }),
    });
    renderPage();

    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));

    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/accounts/a1"));
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/accounts$/)
    );
  });

  it("shows a not-found state for a missing account", async () => {
    setPermissions(["crm.accounts.read"]);
    installApi(client, {
      "GET /accounts/a1": () =>
        problem(404, { status: 404, title: "Not found", code: "not_found" }),
    });
    renderPage();

    expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
  });
  describe("website rendering", () => {
    function showWith(website: string) {
      setPermissions(["crm.accounts.read"]);
      installApi(client, {
        "GET /accounts/a1": () => ({ ...ACCOUNT, website }),
        "GET /audit": () => ({ items: [], total: 0 }),
      });
      renderPage();
    }

    it("renders an https website as a safe external link", async () => {
      showWith("https://acme.example/tr");

      const link = await screen.findByRole("link", { name: "https://acme.example/tr" });
      expect(link).toHaveAttribute("href", "https://acme.example/tr");
      expect(link).toHaveAttribute("target", "_blank");
      expect(link).toHaveAttribute("rel", expect.stringContaining("noopener"));
    });

    it.each(["javascript:alert(document.cookie)", "data:text/html,<b>x</b>", "acme.example"])(
      "shows %s as plain text, never as a link",
      async (website) => {
        showWith(website);

        expect(await screen.findByText(website)).toBeInTheDocument();
        expect(screen.queryByRole("link", { name: website })).not.toBeInTheDocument();
        expect(document.querySelector('a[href^="javascript:"], a[href^="data:"]')).toBeNull();
      }
    );
  });
});
