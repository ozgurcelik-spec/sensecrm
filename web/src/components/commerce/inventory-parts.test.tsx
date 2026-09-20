/** Product vendor / purchase price, the account's default price book and the M9C report sections. */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  MEMBERS,
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type ApiHandler,
  type MockClient,
} from "@/test/crm";
import { priceBook, product, vendor } from "@/test/inventory";
import { toast, toastApiError } from "@/hooks/use-toast";
import ProductsPage from "@/pages/commerce/products";
import { AccountPriceBook } from "./account-price-book";
import { CommerceReport } from "./commerce-report";
import { ProductFormDialog } from "./product-form-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("ProductFormDialog - vendor and purchase price", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const routes = (extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> => ({
    "GET /organization/members": () => MEMBERS,
    "GET /vendors": () => page([vendor()]),
    ...extra,
  });

  it("picks the vendor in a lookup window and sends vendorId and purchasePrice", async () => {
    setPermissions(["crm.products.write", "crm.vendors.read"]);
    installApi(client, routes({ "POST /products": () => product({ id: "p9" }) }));
    renderWithProviders(<ProductFormDialog onClose={vi.fn()} />);
    const dialog = await screen.findByRole("dialog");

    await userEvent.type(within(dialog).getByLabelText(/^Ürün adı/), "Sunucu");
    await userEvent.click(within(dialog).getByRole("textbox", { name: "Tedarikçi" }));
    await userEvent.click(await screen.findByText("Tedarik A.Ş."));
    const purchase = within(dialog).getByLabelText(/^Satın alma fiyatı/);
    await userEvent.type(purchase, "60.5");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const [url, body] = client.post.mock.calls[0] as [string, Record<string, unknown>];
    expect(url).toBe("/products");
    expect(body).toMatchObject({ name: "Sunucu", vendorId: "v1", purchasePrice: 60.5 });
  });

  it("leaves both out when unused (no vendor, blank purchase price)", async () => {
    setPermissions(["crm.products.write", "crm.vendors.read"]);
    installApi(client, routes({ "POST /products": () => product({ id: "p9" }) }));
    renderWithProviders(<ProductFormDialog onClose={vi.fn()} />);
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/^Ürün adı/), "Basit");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const serialized = JSON.stringify((client.post.mock.calls[0] as [string, unknown])[1]);
    expect(serialized).not.toContain("vendorId");
    expect(serialized).not.toContain("purchasePrice");
  });

  it("refuses a purchase price above the limit", async () => {
    setPermissions(["crm.products.write"]);
    installApi(client, routes());
    renderWithProviders(<ProductFormDialog onClose={vi.fn()} />);
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/^Ürün adı/), "Ürün");
    await userEvent.type(within(dialog).getByLabelText(/^Satın alma fiyatı/), "2000000000");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
    expect(await within(dialog).findByText("Fiyat en fazla 1.000.000.000 olabilir")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("keeps an existing vendor and purchase price when the vendor lookup is not allowed (they are sent unchanged)", async () => {
    setPermissions(["crm.products.write"]);
    installApi(client, routes({ "PUT /products/p1": () => undefined }));
    renderWithProviders(
      <ProductFormDialog product={product({ vendorId: "v1", vendorName: "Tedarik A.Ş.", purchasePrice: 60 }) as never} onClose={vi.fn()} />
    );
    const dialog = await screen.findByRole("dialog");
    // No vendors.read: no vendor field at all.
    expect(within(dialog).queryByRole("textbox", { name: "Tedarikçi" })).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]?.[1]).toMatchObject({ vendorId: "v1", purchasePrice: 60 });
  });

  it("puts a vendor error of the server on the vendor field", async () => {
    setPermissions(["crm.products.write", "crm.vendors.read"]);
    installApi(client, routes({ "POST /products": () => problem(404, { code: "commerce.related_not_found" }) }));
    renderWithProviders(<ProductFormDialog product={undefined} onClose={vi.fn()} />);
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/^Ürün adı/), "Ürün");
    await userEvent.click(within(dialog).getByRole("textbox", { name: "Tedarikçi" }));
    await userEvent.click(await screen.findByText("Tedarik A.Ş."));
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
    expect(await within(dialog).findByText(/bulunamadı/)).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("the active switch of the product list keeps the vendor link and the purchase price (a PUT replaces the product)", async () => {
    setPermissions(["crm.products.read", "crm.products.write"]);
    installApi(client, {
      "GET /products": () => page([product({ vendorId: "v1", vendorName: "Tedarik A.Ş.", purchasePrice: 60 })]),
      "PUT /products/p1": () => undefined,
    });
    renderWithProviders(<ProductsPage />);
    expect(await screen.findByText("Tedarik A.Ş.")).toBeInTheDocument();
    await userEvent.click(await screen.findByRole("switch", { name: /CRM Pro/ }));
    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]?.[1]).toMatchObject({ isActive: false, vendorId: "v1", purchasePrice: 60 });
  });
});

