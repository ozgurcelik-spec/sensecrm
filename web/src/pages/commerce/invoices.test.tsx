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
import { invoice, invoiceSummary, payment } from "@/test/inventory";
import { toast, toastApiError } from "@/hooks/use-toast";
import InvoiceDetailPage from "./invoice-detail";
import InvoicesPage from "./invoices";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const READ = ["crm.invoices.read", "crm.accounts.read", "crm.contacts.read", "crm.deals.read", "crm.orders.read"];

function renderList(route = "/app/invoices") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/invoices" element={<InvoicesPage />} />
        <Route path="/app/invoices/new" element={<div>Yeni fatura sayfası</div>} />
        <Route path="/app/invoices/:id" element={<div>Fatura detayı</div>} />
        <Route path="/app/invoices/:id/edit" element={<div>Fatura düzenleme</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("InvoicesPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const ROWS = [
    invoiceSummary({ id: "i1", number: "INV-2026-0001", status: "overdue", balanceAmount: 240, dueDate: "2026-08-01" }),
    invoiceSummary({ id: "i2", number: "INV-2026-0002", status: "partiallyPaid", paidAmount: 100, balanceAmount: 140 }),
    invoiceSummary({ id: "i3", number: "INV-2026-0003", status: "paid", paidAmount: 240, balanceAmount: 0 }),
    invoiceSummary({ id: "i4", number: "INV-2026-0004", status: "draft" }),
  ];

  it("lists number, subject, account, status badge, total, balance, invoice date and due date", async () => {
    setPermissions(READ);
    installApi(client, { "GET /invoices": () => page(ROWS), "GET /organization/members": () => MEMBERS, "GET /accounts": () => page([]) });
    renderList();

    const table = await screen.findByRole("table");
    expect(await within(table).findByRole("link", { name: "INV-2026-0001" })).toHaveAttribute("href", "/app/invoices/i1");
    for (const header of ["Numara", "Konu", "Müşteri", "Durum", "Genel toplam", "Bakiye", "Fatura tarihi", "Son tarih"]) {
      expect(within(table).getByText(header)).toBeInTheDocument();
    }
    // Derived statuses show as badges with their own colours.
    const badge = (label: string) => within(table).getByText(label).closest("[data-status]");
    expect(badge("Vadesi geçti")).toHaveAttribute("data-status", "overdue");
    expect(badge("Kısmen ödendi")).toHaveAttribute("data-status", "partiallyPaid");
    expect(badge("Ödendi")).toHaveAttribute("data-status", "paid");
    expect(badge("Taslak")).toHaveAttribute("data-status", "draft");
    expect(within(table).getByText(/140,00/)).toBeInTheDocument();
  });

  it("syncs the status filter with the URL and the request (overdue shortcut)", async () => {
    setPermissions(READ);
    installApi(client, { "GET /invoices": () => page(ROWS), "GET /organization/members": () => MEMBERS, "GET /accounts": () => page([]) });
    renderList();
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("checkbox", { name: "Vadesi geçenler" }));

    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/invoices").at(-1)?.[1];
      expect(last.params).toEqual({ page: 1, pageSize: 25, status: "overdue" });
    });
    expect(screen.getByTestId("location")).toHaveTextContent("/app/invoices?status=overdue");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Kısmen ödendi" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("status=partiallyPaid"));
  });

  it("reads the filter from the URL on load", async () => {
    setPermissions(READ);
    installApi(client, { "GET /invoices": () => page([]), "GET /organization/members": () => MEMBERS, "GET /accounts": () => page([]) });
    renderList("/app/invoices?status=paid&sort=-balanceAmount");
    await screen.findByRole("table");
    const params = client.get.mock.calls.find(([u]) => u === "/invoices")?.[1].params;
    expect(params).toMatchObject({ status: "paid", sort: "-balanceAmount" });
  });

  it("hides create / edit / delete without crm.invoices.write, and edits or deletes only drafts with it", async () => {
    setPermissions(READ);
    installApi(client, { "GET /invoices": () => page(ROWS), "GET /organization/members": () => MEMBERS, "GET /accounts": () => page([]) });
    const first = renderList();
    await screen.findByRole("link", { name: "INV-2026-0004" });
    expect(screen.queryByRole("button", { name: "Yeni fatura" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    first.unmount();

    setPermissions([...READ, "crm.invoices.write"]);
    renderList();
    await screen.findByRole("link", { name: "INV-2026-0004" });
    expect(screen.getByRole("button", { name: "Yeni fatura" })).toBeInTheDocument();
    // Only the draft row has the row actions.
    expect(screen.getAllByRole("button", { name: "Düzenle" })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Sil" })).toHaveLength(1);
  });

  it("deletes a draft after a confirmation", async () => {
    setPermissions([...READ, "crm.invoices.write"]);
    installApi(client, {
      "GET /invoices": () => page([ROWS[3]]),
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
      "DELETE /invoices/i4": () => undefined,
    });
    renderList();
    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("INV-2026-0004");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/invoices/i4"));
    expect(toast).toHaveBeenCalled();
  });
});

function renderDetail() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/invoices/:id" element={<InvoiceDetailPage />} />
        <Route path="/app/invoices" element={<div>Fatura listesi</div>} />
        <Route path="/app/invoices/:id/edit" element={<div>Fatura düzenleme</div>} />
        <Route path="/app/orders/:id" element={<div>Sipariş sayfası</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/invoices/i1" }
  );
}

function detailRoutes(doc: object, extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return { "GET /invoices/i1": () => doc, "GET /audit": () => ({ items: [], total: 0 }), ...extra };
}

const ACTIONS = ["Ödeme kaydet", "Gönder", "Düzenle", "Taslağa al", "İptal et", "Sil"] as const;

async function visibleActions() {
  await screen.findByRole("heading", { name: /INV-2026-0001/ });
  return ACTIONS.filter((name) => screen.queryByRole("button", { name }));
}

describe("InvoiceDetailPage - actions by status and permission", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  // draft: edit / send / cancel / delete; sent | partiallyPaid | overdue: pay (+ back to draft and cancel only without payments); paid, cancelled: view only.
  const MATRIX: [status: string, paid: number, write: boolean, expected: string[]][] = [
    ["draft", 0, true, ["Gönder", "Düzenle", "İptal et", "Sil"]],
    ["draft", 0, false, []],
    ["sent", 0, true, ["Ödeme kaydet", "Taslağa al", "İptal et"]],
    ["sent", 0, false, []],
    ["overdue", 0, true, ["Ödeme kaydet", "Taslağa al", "İptal et"]],
    ["partiallyPaid", 100, true, ["Ödeme kaydet"]],
    ["overdue", 100, true, ["Ödeme kaydet"]],
    ["paid", 240, true, []],
    ["cancelled", 0, true, []],
    ["cancelled", 0, false, []],
  ];

  it.each(MATRIX)("%s (paid %s), invoices.write=%s", async (status, paid, write, expected) => {
    setPermissions([...READ, ...(write ? ["crm.invoices.write"] : [])]);
    installApi(client, detailRoutes(invoice({ status, paidAmount: paid, balanceAmount: 240 - paid })));
    renderDetail();
    expect(await visibleActions()).toEqual(expected);
  });

  it("shows the balance, the order link, the payment ledger, the lines and the totals with the rounding line", async () => {
    setPermissions(READ);
    installApi(
      client,
      detailRoutes(
        invoice({
          status: "partiallyPaid",
          orderId: "o1",
          orderNumber: "SO-2026-0001",
          paidAmount: 50,
          balanceAmount: 190,
          adjustment: -0.56,
          grandTotal: 239.44,
          payments: [payment()],
          billingAddress: { street: "Atatürk Cd. 12", city: "İstanbul" },
          carrier: "Yurtiçi Kargo",
        })
      )
    );
    renderDetail();
    await screen.findByRole("heading", { name: /INV-2026-0001/ });

    expect(screen.getByRole("link", { name: "SO-2026-0001" })).toHaveAttribute("href", "/app/orders/o1");
    const ledger = screen.getByTestId("payments-card");
    expect(within(ledger).getAllByTestId("payment-row")).toHaveLength(1);
    expect(within(ledger).getByText("Havale / EFT")).toBeInTheDocument();
    expect(within(ledger).getByText(/Bakiye/)).toHaveTextContent("190,00");
    expect(screen.getByText("CRM Pro lisansı")).toBeInTheDocument();
    expect(screen.getByTestId("total-adjustment")).toHaveTextContent(/-.*0,56/);
    expect(screen.getByTestId("total-grand")).toHaveTextContent("239,44");
    expect(screen.getByTestId("view-billing-address")).toHaveTextContent("Atatürk Cd. 12");
    expect(screen.getByText(/Yurtiçi Kargo/)).toBeInTheDocument();
    expect(screen.getByText("Kısmen ödendi")).toBeInTheDocument();
  });

  it("sends a draft, refetches, and toasts a server refusal (invoice.no_lines)", async () => {
    setPermissions([...READ, "crm.invoices.write"]);
    const error = problem(422, { code: "invoice.no_lines" });
    installApi(client, detailRoutes(invoice(), { "POST /invoices/i1/send": () => error }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Gönder" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  it("goes back to draft and cancels with an optional reason through their endpoints", async () => {
    setPermissions([...READ, "crm.invoices.write"]);
    installApi(
      client,
      detailRoutes(invoice({ status: "sent" }), {
        "POST /invoices/i1/revert": () => undefined,
        "POST /invoices/i1/cancel": () => undefined,
      })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Taslağa al" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/invoices/i1/revert", {}));

    await userEvent.click(screen.getByRole("button", { name: "İptal et" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByRole("textbox"), "Yanlış müşteri");
    await userEvent.click(within(dialog).getByRole("button", { name: "İptal et" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/invoices/i1/cancel", { reason: "Yanlış müşteri" }));
  });

  it("toasts invoice.has_payments when a stale page cancels an invoice that got a payment meanwhile", async () => {
    setPermissions([...READ, "crm.invoices.write"]);
    const error = problem(409, { code: "invoice.has_payments" });
    installApi(client, detailRoutes(invoice({ status: "sent" }), { "POST /invoices/i1/cancel": () => error }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "İptal et" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "İptal et" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  describe("payment ledger and 'Ödeme kaydet'", () => {
    const sent = () => invoice({ status: "sent", balanceAmount: 240, paidAmount: 0 });

    async function openDialog() {
      await userEvent.click(await screen.findByRole("button", { name: "Ödeme kaydet" }));
      return screen.findByRole("dialog", { name: "Ödeme kaydet" });
    }

    it("starts with the balance, records the payment with the date, method and reference, and refetches the invoice", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      installApi(client, detailRoutes(sent(), { "POST /invoices/i1/payments": () => payment() }));
      renderDetail();
      const dialog = await openDialog();

      expect(within(dialog).getByLabelText(/^Tutar/)).toHaveValue("240");
      const amount = within(dialog).getByLabelText(/^Tutar/);
      await userEvent.clear(amount);
      await userEvent.type(amount, "50.5");
      await userEvent.type(within(dialog).getByLabelText("Referans"), "EFT-1");
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      const [url, body] = client.post.mock.calls[0] as [string, Record<string, unknown>];
      expect(url).toBe("/invoices/i1/payments");
      expect(body).toMatchObject({ amount: 50.5, reference: "EFT-1", paidOn: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) });
      await waitFor(() =>
        expect(client.get.mock.calls.filter(([u]) => u === "/invoices/i1").length).toBeGreaterThan(1)
      );
      expect(toast).toHaveBeenCalled();
    });

    it("refuses an amount above the balance on the client (upper bound = balance), without a request", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      installApi(client, detailRoutes(sent()));
      renderDetail();
      const dialog = await openDialog();
      const amount = within(dialog).getByLabelText(/^Tutar/);
      await userEvent.clear(amount);
      await userEvent.type(amount, "240.01");
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      expect(await within(dialog).findByText(/Tutar kalan bakiyeyi aşamaz/)).toBeInTheDocument();
      expect(client.post).not.toHaveBeenCalled();
    });

    it("refuses zero and a future date", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      installApi(client, detailRoutes(sent()));
      renderDetail();
      const dialog = await openDialog();
      const amount = within(dialog).getByLabelText(/^Tutar/);
      await userEvent.clear(amount);
      await userEvent.type(amount, "0");
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
      expect(await within(dialog).findByText("Tutar sıfırdan büyük olmalıdır")).toBeInTheDocument();

      await userEvent.clear(amount);
      await userEvent.type(amount, "10");
      const date = within(dialog).getByLabelText(/^Ödeme tarihi/);
      await userEvent.clear(date);
      await userEvent.type(date, "2099-01-01");
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
      expect(await within(dialog).findByText("Ödeme tarihi bugünden sonra olamaz")).toBeInTheDocument();
      expect(client.post).not.toHaveBeenCalled();
    });

    it("refuses a date before the invoice date", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      installApi(client, detailRoutes(sent()));
      renderDetail();
      const dialog = await openDialog();
      const date = within(dialog).getByLabelText(/^Ödeme tarihi/);
      await userEvent.clear(date);
      await userEvent.type(date, "2026-08-01");
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
      expect(await within(dialog).findByText("Ödeme tarihi fatura tarihinden önce olamaz")).toBeInTheDocument();
    });

    it("shows invoice.payment_exceeds_balance on the amount field with the server's balance", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      const error = problem(422, { code: "invoice.payment_exceeds_balance", args: { balance: 100 } });
      installApi(client, detailRoutes(sent(), { "POST /invoices/i1/payments": () => error }));
      renderDetail();
      const dialog = await openDialog();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      expect(await within(dialog).findByText(/Tutar kalan bakiyeyi aşamaz \(.*100,00.*\)/)).toBeInTheDocument();
      // The dialog stays open for a correction and no toast is raised.
      expect(screen.getByRole("dialog", { name: "Ödeme kaydet" })).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
    });

    it("toasts and closes on commerce.concurrent_update (the invoice is refetched)", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      const error = problem(409, { code: "commerce.concurrent_update" });
      installApi(client, detailRoutes(sent(), { "POST /invoices/i1/payments": () => error }));
      renderDetail();
      const dialog = await openDialog();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
      await waitFor(() => expect(screen.queryByRole("dialog", { name: "Ödeme kaydet" })).not.toBeInTheDocument());
      await waitFor(() =>
        expect(client.get.mock.calls.filter(([u]) => u === "/invoices/i1").length).toBeGreaterThan(1)
      );
    });

    it("maps server field errors (paidOn) onto the field", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      const error = problem(400, { code: "validation", errors: { paidOn: ["Tarih sunucuda reddedildi"] } });
      installApi(client, detailRoutes(sent(), { "POST /invoices/i1/payments": () => error }));
      renderDetail();
      const dialog = await openDialog();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
      expect(await within(dialog).findByText("Tarih sunucuda reddedildi")).toBeInTheDocument();
    });

    it("deletes a payment after a confirmation and only for writers", async () => {
      setPermissions(READ);
      installApi(client, detailRoutes(invoice({ status: "partiallyPaid", paidAmount: 50, balanceAmount: 190, payments: [payment()] })));
      const first = renderDetail();
      await screen.findByTestId("payments-card");
      expect(screen.queryByRole("button", { name: /tutarlı tahsilatı sil/ })).not.toBeInTheDocument();
      first.unmount();

      setPermissions([...READ, "crm.invoices.write"]);
      installApi(
        client,
        detailRoutes(invoice({ status: "partiallyPaid", paidAmount: 50, balanceAmount: 190, payments: [payment()] }), {
          "DELETE /invoices/i1/payments/pay1": () => undefined,
        })
      );
      renderDetail();
      await userEvent.click(await screen.findByRole("button", { name: /tutarlı tahsilatı sil/ }));
      const dialog = await screen.findByRole("dialog", { name: "Tahsilatı sil" });
      expect(client.delete).not.toHaveBeenCalled();
      await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
      await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/invoices/i1/payments/pay1"));
    });

    it("offers no payment deletion on a cancelled invoice", async () => {
      setPermissions([...READ, "crm.invoices.write"]);
      installApi(client, detailRoutes(invoice({ status: "cancelled", payments: [payment()] })));
      renderDetail();
      await screen.findByTestId("payments-card");
      expect(screen.queryByRole("button", { name: /tutarlı tahsilatı sil/ })).not.toBeInTheDocument();
    });
  });
});
