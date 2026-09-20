/** M9C additions to the quote and order pages: negotiation, "Fatura oluştur", parity fields, invoice tabs. */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type ApiHandler,
  type MockClient,
} from "@/test/crm";
import { invoiceSummary, line } from "@/test/inventory";
import { toast, toastApiError } from "@/hooks/use-toast";
import { RelatedInvoicesTab } from "@/components/commerce/related-tabs";
import OrderDetailPage from "./order-detail";
import QuoteDetailPage from "./quote-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function order(overrides: Record<string, unknown> = {}) {
  return {
    id: "o1",
    number: "SO-2026-0001",
    subject: "Yıllık lisans siparişi",
    status: "confirmed",
    accountId: "a1",
    accountName: "Acme Ltd",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    currency: "TRY",
    subtotal: 200,
    discountTotal: 0,
    taxTotal: 40,
    adjustment: 0,
    grandTotal: 240,
    orderDate: "2026-09-01",
    createdAt: "2026-09-01T10:00:00Z",
    lines: [line()],
    ...overrides,
  };
}

function quote(overrides: Record<string, unknown> = {}) {
  return {
    id: "q1",
    number: "Q-2026-0001",
    subject: "Yıllık lisans",
    status: "sent",
    accountId: "a1",
    accountName: "Acme Ltd",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    currency: "TRY",
    subtotal: 200,
    discountTotal: 0,
    taxTotal: 40,
    adjustment: 0,
    grandTotal: 240,
    validUntil: "2099-10-19",
    createdAt: "2026-09-01T10:00:00Z",
    lines: [line()],
    ...overrides,
  };
}

const ORDER_READ = ["crm.orders.read", "crm.accounts.read", "crm.contacts.read", "crm.deals.read", "crm.quotes.read"];

function renderOrder() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/orders/:id" element={<OrderDetailPage />} />
        <Route path="/app/invoices/:id" element={<div>Fatura sayfası</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/orders/o1" }
  );
}

function orderRoutes(doc: object, extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return { "GET /orders/o1": () => doc, "GET /audit": () => ({ items: [], total: 0 }), ...extra };
}

async function orderLoaded() {
  await screen.findByRole("heading", { name: /SO-2026-0001/ });
}

