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
  type ApiHandler,
  type MockClient,
} from "@/test/crm";
import { toastApiError } from "@/hooks/use-toast";
import OrderDetailPage from "./order-detail";
import OrderEditorPage from "./order-editor";
import OrdersPage from "./orders";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const order = (over: Record<string, unknown> = {}) => ({
  id: "o1",
  number: "SO-2026-0001",
  subject: "Yıllık lisans siparişi",
  status: "draft",
  accountId: "a1",
  accountName: "Acme Ltd",
  quoteId: "q1",
  quoteNumber: "Q-2026-0001",
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  currency: "TRY",
  grandTotal: 240,
  subtotal: 200,
  discountTotal: 0,
  taxTotal: 40,
  orderDate: "2026-09-01",
  createdAt: "2026-05-01T10:00:00Z",
  lines: [
    {
      id: "l1",
      position: 0,
      description: "CRM Pro lisansı",
      quantity: 2,
      unitPrice: 100,
      discountPercent: 0,
      taxRate: 20,
      lineSubtotal: 200,
      lineDiscount: 0,
      lineTax: 40,
      lineTotal: 240,
    },
  ],
  ...over,
});

const READ = ["crm.orders.read", "crm.accounts.read", "crm.quotes.read"];

function detailRoutes(o: object, extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return { "GET /orders/o1": () => o, "GET /audit": () => ({ items: [], total: 0 }), ...extra };
}

function renderDetail() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/orders/:id" element={<OrderDetailPage />} />
        <Route path="/app/orders" element={<div>Sipariş listesi</div>} />
        <Route path="/app/orders/:id/edit" element={<div>Sipariş düzenleme</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/orders/o1" }
  );
}

const ACTIONS = ["Onayla", "Teslim edildi", "Düzenle", "İptal et", "Sil"] as const;
async function visibleActions(): Promise<string[]> {
  await screen.findByRole("heading", { name: /SO-2026-0001/ });
  return ACTIONS.filter((name) => screen.queryByRole("button", { name }));
}

describe("OrderDetailPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const MATRIX: [status: string, write: boolean, expected: string[]][] = [
    ["draft", true, ["Onayla", "Düzenle", "İptal et", "Sil"]],
    ["draft", false, []],
    ["confirmed", true, ["Teslim edildi", "İptal et"]],
    ["confirmed", false, []],
    ["fulfilled", true, []],
    ["cancelled", true, []],
  ];

  it.each(MATRIX)("%s, orders.write=%s", async (status, write, expected) => {
    setPermissions([...READ, ...(write ? ["crm.orders.write"] : [])]);
    installApi(client, detailRoutes(order({ status })));
    renderDetail();
    expect(await visibleActions()).toEqual(expected);
  });

  it("shows the source quote link, the lines, totals and an audit tab for SalesOrder", async () => {
    setPermissions(READ);
    installApi(client, detailRoutes(order()));
    renderDetail();

    expect(await screen.findByRole("link", { name: "Q-2026-0001" })).toHaveAttribute("href", "/app/quotes/q1");
    expect(screen.getByText("CRM Pro lisansı")).toBeInTheDocument();
    expect(screen.getByTestId("total-grand")).toHaveTextContent("240,00");

    await userEvent.click(screen.getByRole("tab", { name: "Denetim" }));
    await waitFor(() =>
      expect(client.get).toHaveBeenCalledWith(
        "/audit",
        expect.objectContaining({ params: expect.objectContaining({ entityType: "SalesOrder", entityId: "o1" }) })
      )
    );
  });

  it("confirms and fulfills through their endpoints", async () => {
    setPermissions([...READ, "crm.orders.write"]);
    installApi(
      client,
      detailRoutes(order(), { "POST /orders/o1/confirm": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Onayla" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/orders/o1/confirm", {}));
  });

  it("cancels a confirmed order with a reason from the dialog", async () => {
    setPermissions([...READ, "crm.orders.write"]);
    installApi(
      client,
      detailRoutes(order({ status: "confirmed" }), { "POST /orders/o1/cancel": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "İptal et" }));

    const dialog = await screen.findByRole("dialog", { name: "Siparişi iptal et" });
    await userEvent.type(within(dialog).getByLabelText(/İptal nedeni/), "Müşteri vazgeçti");
    await userEvent.click(within(dialog).getByRole("button", { name: "İptal et" }));
    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/orders/o1/cancel", { reason: "Müşteri vazgeçti" })
    );
  });

  it("reports a rejected transition (order.invalid_transition) as an error toast", async () => {
    setPermissions([...READ, "crm.orders.write"]);
    const error = problem(409, { code: "order.invalid_transition" });
    installApi(
      client,
      detailRoutes(order({ status: "confirmed" }), { "POST /orders/o1/fulfill": () => error })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Teslim edildi" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  it("deletes a draft after a confirmation", async () => {
    setPermissions([...READ, "crm.orders.write"]);
    installApi(client, detailRoutes(order(), { "DELETE /orders/o1": () => undefined }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/orders/o1"));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/orders$/));
  });
});

describe("OrdersPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /orders": () =>
        page([
          order(),
          order({ id: "o2", number: "SO-2026-0002", status: "confirmed", quoteId: undefined, quoteNumber: undefined }),
        ]),
      "GET /organization/members": () => MEMBERS,
    });
  });
  afterEach(clearSession);

  it("lists orders with status, order date and the quote link, and syncs the status filter", async () => {
    setPermissions(["crm.orders.read", "crm.quotes.read"]);
    renderWithProviders(
      <>
        <Routes>
          <Route path="/app/orders" element={<OrdersPage />} />
        </Routes>
        <LocationDisplay />
      </>,
      { route: "/app/orders" }
    );

    expect(await screen.findByRole("link", { name: "SO-2026-0001" })).toHaveAttribute("href", "/app/orders/o1");
    expect(screen.getByRole("link", { name: "Q-2026-0001" })).toHaveAttribute("href", "/app/quotes/q1");
    expect(within(screen.getByRole("table")).getByText("Onaylandı")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Onaylandı" }));
    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/orders").at(-1)?.[1];
      expect(last.params).toEqual({ page: 1, pageSize: 25, status: "confirmed" });
    });
    expect(screen.getByTestId("location")).toHaveTextContent("/app/orders?status=confirmed");
    // No write permission: no create / edit / delete controls.
    expect(screen.queryByRole("button", { name: "Yeni sipariş" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
  });
});

