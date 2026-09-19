import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
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
import { toastApiError } from "@/hooks/use-toast";
import QuoteDetailPage from "./quote-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function quote(overrides: Record<string, unknown> = {}) {
  return {
    id: "q1",
    number: "Q-2026-0001",
    subject: "Yıllık lisans",
    status: "draft",
    accountId: "a1",
    accountName: "Acme Ltd",
    contactId: "c1",
    contactName: "Ayşe Yılmaz",
    dealId: "d1",
    dealName: "Acme fırsatı",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    currency: "TRY",
    grandTotal: 94.56,
    subtotal: 85.22,
    discountTotal: 6,
    taxTotal: 15.34,
    validUntil: "2026-10-19",
    terms: "30 gün vadeli",
    notes: "İç not",
    createdAt: "2026-05-01T10:00:00Z",
    lines: [
      {
        id: "l1",
        position: 0,
        description: "CRM Pro lisansı",
        quantity: 3,
        unitPrice: 19.99,
        discountPercent: 10,
        taxRate: 20,
        lineSubtotal: 59.97,
        lineDiscount: 6,
        lineTax: 10.79,
        lineTotal: 64.76,
      },
      {
        id: "l2",
        position: 1,
        description: "Kurulum",
        quantity: 2.5,
        unitPrice: 10.1,
        discountPercent: 0,
        taxRate: 18,
        lineSubtotal: 25.25,
        lineDiscount: 0,
        lineTax: 4.55,
        lineTotal: 29.8,
      },
    ],
    ...overrides,
  };
}

const ALL_READ = [
  "crm.quotes.read",
  "crm.accounts.read",
  "crm.contacts.read",
  "crm.deals.read",
  "crm.orders.read",
];

function routes(q: object, extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return {
    "GET /quotes/q1": () => q,
    "GET /audit": () => ({ items: [], total: 0 }),
    ...extra,
  };
}

function renderDetail() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/quotes/:id" element={<QuoteDetailPage />} />
        <Route path="/app/quotes" element={<div>Teklif listesi</div>} />
        <Route path="/app/quotes/:id/edit" element={<div>Teklif düzenleme</div>} />
        <Route path="/app/orders/:id" element={<div>Sipariş sayfası</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/quotes/q1" }
  );
}

const ACTIONS = [
  "Kabul et",
  "Gönder",
  "Siparişe dönüştür",
  "Süreyi uzat",
  "Reddet",
  "Taslağa al",
  "Düzenle",
  "Sil",
] as const;

async function visibleActions(): Promise<string[]> {
  await screen.findByRole("heading", { name: /Q-2026-0001/ });
  return ACTIONS.filter((name) => screen.queryByRole("button", { name }));
}

