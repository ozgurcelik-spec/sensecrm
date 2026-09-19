import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
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
  setPermissions,
  type MockClient,
} from "@/test/crm";
import AccountDetailPage from "@/pages/crm/account-detail";
import DealDetailPage from "@/pages/crm/deal-detail";
import QuoteEditorPage from "./quote-editor";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const base = { ownerUserId: "user-1", ownerName: "Ada Lovelace", createdAt: "2026-05-01T10:00:00Z" };
const ACCOUNT = { id: "a1", name: "Acme Ltd", contactCount: 0, dealCount: 0, ...base };
const DEAL = {
  id: "d1",
  name: "Alfa fırsatı",
  accountId: "a1",
  accountName: "Acme Ltd",
  contactId: "c1",
  contactName: "Ayşe Yılmaz",
  pipelineId: "p1",
  pipelineName: "Ana",
  stageId: "s1",
  stageName: "Teklif",
  stageKind: "open",
  probability: 50,
  currency: "EUR",
  ...base,
};
const QUOTE = {
  id: "q1",
  number: "Q-2026-0001",
  subject: "Alfa teklifi",
  status: "sent",
  accountId: "a1",
  currency: "TRY",
  grandTotal: 500,
  validUntil: "2026-10-19",
  ownerUserId: "user-1",
  createdAt: "2026-05-01T10:00:00Z",
};
const ORDER = {
  id: "o1",
  number: "SO-2026-0001",
  subject: "Alfa siparişi",
  status: "confirmed",
  accountId: "a1",
  currency: "TRY",
  grandTotal: 800,
  orderDate: "2026-09-01",
  ownerUserId: "user-1",
  createdAt: "2026-05-01T10:00:00Z",
};

const routes = {
  "GET /accounts/a1": () => ACCOUNT,
  "GET /accounts/a1/contacts": () => [],
  "GET /accounts/a1/deals": () => [],
  "GET /deals/d1": () => DEAL,
  "GET /pipelines": () => [],
  "GET /quotes": () => page([QUOTE]),
  "GET /orders": () => page([ORDER]),
  "GET /audit": () => ({ items: [], total: 0 }),
  "GET /organization/members": () => MEMBERS,
  "GET /contacts": () => page([]),
  "GET /deals": () => page([]),
  "GET /accounts": () => page([ACCOUNT]),
};

function renderAt(route: string) {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/accounts/:id" element={<AccountDetailPage />} />
        <Route path="/app/deals/:id" element={<DealDetailPage />} />
        <Route path="/app/quotes/new" element={<QuoteEditorPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("Teklif oluştur and the Teklifler / Siparişler tabs", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, routes);
  });
  afterEach(clearSession);

  it("deal detail: the action opens the quote editor with the deal's ids and the editor prefills from the deal", async () => {
    setPermissions(["crm.deals.read", "crm.quotes.write", "crm.accounts.read"]);
    renderAt("/app/deals/d1");

    await userEvent.click(await screen.findByRole("link", { name: "Teklif oluştur" }));
    // Only ids travel in the URL.
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/quotes/new?accountId=a1&contactId=c1&dealId=d1"
    );
    expect(await screen.findByLabelText(/^Konu/)).toHaveValue("Alfa fırsatı");
    expect(screen.getByRole("combobox", { name: "Para birimi" })).toHaveValue("EUR");
  });

  it("account detail: the action opens the editor with the account only", async () => {
    setPermissions(["crm.accounts.read", "crm.quotes.write"]);
    renderAt("/app/accounts/a1");

    await userEvent.click(await screen.findByRole("link", { name: "Teklif oluştur" }));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/quotes\/new\?accountId=a1$/);
  });

  it.each(["deals", "accounts"] as const)("%s: no 'Teklif oluştur' without crm.quotes.write", async (kind) => {
    setPermissions([kind === "deals" ? "crm.deals.read" : "crm.accounts.read", "crm.quotes.read"]);
    renderAt(kind === "deals" ? "/app/deals/d1" : "/app/accounts/a1");
    await screen.findByRole("tab", { name: "Genel" });
    expect(screen.queryByRole("link", { name: "Teklif oluştur" })).not.toBeInTheDocument();
  });

  it("deal detail: the Teklifler tab lists the deal's quotes (GET /quotes?dealId=)", async () => {
    setPermissions(["crm.deals.read", "crm.quotes.read"]);
    renderAt("/app/deals/d1");

    await userEvent.click(await screen.findByRole("tab", { name: "Teklifler" }));
    expect(await screen.findByRole("link", { name: "Q-2026-0001" })).toHaveAttribute("href", "/app/quotes/q1");
    const params = client.get.mock.calls.find(([url]) => url === "/quotes")?.[1].params;
    expect(params).toMatchObject({ dealId: "d1" });
    expect(params).not.toHaveProperty("accountId");
  });

  it("account detail: Teklifler and Siparişler tabs filter by account and follow their own permission", async () => {
    setPermissions(["crm.accounts.read", "crm.quotes.read", "crm.orders.read"]);
    renderAt("/app/accounts/a1");

    await userEvent.click(await screen.findByRole("tab", { name: "Teklifler" }));
    await screen.findByRole("link", { name: "Q-2026-0001" });
    expect(client.get.mock.calls.find(([url]) => url === "/quotes")?.[1].params).toMatchObject({
      accountId: "a1",
    });

    await userEvent.click(screen.getByRole("tab", { name: "Siparişler" }));
    expect(await screen.findByRole("link", { name: "SO-2026-0001" })).toHaveAttribute("href", "/app/orders/o1");
    expect(client.get.mock.calls.find(([url]) => url === "/orders")?.[1].params).toMatchObject({
      accountId: "a1",
    });
  });

  it("hides the tabs without the read permissions and never requests the lists", async () => {
    setPermissions(["crm.accounts.read", "crm.deals.read"]);
    renderAt("/app/accounts/a1");
    await screen.findByRole("tab", { name: "Genel" });
    expect(screen.queryByRole("tab", { name: "Teklifler" })).not.toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: "Siparişler" })).not.toBeInTheDocument();
    await waitFor(() => expect(client.get).toHaveBeenCalled());
    expect(client.get.mock.calls.some(([url]) => url === "/quotes" || url === "/orders")).toBe(false);
  });
});