describe("OrderDetailPage - 'Fatura oluştur' (order to invoice)", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const MATRIX: [status: string, invoicesWrite: boolean, invoiceId: string | undefined, button: boolean][] = [
    ["confirmed", true, undefined, true],
    ["fulfilled", true, undefined, true],
    ["draft", true, undefined, false],
    ["cancelled", true, undefined, false],
    ["confirmed", false, undefined, false],
    ["fulfilled", false, undefined, false],
    // An order that already has an active invoice never offers a second conversion.
    ["confirmed", true, "i1", false],
    ["fulfilled", true, "i1", false],
  ];

  it.each(MATRIX)("%s, invoices.write=%s, invoiceId=%s -> button %s", async (status, write, invoiceId, button) => {
    setPermissions([...ORDER_READ, "crm.orders.write", ...(write ? ["crm.invoices.write"] : [])]);
    installApi(client, orderRoutes(order({ status, invoiceId, invoiceNumber: invoiceId ? "INV-2026-0001" : undefined })));
    renderOrder();
    await orderLoaded();
    expect(!!screen.queryByRole("button", { name: "Fatura oluştur" })).toBe(button);
  });

  it("converts with the optional dates and opens the new invoice", async () => {
    setPermissions([...ORDER_READ, "crm.orders.write", "crm.invoices.write"]);
    installApi(
      client,
      orderRoutes(order(), { "POST /orders/o1/invoice": () => ({ id: "i9", number: "INV-2026-0009", status: "draft" }) })
    );
    renderOrder();
    await userEvent.click(await screen.findByRole("button", { name: "Fatura oluştur" }));
    const dialog = await screen.findByRole("dialog", { name: "Fatura oluştur" });
    await userEvent.type(within(dialog).getByLabelText("Son tarih"), "2026-10-15");
    await userEvent.click(within(dialog).getByRole("button", { name: "Fatura oluştur" }));

    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/invoices/i9"));
    expect(client.post).toHaveBeenCalledWith("/orders/o1/invoice", expect.objectContaining({ dueDate: "2026-10-15" }));
    expect(toast).toHaveBeenCalled();
  });

  it("sends nothing but an empty body when both dates are left empty", async () => {
    setPermissions([...ORDER_READ, "crm.orders.write", "crm.invoices.write"]);
    installApi(client, orderRoutes(order({ status: "fulfilled" }), { "POST /orders/o1/invoice": () => ({ id: "i9", number: "INV-1" }) }));
    renderOrder();
    await userEvent.click(await screen.findByRole("button", { name: "Fatura oluştur" }));
    const dialog = await screen.findByRole("dialog", { name: "Fatura oluştur" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Fatura oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const body = JSON.parse(JSON.stringify(client.post.mock.calls[0]?.[1]));
    expect(body).toEqual({});
  });

  it("refuses a due date before the invoice date on the client", async () => {
    setPermissions([...ORDER_READ, "crm.orders.write", "crm.invoices.write"]);
    installApi(client, orderRoutes(order()));
    renderOrder();
    await userEvent.click(await screen.findByRole("button", { name: "Fatura oluştur" }));
    const dialog = await screen.findByRole("dialog", { name: "Fatura oluştur" });
    await userEvent.type(within(dialog).getByLabelText("Fatura tarihi"), "2026-10-15");
    await userEvent.type(within(dialog).getByLabelText("Son tarih"), "2026-10-01");
    await userEvent.click(within(dialog).getByRole("button", { name: "Fatura oluştur" }));
    expect(await within(dialog).findByText("Son tarih, fatura tarihinden önce olamaz")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("toasts order.already_invoiced and stays on the order", async () => {
    setPermissions([...ORDER_READ, "crm.orders.write", "crm.invoices.write"]);
    const error = problem(409, { code: "order.already_invoiced" });
    installApi(client, orderRoutes(order(), { "POST /orders/o1/invoice": () => error }));
    renderOrder();
    await userEvent.click(await screen.findByRole("button", { name: "Fatura oluştur" }));
    const dialog = await screen.findByRole("dialog", { name: "Fatura oluştur" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Fatura oluştur" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/orders\/o1$/);
  });

  it("shows the invoice link and disables 'İptal et' (with the reason) for an order with an active invoice", async () => {
    setPermissions([...ORDER_READ, "crm.orders.write", "crm.invoices.read", "crm.invoices.write"]);
    installApi(client, orderRoutes(order({ invoiceId: "i1", invoiceNumber: "INV-2026-0001" })));
    renderOrder();
    await orderLoaded();

    expect(screen.getByRole("link", { name: "Fatura: INV-2026-0001" })).toHaveAttribute("href", "/app/invoices/i1");
    expect(screen.getByRole("button", { name: "İptal et" })).toBeDisabled();
    expect(screen.getByTitle(/önce faturayı iptal edin/)).toBeInTheDocument();
  });

  it("keeps 'İptal et' enabled for an order without an invoice", async () => {
    setPermissions([...ORDER_READ, "crm.orders.write"]);
    installApi(client, orderRoutes(order()));
    renderOrder();
    await orderLoaded();
    expect(screen.getByRole("button", { name: "İptal et" })).toBeEnabled();
  });

  it("shows the parity fields, the rounding line and the addresses of the order", async () => {
    setPermissions(ORDER_READ);
    installApi(
      client,
      orderRoutes(
        order({
          adjustment: -0.56,
          grandTotal: 239.44,
          customerPoNumber: "PO-77812",
          dueDate: "2026-10-20",
          exciseTax: 12.5,
          salesCommission: 150,
          pending: "Onay bekleniyor",
          priceBookName: "Kurumsal liste",
          carrier: "Yurtiçi Kargo",
          shippingAddress: { city: "Ankara", country: "Türkiye" },
        })
      )
    );
    renderOrder();
    await orderLoaded();

    for (const text of ["PO-77812", "Onay bekleniyor", "Kurumsal liste"]) {
      expect(screen.getByText(text)).toBeInTheDocument();
    }
    expect(screen.getByTestId("total-adjustment")).toHaveTextContent(/-.*0,56/);
    expect(screen.getByTestId("view-shipping-address")).toHaveTextContent("Ankara");
    expect(screen.getByText(/Yurtiçi Kargo/)).toBeInTheDocument();
  });
});

describe("QuoteDetailPage - negotiation stage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  function renderQuote() {
    return renderWithProviders(
      <Routes>
        <Route path="/app/quotes/:id" element={<QuoteDetailPage />} />
      </Routes>,
      { route: "/app/quotes/q1" }
    );
  }

  const READ = ["crm.quotes.read", "crm.quotes.write", "crm.accounts.read"];
  const NAMES = ["Müzakereye al", "Kabul et", "Süreyi uzat", "Reddet", "Taslağa al", "Gönder"] as const;

  async function actions() {
    await screen.findByRole("heading", { name: /Q-2026-0001/ });
    return NAMES.filter((name) => screen.queryByRole("button", { name }));
  }

  it("a sent quote can move to negotiation", async () => {
    setPermissions(READ);
    installApi(client, {
      "GET /quotes/q1": () => quote({ status: "sent" }),
      "GET /audit": () => ({ items: [], total: 0 }),
      "POST /quotes/q1/negotiate": () => undefined,
    });
    renderQuote();
    expect(await actions()).toEqual(["Müzakereye al", "Kabul et", "Süreyi uzat", "Reddet", "Taslağa al"]);

    await userEvent.click(screen.getByRole("button", { name: "Müzakereye al" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/quotes/q1/negotiate", {}));
    expect(toast).toHaveBeenCalled();
  });

  it("a quote in negotiation can be accepted, rejected, extended or reverted but not negotiated again", async () => {
    setPermissions(READ);
    installApi(client, { "GET /quotes/q1": () => quote({ status: "negotiation" }), "GET /audit": () => ({ items: [], total: 0 }) });
    renderQuote();
    expect(await actions()).toEqual(["Kabul et", "Süreyi uzat", "Reddet", "Taslağa al"]);
    expect(screen.getByText("Müzakere")).toBeInTheDocument();
  });

  it("negotiation is a write action: read-only users see none", async () => {
    setPermissions(["crm.quotes.read", "crm.accounts.read"]);
    installApi(client, { "GET /quotes/q1": () => quote({ status: "sent" }), "GET /audit": () => ({ items: [], total: 0 }) });
    renderQuote();
    expect(await actions()).toEqual([]);
  });

  it("toasts quote.invalid_transition", async () => {
    setPermissions(READ);
    const error = problem(409, { code: "quote.invalid_transition" });
    installApi(client, {
      "GET /quotes/q1": () => quote({ status: "sent" }),
      "GET /audit": () => ({ items: [], total: 0 }),
      "POST /quotes/q1/negotiate": () => error,
    });
    renderQuote();
    await userEvent.click(await screen.findByRole("button", { name: "Müzakereye al" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });
});

describe("RelatedInvoicesTab (account and deal 'Faturalar' tab)", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("asks for the account's invoices and shows number, status, total, balance and due date", async () => {
    setPermissions(["crm.invoices.read"]);
    installApi(client, {
      "GET /invoices": () =>
        page([invoiceSummary({ id: "i1", status: "overdue", balanceAmount: 240, dueDate: "2026-08-01" })]),
    });
    renderWithProviders(<RelatedInvoicesTab accountId="a1" />);

    expect(await screen.findByRole("link", { name: "INV-2026-0001" })).toHaveAttribute("href", "/app/invoices/i1");
    expect(screen.getByText("Vadesi geçti")).toBeInTheDocument();
    expect(client.get.mock.calls[0]?.[1].params).toMatchObject({ accountId: "a1", pageSize: 50 });
  });

  it("filters by deal and shows an empty state", async () => {
    setPermissions(["crm.invoices.read"]);
    installApi(client, { "GET /invoices": () => page([]) });
    renderWithProviders(<RelatedInvoicesTab dealId="d1" />);
    expect(await screen.findByText("Bu kayda ait fatura yok")).toBeInTheDocument();
    expect(client.get.mock.calls[0]?.[1].params).toMatchObject({ dealId: "d1" });
  });
});