describe("AccountPriceBook (account page row)", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("shows the default price book as a link", async () => {
    setPermissions(["crm.pricebooks.read"]);
    installApi(client, {
      "GET /pricebooks/accounts/a1/default": () => ({ priceBookId: "pb1", priceBookName: "Kurumsal liste", isEffective: true }),
    });
    renderWithProviders(<AccountPriceBook accountId="a1" />);
    expect(await screen.findByRole("link", { name: "Kurumsal liste" })).toHaveAttribute("href", "/app/pricebooks/pb1");
    expect(screen.queryByRole("button", { name: "Değiştir" })).not.toBeInTheDocument();
  });

  it("says none on a 204 and warns when the default is not effective", async () => {
    setPermissions(["crm.pricebooks.read"]);
    installApi(client, { "GET /pricebooks/accounts/a1/default": () => undefined });
    const first = renderWithProviders(<AccountPriceBook accountId="a1" />);
    expect(await screen.findByText("Yok")).toBeInTheDocument();
    first.unmount();

    installApi(client, {
      "GET /pricebooks/accounts/a1/default": () => ({ priceBookId: "pb1", priceBookName: "Eski", isEffective: false }),
    });
    renderWithProviders(<AccountPriceBook accountId="a1" />);
    expect(await screen.findByText("Şu an geçerli değil")).toBeInTheDocument();
  });

  it("renders nothing and asks for nothing without crm.pricebooks.read", async () => {
    setPermissions([]);
    installApi(client, {});
    renderWithProviders(<AccountPriceBook accountId="a1" />);
    expect(screen.queryByTestId("account-price-book")).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalled();
  });

  it("changes the default through a lookup window, and clears it, with crm.pricebooks.write", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    installApi(client, {
      "GET /pricebooks/accounts/a1/default": () => ({ priceBookId: "pb1", priceBookName: "Kurumsal liste", isEffective: true }),
      "GET /pricebooks": () => page([priceBook({ id: "pb2", name: "Yeni liste" })]),
      "PUT /pricebooks/accounts/a1/default": () => undefined,
      "DELETE /pricebooks/accounts/a1/default": () => undefined,
      "GET /organization/members": () => MEMBERS,
    });
    renderWithProviders(<AccountPriceBook accountId="a1" />);
    await userEvent.click(await screen.findByRole("button", { name: "Değiştir" }));
    const dialog = await screen.findByRole("dialog");
    expect(client.get.mock.calls.find(([u]) => u === "/pricebooks")?.[1].params).toMatchObject({ effective: "true" });
    await userEvent.click(await within(dialog).findByText("Yeni liste"));
    await waitFor(() =>
      expect(client.put).toHaveBeenCalledWith("/pricebooks/accounts/a1/default", { priceBookId: "pb2" })
    );

    await userEvent.click(screen.getByRole("button", { name: "Kaldır" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/pricebooks/accounts/a1/default"));
    expect(toast).toHaveBeenCalled();
  });

  it("toasts the 404 of an account that is gone (commerce.related_not_found)", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    const error = problem(404, { code: "commerce.related_not_found" });
    installApi(client, {
      "GET /pricebooks/accounts/a1/default": () => ({ priceBookId: "pb1", priceBookName: "Kurumsal liste", isEffective: true }),
      "DELETE /pricebooks/accounts/a1/default": () => error,
    });
    renderWithProviders(<AccountPriceBook accountId="a1" />);
    await userEvent.click(await screen.findByRole("button", { name: "Kaldır" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });
});

describe("CommerceReport - M9C sections", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const base = {
    currencies: ["TRY"],
    quotes: {
      totalCount: 4,
      totalAmount: 1000,
      byStatus: [
        { status: "draft", count: 1, amount: 100 },
        { status: "sent", count: 1, amount: 100 },
        { status: "negotiation", count: 1, amount: 300 },
        { status: "accepted", count: 1, amount: 500 },
        { status: "rejected", count: 0, amount: 0 },
        { status: "expired", count: 0, amount: 0 },
      ],
    },
    orders: { totalCount: 0, totalAmount: 0, byStatus: [{ status: "draft", count: 0, amount: 0 }] },
    conversionRate: 0.5,
    invoices: {
      totalCount: 5,
      totalAmount: 41000,
      paidAmount: 15000,
      outstandingAmount: 26000,
      overdueCount: 1,
      overdueAmount: 6000,
      byStatus: [
        { status: "draft", count: 1, amount: 3000 },
        { status: "sent", count: 1, amount: 8000 },
        { status: "partiallyPaid", count: 1, amount: 12000 },
        { status: "paid", count: 1, amount: 12000 },
        { status: "overdue", count: 1, amount: 6000 },
        { status: "cancelled", count: 0, amount: 0 },
      ],
    },
    purchaseOrders: {
      totalCount: 3,
      totalAmount: 22000,
      byStatus: [
        { status: "draft", count: 1, amount: 2000 },
        { status: "confirmed", count: 1, amount: 10000 },
        { status: "received", count: 1, amount: 10000 },
        { status: "cancelled", count: 0, amount: 0 },
      ],
    },
  };

  const range = { from: "2026-09-01", to: "2026-09-30" } as never;

  it("shows the negotiation row, the invoice cards (collected, outstanding, overdue) and the invoice / purchase order status tables", async () => {
    setPermissions(["crm.reports.read"]);
    installApi(client, { "GET /reports/commerce/summary": () => base });
    renderWithProviders(<CommerceReport range={range} />);

    expect(await screen.findByTestId("invoice-report")).toBeInTheDocument();
    expect(screen.getByTestId("invoice-paid")).toHaveTextContent(/15\.000,00/);
    expect(screen.getByTestId("invoice-outstanding")).toHaveTextContent(/26\.000,00/);
    expect(screen.getByTestId("invoice-overdue")).toHaveTextContent(/6\.000,00/);
    // The negotiation row of the quote table and the derived invoice statuses.
    expect(screen.getByText("Müzakere")).toBeInTheDocument();
    for (const label of ["Kısmen ödendi", "Vadesi geçti"]) {
      expect(screen.getAllByText(label).length, label).toBeGreaterThan(0);
    }
    // Purchase order statuses (received is theirs alone).
    expect(screen.getAllByText("Teslim alındı").length).toBeGreaterThan(0);
  });

  it("draws no invoice or purchase order section for an older server", async () => {
    setPermissions(["crm.reports.read"]);
    const older: Record<string, unknown> = { ...base };
    delete older.invoices;
    delete older.purchaseOrders;
    installApi(client, { "GET /reports/commerce/summary": () => older });
    renderWithProviders(<CommerceReport range={range} />);
    await screen.findByTestId("conversion-rate");
    expect(screen.queryByTestId("invoice-report")).not.toBeInTheDocument();
  });
});