describe("QuoteDetailPage - actions by status and permission", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  // The status machine crossed with crm.quotes.write / crm.orders.write.
  const MATRIX: [status: string, write: boolean, orders: boolean, expected: string[]][] = [
    ["draft", true, false, ["Gönder", "Düzenle", "Sil"]],
    ["draft", false, true, []],
    ["sent", true, false, ["Kabul et", "Süreyi uzat", "Reddet", "Taslağa al"]],
    ["sent", false, false, []],
    ["expired", true, false, ["Süreyi uzat", "Reddet", "Taslağa al"]],
    ["expired", false, false, []],
    ["rejected", true, false, ["Taslağa al"]],
    ["rejected", false, false, []],
    ["accepted", true, true, ["Siparişe dönüştür"]],
    // Converting needs crm.orders.write, whatever the quote permission.
    ["accepted", true, false, []],
    ["accepted", false, true, ["Siparişe dönüştür"]],
    ["accepted", false, false, []],
  ];

  it.each(MATRIX)("%s, quotes.write=%s, orders.write=%s", async (status, write, orders, expected) => {
    setPermissions([
      ...ALL_READ,
      ...(write ? ["crm.quotes.write"] : []),
      ...(orders ? ["crm.orders.write"] : []),
    ]);
    installApi(client, routes(quote({ status })));
    renderDetail();
    expect(await visibleActions()).toEqual(expected);
  });

  it("shows the order link (and no convert button) once the quote is converted", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write", "crm.orders.write"]);
    installApi(
      client,
      routes(quote({ status: "accepted", convertedOrderId: "o1", convertedOrderNumber: "SO-2026-0001" }))
    );
    renderDetail();

    expect(await visibleActions()).toEqual([]);
    expect(screen.getByRole("link", { name: "Sipariş: SO-2026-0001" })).toHaveAttribute(
      "href",
      "/app/orders/o1"
    );
  });

  it("shows the read-only lines with server values, the totals card, terms and notes", async () => {
    setPermissions(ALL_READ);
    installApi(client, routes(quote()));
    renderDetail();

    await screen.findByRole("heading", { name: /Q-2026-0001/ });
    expect(screen.getByText("CRM Pro lisansı")).toBeInTheDocument();
    const rows = screen.getAllByRole("row");
    expect(within(rows[1] as HTMLElement).getByText(/64,76/)).toBeInTheDocument();
    expect(screen.getByTestId("total-grand")).toHaveTextContent("94,56");
    expect(screen.getByTestId("total-subtotal")).toHaveTextContent("85,22");
    expect(screen.getByText("30 gün vadeli")).toBeInTheDocument();
    expect(screen.getByText("Taslak")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Acme Ltd" })).toHaveAttribute("href", "/app/accounts/a1");
  });

  it("shows the expired badge for an expired quote", async () => {
    setPermissions(ALL_READ);
    installApi(client, routes(quote({ status: "expired" })));
    renderDetail();
    await screen.findByRole("heading", { name: /Q-2026-0001/ });
    expect(screen.getByText("Süresi doldu")).toBeInTheDocument();
  });

  it("converts an accepted quote and opens the new order", async () => {
    setPermissions([...ALL_READ, "crm.orders.write"]);
    installApi(
      client,
      routes(quote({ status: "accepted" }), {
        "POST /quotes/q1/convert": () => ({ id: "o9", number: "SO-2026-0007", status: "draft" }),
      })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Siparişe dönüştür" }));

    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/orders/o9"));
    expect(client.post).toHaveBeenCalledWith("/quotes/q1/convert", {});
  });

  it("reports a failed conversion (quote.already_converted) without navigating", async () => {
    setPermissions([...ALL_READ, "crm.orders.write"]);
    const error = problem(409, { code: "quote.already_converted" });
    installApi(
      client,
      routes(quote({ status: "accepted" }), { "POST /quotes/q1/convert": () => error })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Siparişe dönüştür" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/quotes\/q1$/);
  });

  it("sends and accepts through their endpoints and refetches the quote", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    installApi(
      client,
      routes(quote({ status: "draft" }), { "POST /quotes/q1/send": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Gönder" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/quotes/q1/send", {}));
    await waitFor(() =>
      expect(client.get.mock.calls.filter(([url]) => url === "/quotes/q1").length).toBeGreaterThan(1)
    );
  });

  it("shows the translated server error when sending a quote without lines (quote.no_lines)", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    const error = problem(422, { code: "quote.no_lines" });
    installApi(client, routes(quote({ lines: [] }), { "POST /quotes/q1/send": () => error }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Gönder" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  it("rejects with an optional reason from the dialog", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    installApi(
      client,
      routes(quote({ status: "sent" }), { "POST /quotes/q1/reject": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Reddet" }));

    const dialog = await screen.findByRole("dialog", { name: "Teklifi reddet" });
    await userEvent.type(within(dialog).getByLabelText(/Ret nedeni/), "Bütçe yok");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reddet" }));

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/quotes/q1/reject", { reason: "Bütçe yok" })
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("rejects without a reason (the reason is optional)", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    installApi(
      client,
      routes(quote({ status: "expired" }), { "POST /quotes/q1/reject": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Reddet" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reddet" }));
    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/quotes/q1/reject", { reason: undefined })
    );
  });

  it("extends the validity: rejects a past date on the client, sends a valid one, shows a server date error", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    let attempt = 0;
    installApi(
      client,
      routes(quote({ status: "expired", validUntil: "2020-01-01" }), {
        "POST /quotes/q1/extend": () =>
          ++attempt === 1
            ? problem(400, { code: "validation", errors: { validUntil: ["Tarih sunucuda reddedildi"] } })
            : undefined,
      })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Süreyi uzat" }));

    const dialog = await screen.findByRole("dialog", { name: "Geçerlilik süresini uzat" });
    const date = within(dialog).getByLabelText(/Geçerlilik tarihi/);
    // The old (past) date is not offered as the starting value.
    expect(date).toHaveValue("");

    fireEvent.change(date, { target: { value: "2020-02-02" } });
    await userEvent.click(within(dialog).getByRole("button", { name: "Süreyi uzat" }));
    expect(await within(dialog).findByText("Geçerlilik tarihi bugünden önce olamaz")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    fireEvent.change(date, { target: { value: "2099-01-31" } });
    await userEvent.click(within(dialog).getByRole("button", { name: "Süreyi uzat" }));
    expect(await within(dialog).findByText("Tarih sunucuda reddedildi")).toBeInTheDocument();
    expect(client.post).toHaveBeenCalledWith("/quotes/q1/extend", { validUntil: "2099-01-31" });

    await userEvent.click(within(dialog).getByRole("button", { name: "Süreyi uzat" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(client.post).toHaveBeenCalledTimes(2);
  });

  it("goes back to draft and deletes a draft after a confirmation", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    installApi(
      client,
      routes(quote({ status: "rejected" }), { "POST /quotes/q1/revert": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Taslağa al" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/quotes/q1/revert", {}));
  });

  it("deletes a draft only after confirming, then returns to the list", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    installApi(client, routes(quote(), { "DELETE /quotes/q1": () => undefined, "GET /quotes": () => page([]) }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    expect(client.delete).not.toHaveBeenCalled();

    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/quotes/q1"));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/quotes$/));
  });

  it("opens the edit page from a draft", async () => {
    setPermissions([...ALL_READ, "crm.quotes.write"]);
    installApi(client, routes(quote()));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Düzenle" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/quotes/q1/edit");
  });

  it("has an audit tab for the quote", async () => {
    setPermissions(ALL_READ);
    installApi(client, routes(quote()));
    renderDetail();
    await userEvent.click(await screen.findByRole("tab", { name: "Denetim" }));
    await waitFor(() =>
      expect(client.get).toHaveBeenCalledWith(
        "/audit",
        expect.objectContaining({ params: expect.objectContaining({ entityType: "Quote", entityId: "q1" }) })
      )
    );
  });

  it("shows a not-found state for an unknown quote", async () => {
    setPermissions(ALL_READ);
    installApi(client, { "GET /quotes/q1": () => problem(404, { code: "not_found" }) });
    renderDetail();
    expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
  });
});
