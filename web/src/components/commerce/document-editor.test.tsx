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
import { ACCOUNT, CONTACT, invoice, line, priceBook, product, purchaseOrder, vendor } from "@/test/inventory";
import { toastApiError } from "@/hooks/use-toast";
import { DocumentEditor, type DocumentKind } from "./document-editor";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

// The full editor re-renders on every keystroke: give the typing-heavy cases room when the machine is loaded.
vi.setConfig({ testTimeout: 45_000 });

const ALL_PERMS = [
  "crm.quotes.read",
  "crm.quotes.write",
  "crm.orders.read",
  "crm.orders.write",
  "crm.invoices.read",
  "crm.invoices.write",
  "crm.purchaseorders.read",
  "crm.purchaseorders.write",
  "crm.accounts.read",
  "crm.contacts.read",
  "crm.deals.read",
  "crm.products.read",
  "crm.vendors.read",
  "crm.pricebooks.read",
];

function baseRoutes(extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return {
    "GET /organization/members": () => MEMBERS,
    "GET /accounts": () => page([ACCOUNT]),
    "GET /accounts/a1": () => ({
      ...ACCOUNT,
      billingAddress: { street: "Cumhuriyet Cd. 5", city: "Bursa", country: "Türkiye" },
    }),
    "GET /contacts": () => page([CONTACT]),
    "GET /deals": () => page([]),
    "GET /products": () => page([product({ purchasePrice: 60 })]),
    "GET /vendors": () => page([vendor()]),
    "GET /pricebooks": () => page([priceBook()]),
    ...extra,
  };
}

const LISTS: Record<DocumentKind, string> = {
  quote: "/app/quotes",
  order: "/app/orders",
  invoice: "/app/invoices",
  purchaseOrder: "/app/purchase-orders",
};