describe("OrderEditorPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  function renderEditor(route: string) {
    return renderWithProviders(
      <>
        <Routes>
          <Route path="/app/orders/new" element={<OrderEditorPage />} />
          <Route path="/app/orders/:id/edit" element={<OrderEditorPage />} />
          <Route path="/app/orders/:id" element={<div>Sipariş detay sayfası</div>} />
        </Routes>
        <LocationDisplay />
      </>,
      { route }
    );
  }

  it("creates a direct order with the shared line grid (orderDate instead of validUntil)", async () => {
    setPermissions(["crm.orders.read", "crm.orders.write", "crm.accounts.read"]);
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
      "GET /accounts/a1": () => ({ id: "a1", name: "Acme Ltd" }),
      "POST /orders": () => order({ id: "o-new" }),
    });
    renderEditor("/app/orders/new?accountId=a1");
    await screen.findByRole("textbox", { name: "Müşteri" });
    expect(screen.queryByLabelText("Geçerlilik tarihi")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Sahip" })).not.toBeDisabled());

    await userEvent.type(screen.getByLabelText(/^Konu/), "Doğrudan sipariş");
    await userEvent.type(screen.getByLabelText("Açıklama 1"), "Kalem");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const [url, body] = client.post.mock.calls[0] as [string, Record<string, unknown>];
    expect(url).toBe("/orders");
    expect(body).toMatchObject({
      subject: "Doğrudan sipariş",
      accountId: "a1",
      currency: "TRY",
      adjustment: 0,
      lines: [{ description: "Kalem", quantity: 1, unitPrice: 0, discountPercent: 0, taxRate: 0 }],
    });
    expect(body).not.toHaveProperty("validUntil");
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/orders/o-new"));
  });

  it.each(["confirmed", "fulfilled", "cancelled"])(
    "redirects the edit route of a %s order to its detail page",
    async (status) => {
      setPermissions(["crm.orders.read", "crm.orders.write"]);
      installApi(client, { "GET /orders/o1": () => order({ status }) });
      renderEditor("/app/orders/o1/edit");
      expect(await screen.findByText("Sipariş detay sayfası")).toBeInTheDocument();
    }
  );
});
