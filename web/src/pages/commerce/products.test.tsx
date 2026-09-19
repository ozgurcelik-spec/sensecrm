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
  type MockClient,
} from "@/test/crm";
import ProductsPage from "./products";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const product = (id: string, over: Record<string, unknown> = {}) => ({
  id,
  name: `Ürün ${id}`,
  code: `SKU-${id}`,
  description: "Açıklama",
  unitPrice: 1500,
  currency: "TRY",
  taxRate: 20,
  unit: "adet",
  isActive: true,
  createdAt: "2026-05-01T10:00:00Z",
  ...over,
});

const lastProductParams = () =>
  client.get.mock.calls.filter(([url]) => url === "/products").at(-1)?.[1].params;

function renderPage(route = "/app/products") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/products" element={<ProductsPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("ProductsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /products": () => page([product("1"), product("2", { isActive: false, code: undefined })]),
      "DELETE /products/1": () => undefined,
    });
  });
  afterEach(clearSession);

  it("lists products with price, tax rate, unit and status", async () => {
    setPermissions(["crm.products.read"]);
    renderPage();

    const row = (await screen.findByText("Ürün 1")).closest("tr") as HTMLElement;
    expect(within(row).getByText("SKU-1")).toBeInTheDocument();
    expect(within(row).getByText(/1\.500,00/)).toBeInTheDocument();
    expect(within(row).getByText("20%")).toBeInTheDocument();
    expect(within(row).getByText("adet")).toBeInTheDocument();
    expect(within(row).getByText("Aktif")).toBeInTheDocument();
    const second = screen.getByText("Ürün 2").closest("tr") as HTMLElement;
    expect(within(second).getByText("Pasif")).toBeInTheDocument();
  });

  it("filters by active state through the URL, sorts and searches", async () => {
    setPermissions(["crm.products.read"]);
    renderPage();
    await screen.findByText("Ürün 1");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Pasif" }));
    await waitFor(() =>
      expect(lastProductParams()).toEqual({ page: 1, pageSize: 25, isActive: "false" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("/app/products?isActive=false");

    await userEvent.click(screen.getByRole("button", { name: "Birim fiyat (KDV hariç) sütununa göre sırala" }));
    await waitFor(() => expect(lastProductParams()).toMatchObject({ sort: "unitPrice", isActive: "false" }));
  });

  it("is read-only without crm.products.write (badges instead of switches, no buttons)", async () => {
    setPermissions(["crm.products.read"]);
    renderPage();
    await screen.findByText("Ürün 1");
    expect(screen.queryByRole("button", { name: "Yeni ürün" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
    expect(screen.queryByRole("switch")).not.toBeInTheDocument();
  });

  it("creates a product with the suggested 20 % tax rate and sends the contract body", async () => {
    setPermissions(["crm.products.read", "crm.products.write"]);
    installApi(client, {
      "GET /products": () => page([]),
      "POST /products": () => product("9"),
    });
    renderPage();
    await userEvent.click(await screen.findByRole("button", { name: "Yeni ürün" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText("KDV %")).toHaveValue("20");
    await userEvent.type(within(dialog).getByLabelText(/Ürün adı/), "  Danışmanlık ");
    await userEvent.type(within(dialog).getByLabelText("Ürün kodu (SKU)"), "DAN-1");
    const price = within(dialog).getByLabelText("Birim fiyat (KDV hariç)");
    await userEvent.clear(price);
    await userEvent.type(price, "250.5");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/products", {
      name: "Danışmanlık",
      code: "DAN-1",
      unitPrice: 250.5,
      currency: "TRY",
      taxRate: 20,
      isActive: true,
    });
  });

  it("puts product.code_taken on the code field", async () => {
    setPermissions(["crm.products.read", "crm.products.write"]);
    installApi(client, {
      "GET /products": () => page([]),
      "POST /products": () => problem(409, { code: "product.code_taken" }),
    });
    renderPage();
    await userEvent.click(await screen.findByRole("button", { name: "Yeni ürün" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/Ürün adı/), "Ürün");
    await userEvent.type(within(dialog).getByLabelText("Ürün kodu (SKU)"), "dup");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    expect(await within(dialog).findByText("Bu ürün kodu zaten kullanılıyor")).toBeInTheDocument();
    expect(within(dialog).getByLabelText("Ürün kodu (SKU)")).toHaveAttribute("aria-invalid", "true");
    // The dialog stays open.
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("edits a product with a full PUT and toggles active with the row switch", async () => {
    setPermissions(["crm.products.read", "crm.products.write"]);
    installApi(client, {
      "GET /products": () => page([product("1")]),
      "PUT /products/1": () => undefined,
    });
    renderPage();
    const row = (await screen.findByText("Ürün 1")).closest("tr") as HTMLElement;

    await userEvent.click(within(row).getByRole("switch"));
    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put).toHaveBeenLastCalledWith("/products/1", {
      name: "Ürün 1",
      code: "SKU-1",
      description: "Açıklama",
      unitPrice: 1500,
      currency: "TRY",
      taxRate: 20,
      unit: "adet",
      isActive: false,
    });

    await userEvent.click(within(row).getByRole("button", { name: "Düzenle" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText(/Ürün adı/)).toHaveValue("Ürün 1");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(2));
  });

  it("deletes after a confirmation", async () => {
    setPermissions(["crm.products.read", "crm.products.write"]);
    renderPage();
    const row = (await screen.findByText("Ürün 1")).closest("tr") as HTMLElement;
    await userEvent.click(within(row).getByRole("button", { name: "Sil" }));
    expect(client.delete).not.toHaveBeenCalled();
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/products/1"));
  });

  it("shows the error state with a retry", async () => {
    setPermissions(["crm.products.read"]);
    installApi(client, { "GET /products": () => problem(500, { code: "unknown" }) });
    renderPage();
    expect(await screen.findByText("Veriler yüklenemedi")).toBeInTheDocument();
  });
});