function renderEditor(
  kind: DocumentKind,
  props: { existing?: Parameters<typeof DocumentEditor>[0]["existing"]; withAccount?: boolean } = {}
) {
  return renderWithProviders(
    <>
      <Routes>
        <Route
          path="/editor"
          element={
            <DocumentEditor
              kind={kind}
              existing={props.existing}
              prefill={props.withAccount === false ? undefined : { accountId: "a1", accountName: "Acme Ltd" }}
            />
          }
        />
        <Route path={`${LISTS[kind]}/:id`} element={<div>Detay sayfası</div>} />
        <Route path={LISTS[kind]} element={<div>Liste sayfası</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/editor" }
  );
}

const lastBody = (method: "post" | "put") =>
  (client[method].mock.calls.at(-1) as [string, Record<string, unknown>])[1];

async function ready() {
  await waitFor(() => expect(screen.getByRole("combobox", { name: "Sahip" })).not.toBeDisabled());
}

async function fillBasics(subject = "Konu", description = "Kalem") {
  await userEvent.type(screen.getByLabelText(/^Konu/), subject);
  await userEvent.type(screen.getByLabelText("Açıklama 1"), description);
}

describe("DocumentEditor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(ALL_PERMS);
  });
  afterEach(clearSession);

  describe("field layout per kind (DOCUMENT_FIELD_LAYOUT)", () => {
    const expectations: [DocumentKind, string[], string[]][] = [
      [
        "quote",
        ["Geçerlilik tarihi", "Fiyat listesi", "Nakliye", "Müşteri"],
        ["Sipariş tarihi", "Müşteri satın alma emri no", "Gider vergisi", "Bekliyor", "Fatura tarihi", "SAE tarihi"],
      ],
      [
        "order",
        ["Sipariş tarihi", "Son tarih", "Müşteri satın alma emri no", "Gider vergisi", "Satış komisyonu", "Bekliyor", "Fiyat listesi", "Nakliye"],
        ["Geçerlilik tarihi", "Fatura tarihi", "SAE tarihi", "Tedarikçi"],
      ],
      [
        "invoice",
        ["Fatura tarihi", "Son tarih", "Müşteri satın alma emri no", "Gider vergisi", "Satış komisyonu", "Fiyat listesi", "Nakliye"],
        ["Bekliyor", "Geçerlilik tarihi", "Sipariş tarihi", "Tedarikçi"],
      ],
      [
        "purchaseOrder",
        ["Tedarikçi", "SAE tarihi", "Son tarih", "Gider vergisi", "Satış komisyonu", "Nakliye"],
        ["Fiyat listesi", "Müşteri satın alma emri no", "Bekliyor", "Fatura tarihi", "Fırsat"],
      ],
    ];

    it.each(expectations)("%s", async (kind, shown, hidden) => {
      installApi(client, baseRoutes());
      renderEditor(kind);
      await ready();

      for (const label of shown) {
        expect(screen.getAllByLabelText(new RegExp(`^${label}`)).length, label).toBeGreaterThan(0);
      }
      for (const label of hidden) {
        expect(screen.queryByLabelText(new RegExp(`^${label}`)), label).not.toBeInTheDocument();
      }
      // Every kind has the two address blocks and the rounding line.
      expect(screen.getByTestId("address-billing")).toBeInTheDocument();
      expect(screen.getByTestId("address-shipping")).toBeInTheDocument();
      expect(screen.getByLabelText("Yuvarlama")).toBeInTheDocument();
    });

    it("the price book field needs crm.pricebooks.read", async () => {
      setPermissions(ALL_PERMS.filter((p) => p !== "crm.pricebooks.read"));
      installApi(client, baseRoutes());
      renderEditor("order");
      await ready();
      expect(screen.queryByLabelText(/^Fiyat listesi/)).not.toBeInTheDocument();
    });
  });

  describe("contract body", () => {
    it("sends addresses (an empty block is left out), carrier, the signed adjustment and an explicit unit price - never a computed field", async () => {
      installApi(client, baseRoutes({ "POST /orders": () => ({ id: "o-new" }) }));
      renderEditor("order");
      await ready();

      await fillBasics("Doğrudan sipariş");
      const price = screen.getByLabelText("Birim fiyat 1");
      await userEvent.clear(price);
      await userEvent.type(price, "100");
      await userEvent.type(screen.getByLabelText("Faturalama Adresi - Şehir"), "İstanbul");
      await userEvent.type(screen.getByLabelText("Faturalama Adresi - Daire / Ev No / Bina / Apartman adı"), "Kat 3");
      await userEvent.type(screen.getByRole("combobox", { name: "Nakliye" }), "Yurtiçi Kargo");
      await userEvent.type(screen.getByLabelText("Müşteri satın alma emri no"), "PO-77812");
      await userEvent.type(screen.getByLabelText("Bekliyor"), "Ödeme bekleniyor");
      await userEvent.type(screen.getByLabelText("Gider vergisi"), "12.5");
      await userEvent.type(screen.getByLabelText("Yuvarlama"), "-0.56");
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      const body = lastBody("post");
      expect(body).toMatchObject({
        subject: "Doğrudan sipariş",
        accountId: "a1",
        billingAddress: { city: "İstanbul", building: "Kat 3" },
        carrier: "Yurtiçi Kargo",
        customerPoNumber: "PO-77812",
        pending: "Ödeme bekleniyor",
        exciseTax: 12.5,
        adjustment: -0.56,
        lines: [{ description: "Kalem", quantity: 1, unitPrice: 100, discountPercent: 0, taxRate: 0 }],
      });
      // undefined values never reach the wire (JSON drops them): an empty block is no block.
      const serialized = JSON.stringify(body);
      expect(serialized).not.toContain("shippingAddress");
      expect(serialized).not.toContain("salesCommission");
      for (const computed of ["lineTotal", "grandTotal", "subtotal", "taxTotal", "discountTotal", "paidAmount"]) {
        expect(serialized).not.toContain(computed);
      }
      await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/orders/o-new"));
    });

    it("always sends adjustment (0 when unused) so a PUT never resets it by omission", async () => {
      installApi(client, baseRoutes({ "PUT /quotes/q1": () => undefined }));
      renderEditor("quote", {
        existing: {
          ...invoice({ id: "q1", number: "Q-1", status: "draft", validUntil: "2026-10-19" }),
        } as never,
      });
      await ready();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
      expect(lastBody("put")).toHaveProperty("adjustment", 0);
    });

    it("keeps the existing price book when the user may not read price books (it is sent unchanged)", async () => {
      setPermissions(ALL_PERMS.filter((p) => p !== "crm.pricebooks.read"));
      installApi(client, baseRoutes({ "PUT /invoices/i1": () => undefined }));
      renderEditor("invoice", { existing: invoice({ priceBookId: "pb1", priceBookName: "Kurumsal liste" }) as never });
      await ready();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
      expect(lastBody("put")).toMatchObject({ priceBookId: "pb1" });
    });
  });

  describe("rounding line", () => {
    /** The plan's reference document {1, 3}: subtotal 85.22, discount 6.00, tax 15.34, total 94.56 (loaded, not typed: fast). */
    function vectorInvoice() {
      return invoice({
        lines: [
          line({ id: "l1", position: 0, description: "V1", quantity: 3, unitPrice: 19.99, discountPercent: 10, taxRate: 20 }),
          line({ id: "l2", position: 1, description: "V3", quantity: 2.5, unitPrice: 10.1, discountPercent: 0, taxRate: 18 }),
        ],
      }) as never;
    }

    it("previews the grand total with the adjustment (A1: -0.56 -> 94,00) and 'Yuvarla' brings 94.56 to 95,00", async () => {
      installApi(client, baseRoutes());
      renderEditor("invoice", { existing: vectorInvoice() });
      await ready();
      expect(screen.getByTestId("total-grand")).toHaveTextContent("94,56");

      await userEvent.type(screen.getByLabelText("Yuvarlama"), "-0.56");
      expect(screen.getByTestId("total-grand")).toHaveTextContent("94,00");

      await userEvent.clear(screen.getByLabelText("Yuvarlama"));
      await userEvent.click(screen.getByRole("button", { name: "Yuvarla" }));
      expect(screen.getByLabelText("Yuvarlama")).toHaveValue("0.44");
      expect(screen.getByTestId("total-grand")).toHaveTextContent("95,00");
    });

    it("refuses a rounding that makes the total negative (A4) on the client, without a request", async () => {
      installApi(client, baseRoutes({ "PUT /invoices/i1": () => undefined }));
      renderEditor("invoice", { existing: vectorInvoice() });
      await ready();

      await userEvent.type(screen.getByLabelText("Yuvarlama"), "-94.57");
      expect(await screen.findByText("Yuvarlama genel toplamı negatif yapamaz")).toBeInTheDocument();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      expect(client.put).not.toHaveBeenCalled();

      // A3: exactly the total is valid.
      await userEvent.clear(screen.getByLabelText("Yuvarlama"));
      await userEvent.type(screen.getByLabelText("Yuvarlama"), "-94.56");
      expect(screen.queryByText("Yuvarlama genel toplamı negatif yapamaz")).not.toBeInTheDocument();
      expect(screen.getByTestId("total-grand")).toHaveTextContent("0,00");
    });

    it("refuses any adjustment on a document without lines (A7)", async () => {
      installApi(client, baseRoutes());
      renderEditor("invoice");
      await ready();
      await userEvent.click(screen.getByRole("button", { name: "Satırı sil 1" }));
      await userEvent.type(screen.getByLabelText("Yuvarlama"), "0.01");
      expect(await screen.findByText("Kalemsiz belgede yuvarlama olamaz")).toBeInTheDocument();
    });
  });

  describe("server errors", () => {
    it("maps billingAddress.city, adjustment and priceBookId onto their fields", async () => {
      const error = problem(400, {
        code: "validation",
        errors: {
          "billingAddress.city": ["Şehir çok uzun"],
          adjustment: ["Yuvarlama sunucuda reddedildi"],
          priceBookId: ["Fiyat listesi geçerli değil"],
          "lines[0].unitPrice": ["Fiyat çözülemedi"],
        },
      });
      installApi(client, baseRoutes({ "POST /orders": () => error }));
      renderEditor("order");
      await ready();
      await fillBasics();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

      expect(await screen.findByText("Şehir çok uzun")).toBeInTheDocument();
      expect(screen.getByText("Yuvarlama sunucuda reddedildi")).toBeInTheDocument();
      expect(screen.getByText("Fiyat listesi geçerli değil")).toBeInTheDocument();
      expect(within(screen.getAllByTestId("line-row")[0] as HTMLElement).getByText("Fiyat çözülemedi")).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
      expect(screen.getByTestId("location")).toHaveTextContent("/editor");
    });

    it("toasts coded errors (commerce.concurrent_update)", async () => {
      const error = problem(409, { code: "commerce.concurrent_update" });
      installApi(client, baseRoutes({ "POST /invoices": () => error }));
      renderEditor("invoice");
      await ready();
      await fillBasics();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
    });
  });

  describe("dates", () => {
    it("does not send a due date before the invoice date", async () => {
      installApi(client, baseRoutes({ "POST /invoices": () => ({ id: "i-new" }) }));
      renderEditor("invoice");
      await ready();
      await fillBasics();
      const invoiceDate = screen.getByLabelText("Fatura tarihi");
      await userEvent.clear(invoiceDate);
      await userEvent.type(invoiceDate, "2026-09-10");
      await userEvent.type(screen.getByLabelText("Son tarih"), "2026-09-01");
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

      expect(await screen.findByText("Son tarih, belge tarihinden önce olamaz")).toBeInTheDocument();
      expect(client.post).not.toHaveBeenCalled();
    });

    it("an invoice starts with today's date", async () => {
      installApi(client, baseRoutes());
      renderEditor("invoice");
      const input = (await screen.findByLabelText("Fatura tarihi")) as HTMLInputElement;
      expect(input.value).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });
  });

  describe("price book", () => {
    async function pickProduct(index = 1, name = /CRM Pro/) {
      await userEvent.click(screen.getByRole("combobox", { name: `Ürün ${index}` }));
      await userEvent.click(await screen.findByRole("option", { name }));
    }

    it("resolves the list price on the server when a product is picked, shows the source and lets the user override", async () => {
      installApi(
        client,
        baseRoutes({
          "POST /pricebooks/pb1/resolve": () => ({ items: [{ productId: "p1", unitPrice: 90, source: "entry" }] }),
          "POST /quotes": () => ({ id: "q-new" }),
        })
      );
      renderEditor("quote");
      await ready();
      // Choose the price book through its lookup window.
      await userEvent.click(screen.getByRole("textbox", { name: "Fiyat listesi" }));
      await userEvent.click(await screen.findByText("Kurumsal liste"));
      await waitFor(() => expect(screen.getByRole("textbox", { name: "Fiyat listesi" })).toHaveValue("Kurumsal liste"));

      await pickProduct();

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/pricebooks/pb1/resolve", { productIds: ["p1"] })
      );
      await waitFor(() => expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("90"));
      expect(screen.getByTestId("price-source")).toHaveTextContent("Liste");

      // A hand-typed price is an override: the badge goes and the body carries the typed price.
      const price = screen.getByLabelText("Birim fiyat 1");
      await userEvent.clear(price);
      await userEvent.type(price, "85");
      expect(screen.queryByTestId("price-source")).not.toBeInTheDocument();

      await userEvent.type(screen.getByLabelText(/^Konu/), "Teklif");
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(client.post).toHaveBeenCalledWith("/quotes", expect.anything()));
      const body = lastBody("post");
      expect(body).toMatchObject({ priceBookId: "pb1", lines: [{ productId: "p1", unitPrice: 85 }] });
    });

    it("uses the catalog price without a price book and never calls resolve", async () => {
      installApi(client, baseRoutes());
      renderEditor("quote");
      await ready();
      await pickProduct();

      await waitFor(() => expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("100"));
      expect(screen.getByTestId("price-source")).toHaveTextContent("Katalog");
      expect(client.post.mock.calls.some(([url]) => String(url).includes("/resolve"))).toBe(false);
    });

    it("re-fetches all product prices from the list after a confirmation ('Fiyatları listeye göre yeniden getir')", async () => {
      let resolved = 90;
      installApi(
        client,
        baseRoutes({
          "POST /pricebooks/pb1/resolve": () => ({ items: [{ productId: "p1", unitPrice: resolved, source: "flat" }] }),
        })
      );
      renderEditor("quote");
      await ready();
      await userEvent.click(screen.getByRole("textbox", { name: "Fiyat listesi" }));
      await userEvent.click(await screen.findByText("Kurumsal liste"));
      await pickProduct();
      await waitFor(() => expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("90"));

      resolved = 80;
      await userEvent.click(screen.getByRole("button", { name: "Fiyatları listeye göre yeniden getir" }));
      // Nothing happens before the confirmation.
      expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("90");
      const dialog = await screen.findByRole("dialog", { name: "Fiyatları yeniden getir" });
      await userEvent.click(within(dialog).getByRole("button", { name: "Fiyatları listeye göre yeniden getir" }));

      await waitFor(() => expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("80"));
    });

    it("suggests the account's effective default price book once and lets the user clear it", async () => {
      installApi(
        client,
        baseRoutes({
          "GET /pricebooks/accounts/a1/default": () => ({ priceBookId: "pb1", priceBookName: "Kurumsal liste", isEffective: true }),
        })
      );
      renderEditor("order");
      const field = await screen.findByRole("textbox", { name: "Fiyat listesi" });
      await waitFor(() => expect(field).toHaveValue("Kurumsal liste"));
      expect(screen.getByText(/varsayılan fiyat listesi önerildi/)).toBeInTheDocument();

      await userEvent.click(screen.getByRole("button", { name: "Fiyat listesi alanını temizle" }));
      expect(field).toHaveValue("");
    });

    it("suggests nothing when there is no default (204) or it is not effective", async () => {
      installApi(
        client,
        baseRoutes({
          "GET /pricebooks/accounts/a1/default": () => undefined,
        })
      );
      const first = renderEditor("order");
      await ready();
      await waitFor(() => expect(client.get).toHaveBeenCalledWith("/pricebooks/accounts/a1/default"));
      expect(screen.getByRole("textbox", { name: "Fiyat listesi" })).toHaveValue("");
      first.unmount();

      installApi(
        client,
        baseRoutes({
          "GET /pricebooks/accounts/a1/default": () => ({ priceBookId: "pb1", priceBookName: "Eski liste", isEffective: false }),
        })
      );
      renderEditor("order");
      await ready();
      await waitFor(() => expect(client.get).toHaveBeenCalledWith("/pricebooks/accounts/a1/default"));
      expect(screen.getByRole("textbox", { name: "Fiyat listesi" })).toHaveValue("");
    });

    it("asks for no default price book without crm.pricebooks.read", async () => {
      setPermissions(ALL_PERMS.filter((p) => p !== "crm.pricebooks.read"));
      installApi(client, baseRoutes());
      renderEditor("order");
      await ready();
      expect(client.get.mock.calls.some(([url]) => String(url).includes("/pricebooks"))).toBe(false);
    });
  });

  describe("purchase order", () => {
    it("takes the unit price from the product's purchase price and sends the vendor, without an account or a price book", async () => {
      installApi(client, baseRoutes({ "POST /purchase-orders": () => ({ id: "po-new" }) }));
      renderEditor("purchaseOrder", { withAccount: false });
      await ready();

      await userEvent.click(screen.getByRole("textbox", { name: "Tedarikçi" }));
      await userEvent.click(await screen.findByText("Tedarik A.Ş."));
      await userEvent.click(screen.getByRole("combobox", { name: "Ürün 1" }));
      await userEvent.click(await screen.findByRole("option", { name: /CRM Pro/ }));

      await waitFor(() => expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("60"));
      expect(screen.getByTestId("price-source")).toHaveTextContent("Satın alma");

      await userEvent.type(screen.getByLabelText(/^Konu/), "Sunucu alımı");
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      const body = lastBody("post");
      expect(client.post.mock.calls[0]?.[0]).toBe("/purchase-orders");
      expect(body).toMatchObject({ vendorId: "v1", subject: "Sunucu alımı", adjustment: 0, lines: [{ productId: "p1", unitPrice: 60 }] });
      expect(JSON.stringify(body)).not.toContain("accountId");
      expect(JSON.stringify(body)).not.toContain("priceBookId");
      await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/purchase-orders/po-new"));
    });

    it("requires a vendor", async () => {
      installApi(client, baseRoutes());
      renderEditor("purchaseOrder", { withAccount: false });
      await ready();
      await fillBasics();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      expect(await screen.findAllByText("Bu alan zorunludur")).not.toHaveLength(0);
      expect(client.post).not.toHaveBeenCalled();
    });

    it("leaves the price for the user to type when the product has no purchase price", async () => {
      installApi(client, baseRoutes({ "GET /products": () => page([product()]) }));
      renderEditor("purchaseOrder", { withAccount: false });
      await ready();
      await userEvent.click(screen.getByRole("combobox", { name: "Ürün 1" }));
      await userEvent.click(await screen.findByRole("option", { name: /CRM Pro/ }));
      expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("0");
      expect(screen.queryByTestId("price-source")).not.toBeInTheDocument();
    });

    it("loads an existing draft with its vendor and lines and saves a full replacement", async () => {
      installApi(client, baseRoutes({ "PUT /purchase-orders/po1": () => undefined }));
      renderEditor("purchaseOrder", {
        existing: purchaseOrder({ lines: [line({ description: "Sunucu", unitPrice: 100 })] }) as never,
        withAccount: false,
      });
      expect(await screen.findByRole("textbox", { name: "Tedarikçi" })).toHaveValue("Tedarik A.Ş.");
      expect(screen.getByLabelText("Açıklama 1")).toHaveValue("Sunucu");
      await ready();
      await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
      expect(client.put.mock.calls[0]?.[0]).toBe("/purchase-orders/po1");
      expect(lastBody("put")).toMatchObject({ vendorId: "v1", subject: "Sunucu alımı" });
    });
  });
});
