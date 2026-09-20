import type { ComponentType } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { MEMBERS, clearSession, installApi, page, setPermissions, type MockClient } from "@/test/crm";
import { invoiceSummary } from "@/test/inventory";
import AccountDetailPage from "./account-detail";
import DealDetailPage from "./deal-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const base = { ownerUserId: "user-1", ownerName: "Ada Lovelace", createdAt: "2026-05-01T10:00:00Z" };

interface Scenario {
  name: string;
  path: string;
  Page: ComponentType;
  read: string;
  record: object;
  param: "accountId" | "dealId";
}

const SCENARIOS: Scenario[] = [
  { name: "account", path: "accounts", Page: AccountDetailPage, read: "crm.accounts.read", record: { id: "r1", name: "Acme Ltd", ...base }, param: "accountId" },
  {
    name: "deal",
    path: "deals",
    Page: DealDetailPage,
    read: "crm.deals.read",
    record: {
      id: "r1",
      name: "Acme fırsatı",
      accountId: "a1",
      accountName: "Acme Ltd",
      pipelineId: "p1",
      pipelineName: "Ana huni",
      stageId: "s1",
      stageName: "Teklif",
      stageKind: "open",
      probability: 50,
      currency: "TRY",
      ...base,
    },
    param: "dealId",
  },
];

const invoiceRequests = (param: string) =>
  client.get.mock.calls.filter(([url, config]) => url === "/invoices" && config?.params?.[param]);

describe.each(SCENARIOS)("$name detail: Faturalar tab", (s) => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      [`GET /${s.path}/r1`]: () => s.record,
      "GET /invoices": () => page([invoiceSummary({ status: "overdue", balanceAmount: 240 })]),
      "GET /pricebooks/accounts/r1/default": () => ({ priceBookId: "pb1", priceBookName: "Kurumsal liste", isEffective: true }),
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
      "GET /contacts": () => page([]),
      "GET /deals": () => page([]),
      "GET /audit": () => ({ items: [], total: 0 }),
      "GET /pipelines": () => [],
      "GET /activities": () => page([]),
      "GET /cases": () => page([]),
      "GET /workflows/executions": () => page([]),
    });
  });
  afterEach(clearSession);

  function renderPage() {
    return renderWithProviders(
      <Routes>
        <Route path={`/app/${s.path}/:id`} element={<s.Page />} />
      </Routes>,
      { route: `/app/${s.path}/r1` }
    );
  }

  it("shows the tab with crm.invoices.read and asks for the record's invoices only when it opens", async () => {
    setPermissions([s.read, "crm.invoices.read"]);
    renderPage();

    const tab = await screen.findByRole("tab", { name: "Faturalar" });
    expect(invoiceRequests(s.param)).toHaveLength(0);
    await userEvent.click(tab);

    expect(await screen.findByRole("link", { name: "INV-2026-0001" })).toHaveAttribute("href", "/app/invoices/i1");
    expect(screen.getByText("Vadesi geçti")).toBeInTheDocument();
    expect(invoiceRequests(s.param)[0]?.[1].params).toMatchObject({ [s.param]: "r1" });
  });

  it("has no tab and sends no request without crm.invoices.read", async () => {
    setPermissions([s.read]);
    renderPage();
    await screen.findByRole("tab", { name: /Genel|General/ });
    expect(screen.queryByRole("tab", { name: "Faturalar" })).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([url]) => url === "/invoices")).toBe(false);
  });
});

describe("account detail: default price book row", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /accounts/r1": () => ({ id: "r1", name: "Acme Ltd", ...base }),
      "GET /pricebooks/accounts/r1/default": () => ({ priceBookId: "pb1", priceBookName: "Kurumsal liste", isEffective: true }),
      "GET /organization/members": () => MEMBERS,
      "GET /contacts": () => page([]),
      "GET /deals": () => page([]),
      "GET /audit": () => ({ items: [], total: 0 }),
    });
  });
  afterEach(clearSession);

  function renderAccount() {
    return renderWithProviders(
      <Routes>
        <Route path="/app/accounts/:id" element={<AccountDetailPage />} />
      </Routes>,
      { route: "/app/accounts/r1" }
    );
  }

  it("shows the account's default price book with crm.pricebooks.read", async () => {
    setPermissions(["crm.accounts.read", "crm.pricebooks.read"]);
    renderAccount();
    expect(await screen.findByRole("link", { name: "Kurumsal liste" })).toHaveAttribute("href", "/app/pricebooks/pb1");
  });

  it("shows nothing and asks for nothing without it", async () => {
    setPermissions(["crm.accounts.read"]);
    renderAccount();
    await screen.findByRole("heading", { name: "Acme Ltd" });
    await waitFor(() => expect(screen.queryByTestId("account-price-book")).not.toBeInTheDocument());
    expect(client.get.mock.calls.some(([url]) => String(url).includes("/pricebooks"))).toBe(false);
  });
});
