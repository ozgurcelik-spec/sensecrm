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
import QuoteEditorPage from "./quote-editor";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const PERMS = [
  "crm.quotes.read",
  "crm.quotes.write",
  "crm.accounts.read",
  "crm.contacts.read",
  "crm.deals.read",
  "crm.products.read",
];

const ACCOUNT = {
  id: "a1",
  name: "Acme Ltd",
  ownerUserId: "user-1",
  createdAt: "2026-05-01T10:00:00Z",
};
const DEAL = {
  id: "d1",
  name: "Acme yıllık lisans",
  accountId: "a1",
  accountName: "Acme Ltd",
  contactId: "c1",
  contactName: "Ayşe Yılmaz",
  currency: "USD",
  pipelineId: "p",
  pipelineName: "Ana huni",
  stageId: "s",
  stageName: "Teklif",
  stageKind: "open",
  probability: 50,
  ownerUserId: "user-1",
  createdAt: "2026-05-01T10:00:00Z",
};
const CONTACT = { id: "c1", lastName: "Yılmaz", fullName: "Ayşe Yılmaz", accountId: "a1" };

function quote(overrides: Record<string, unknown> = {}) {
  return {
    id: "q1",
    number: "Q-2026-0001",
    subject: "Yıllık lisans",
    status: "draft",
    accountId: "a1",
    accountName: "Acme Ltd",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    currency: "TRY",
    grandTotal: 240,
    subtotal: 200,
    discountTotal: 0,
    taxTotal: 40,
    validUntil: "2026-10-19",
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
    ...overrides,
  };
}

function baseRoutes(extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return {
    "GET /organization/members": () => MEMBERS,
    "GET /accounts": () => page([ACCOUNT]),
    "GET /accounts/a1": () => ACCOUNT,
    "GET /contacts": () => page([CONTACT]),
    "GET /deals": () => page([DEAL]),
    "GET /deals/d1": () => DEAL,
    "GET /products": () => page([]),
    ...extra,
  };
}

function renderEditor(route: string) {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/quotes/new" element={<QuoteEditorPage />} />
        <Route path="/app/quotes/:id/edit" element={<QuoteEditorPage />} />
        <Route path="/app/quotes/:id" element={<div>Teklif detay sayfası</div>} />
        <Route path="/app/quotes" element={<div>Teklif listesi</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const lastCall = (method: "post" | "put") => client[method].mock.calls.at(-1) as [string, Record<string, unknown>];

describe("QuoteEditorPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(PERMS);
  });
  afterEach(clearSession);

  it("prefills account, contact, deal, currency and subject from a deal ('Teklif oluştur')", async () => {
    installApi(client, baseRoutes());
    renderEditor("/app/quotes/new?accountId=a1&contactId=c1&dealId=d1");

    const subject = await screen.findByLabelText(/^Konu/);
    expect(subject).toHaveValue("Acme yıllık lisans");
    expect(screen.getByRole("textbox", { name: "Müşteri" })).toHaveValue("Acme Ltd");
    expect(await screen.findByRole("textbox", { name: "Kişi" })).toHaveValue("Ayşe Yılmaz");
    expect(screen.getByRole("textbox", { name: "Fırsat" })).toHaveValue("Acme yıllık lisans");
    expect(screen.getByRole("combobox", { name: "Para birimi" })).toHaveValue("USD");
    // Only ids travel in the URL; the deal lookup fills the rest.
    expect(client.get).toHaveBeenCalledWith("/deals/d1");
  });

  it("prefills the account from an account page (?accountId only)", async () => {
    installApi(client, baseRoutes());
    renderEditor("/app/quotes/new?accountId=a1");

    expect(await screen.findByRole("textbox", { name: "Müşteri" })).toHaveValue("Acme Ltd");
    expect(screen.getByLabelText(/^Konu/)).toHaveValue("");
    expect(client.get).not.toHaveBeenCalledWith("/deals/d1");
  });

  it("saves exactly the contract body (no computed fields) and opens the saved quote", async () => {
    installApi(
      client,
      baseRoutes({ "POST /quotes": () => quote({ id: "q-new", number: "Q-2026-0002" }) })
    );
    renderEditor("/app/quotes/new?accountId=a1");
    await screen.findByRole("textbox", { name: "Müşteri" });
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Sahip" })).not.toBeDisabled());

    await userEvent.type(screen.getByLabelText(/^Konu/), "  Yeni teklif ");
    await userEvent.type(screen.getByLabelText("Açıklama 1"), "Danışmanlık");
    const price = screen.getByLabelText("Birim fiyat 1");
    await userEvent.clear(price);
    await userEvent.type(price, "100");
    const tax = screen.getByLabelText("KDV % 1");
    await userEvent.clear(tax);
    await userEvent.type(tax, "20");
    await userEvent.type(screen.getByLabelText("Notlar"), "Not");

    expect(screen.getByTestId("total-grand")).toHaveTextContent("120,00");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const [url, body] = lastCall("post");
    expect(url).toBe("/quotes");
    expect(body).toEqual({
      subject: "Yeni teklif",
      accountId: "a1",
      validUntil: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/),
      ownerUserId: "user-1",
      currency: "TRY",
      notes: "Not",
      // Always sent (0 when unused): PUT replaces the document.
      adjustment: 0,
      lines: [
        {
          description: "Danışmanlık",
          quantity: 1,
          unitPrice: 100,
          discountPercent: 0,
          taxRate: 20,
        },
      ],
    });
    // Never a computed total.
    const serialized = JSON.stringify(body);
    for (const computed of ["lineTotal", "lineSubtotal", "grandTotal", "subtotal", "taxTotal", "discountTotal"]) {
      expect(serialized).not.toContain(computed);
    }
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/app/quotes/q-new")
    );
  });

  it("defaults 'valid until' to 30 days from today", async () => {
    installApi(client, baseRoutes());
    renderEditor("/app/quotes/new?accountId=a1");
    const input = (await screen.findByLabelText("Geçerlilik tarihi")) as HTMLInputElement;
    const days = (Date.parse(input.value) - Date.now()) / 86_400_000;
    expect(days).toBeGreaterThan(28);
    expect(days).toBeLessThan(31);
  });

  it("maps server errors: lines[i].field onto the cell, header fields onto their inputs, the rest into an alert", async () => {
    installApi(
      client,
      baseRoutes({
        "POST /quotes": () =>
          problem(400, {
            code: "validation",
            errors: {
              subject: ["Konu sunucuda reddedildi"],
              "lines[1].quantity": ["Adet sunucuda reddedildi"],
              lines: ["En çok 100 kalem"],
            },
          }),
      })
    );
    renderEditor("/app/quotes/new?accountId=a1");
    await screen.findByRole("textbox", { name: "Müşteri" });

    await userEvent.type(screen.getByLabelText(/^Konu/), "Konu");
    await userEvent.type(screen.getByLabelText("Açıklama 1"), "A");
    await userEvent.click(screen.getByRole("button", { name: "Satır ekle" }));
    await userEvent.type(screen.getByLabelText("Açıklama 2"), "B");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(await screen.findByText("Konu sunucuda reddedildi")).toBeInTheDocument();
    const rows = screen.getAllByTestId("line-row");
    expect(within(rows[1] as HTMLElement).getByText("Adet sunucuda reddedildi")).toBeInTheDocument();
    expect(within(rows[0] as HTMLElement).queryByText("Adet sunucuda reddedildi")).not.toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("En çok 100 kalem");
    expect(toastApiError).not.toHaveBeenCalled();
    // Still on the editor.
    expect(screen.getByTestId("location")).toHaveTextContent("/app/quotes/new");
  });

  it("reports coded errors (commerce.related_not_found) through the toast", async () => {
    const error = problem(404, { code: "commerce.related_not_found" });
    installApi(client, baseRoutes({ "POST /quotes": () => error }));
    renderEditor("/app/quotes/new?accountId=a1");
    await screen.findByRole("textbox", { name: "Müşteri" });
    await userEvent.type(screen.getByLabelText(/^Konu/), "Konu");
    await userEvent.type(screen.getByLabelText("Açıklama 1"), "A");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  it("does not send an invalid form: required subject and line checks show first", async () => {
    installApi(client, baseRoutes());
    renderEditor("/app/quotes/new?accountId=a1");
    await screen.findByRole("textbox", { name: "Müşteri" });

    const quantity = screen.getByLabelText("Adet 1");
    await userEvent.clear(quantity);
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(await screen.findAllByText("Bu alan zorunludur")).not.toHaveLength(0);
    expect(screen.getByText("Adet sıfırdan büyük olmalıdır")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("edits a draft: loads the lines and sends a full replacement with PUT", async () => {
    installApi(
      client,
      baseRoutes({ "GET /quotes/q1": () => quote(), "PUT /quotes/q1": () => undefined })
    );
    renderEditor("/app/quotes/q1/edit");

    expect(await screen.findByLabelText(/^Konu/)).toHaveValue("Yıllık lisans");
    expect(screen.getByLabelText("Açıklama 1")).toHaveValue("CRM Pro lisansı");
    expect(screen.getByTestId("total-grand")).toHaveTextContent("240,00");

    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    const [url, body] = lastCall("put");
    expect(url).toBe("/quotes/q1");
    expect(body).toMatchObject({
      subject: "Yıllık lisans",
      accountId: "a1",
      validUntil: "2026-10-19",
      ownerUserId: "user-1",
      currency: "TRY",
      lines: [{ description: "CRM Pro lisansı", quantity: 2, unitPrice: 100, discountPercent: 0, taxRate: 20 }],
    });
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/quotes/q1"));
  });

  it.each(["sent", "expired", "accepted", "rejected"])(
    "redirects the edit route of a %s quote to its detail page",
    async (status) => {
      installApi(client, baseRoutes({ "GET /quotes/q1": () => quote({ status }) }));
      renderEditor("/app/quotes/q1/edit");
      expect(await screen.findByText("Teklif detay sayfası")).toBeInTheDocument();
      expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/quotes\/q1$/);
    }
  );

  it("without crm.accounts.read the prefilled account is shown read-only and can still be saved", async () => {
    setPermissions(["crm.quotes.write", "crm.quotes.read"]);
    installApi(
      client,
      baseRoutes({ "POST /quotes": () => quote({ id: "q9" }), "GET /deals/d1": () => DEAL })
    );
    renderEditor("/app/quotes/new?accountId=a1");

    // A read-only lookup field (no search window) instead of a searchable account picker.
    const account = await screen.findByRole("textbox", { name: "Müşteri" });
    expect(account).toHaveAttribute("readonly");
    expect(screen.queryByRole("button", { name: "Müşteri seç" })).not.toBeInTheDocument();
    expect(account).toHaveValue("a1");
    // No product picker, no contact / deal selects without their read permissions.
    expect(screen.queryByRole("combobox", { name: "Ürün 1" })).not.toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: "Kişi" })).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalledWith("/accounts/a1");

    await userEvent.type(screen.getByLabelText(/^Konu/), "Konu");
    await userEvent.type(screen.getByLabelText("Açıklama 1"), "Kalem");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(lastCall("post")[1]).toMatchObject({ accountId: "a1" });
  });
});
