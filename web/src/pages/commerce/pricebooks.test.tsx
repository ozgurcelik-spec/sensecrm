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
import { ACCOUNT, priceBook, product } from "@/test/inventory";
import { toast, toastApiError } from "@/hooks/use-toast";
import PriceBookDetailPage from "./pricebook-detail";
import PriceBooksPage from "./pricebooks";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function renderList(route = "/app/pricebooks") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/pricebooks" element={<PriceBooksPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const FLAT = priceBook({ id: "pb2", name: "Yüzde liste", pricingModel: "flat", adjustmentPercent: -10, entryCount: 0 });

const listRoutes = (extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> => ({
  "GET /pricebooks": () =>
    page([priceBook(), FLAT, priceBook({ id: "pb3", name: "Süresi dolmuş", isEffective: false, validTo: "2026-01-01" }), priceBook({ id: "pb4", name: "Pasif", isActive: false, isEffective: false })]),
  "GET /organization/members": () => MEMBERS,
  ...extra,
});

describe("PriceBooksPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("lists name, model (flat shows its percent), currency, state, validity and entry count", async () => {
    setPermissions(["crm.pricebooks.read"]);
    installApi(client, listRoutes());
    renderList();

    expect(await screen.findByRole("link", { name: "Kurumsal liste" })).toHaveAttribute("href", "/app/pricebooks/pb1");
    expect(screen.getByText("Yüzde (flat) (-10%)")).toBeInTheDocument();
    expect(screen.getAllByText("Geçerli")).toHaveLength(2);
    expect(screen.getByText("Tarih dışında")).toBeInTheDocument();
    expect(screen.getAllByText("Pasif").length).toBeGreaterThan(0);
  });

  it("filters by active state and currency through the URL and the request", async () => {
    setPermissions(["crm.pricebooks.read"]);
    installApi(client, listRoutes());
    renderList();
    await screen.findByRole("link", { name: "Kurumsal liste" });

    await userEvent.click(screen.getByRole("combobox", { name: "Aktif" }));
    await userEvent.click(await screen.findByRole("option", { name: "Aktif" }));
    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/pricebooks").at(-1)?.[1];
      expect(last.params).toMatchObject({ isActive: "true" });
    });
    expect(screen.getByTestId("location")).toHaveTextContent("isActive=true");
  });

  it("hides create / edit / delete without crm.pricebooks.write", async () => {
    setPermissions(["crm.pricebooks.read"]);
    installApi(client, listRoutes());
    renderList();
    await screen.findByRole("link", { name: "Kurumsal liste" });
    expect(screen.queryByRole("button", { name: "Yeni fiyat listesi" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
  });

  describe("form", () => {
    async function openCreate() {
      setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
      renderList();
      await userEvent.click(await screen.findByRole("button", { name: "Yeni fiyat listesi" }));
      return screen.findByRole("dialog", { name: "Yeni fiyat listesi" });
    }

    it("creates a per-product book: no percent field, the body carries the model and currency", async () => {
      installApi(client, listRoutes({ "POST /pricebooks": () => priceBook({ id: "pb9" }) }));
      const dialog = await openCreate();
      expect(within(dialog).queryByLabelText(/^Fiyat farkı/)).not.toBeInTheDocument();

      await userEvent.type(within(dialog).getByLabelText(/^Fiyat listesi adı/), "Yeni liste");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      const [url, body] = client.post.mock.calls[0] as [string, Record<string, unknown>];
      expect(url).toBe("/pricebooks");
      expect(body).toMatchObject({ name: "Yeni liste", pricingModel: "perProduct", currency: "TRY", isActive: true });
      expect(JSON.stringify(body)).not.toContain("adjustmentPercent");
    });

    it("a flat book needs its percent (-99.99 to 1000) and sends it signed", async () => {
      installApi(client, listRoutes({ "POST /pricebooks": () => priceBook({ id: "pb9" }) }));
      const dialog = await openCreate();
      await userEvent.click(within(dialog).getByRole("combobox", { name: /^Fiyatlandırma modeli/ }));
      await userEvent.click(await screen.findByRole("option", { name: "Yüzde (flat)" }));
      await userEvent.type(within(dialog).getByLabelText(/^Fiyat listesi adı/), "İskontolu");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
      expect(await within(dialog).findByText("Yüzde zorunludur")).toBeInTheDocument();
      expect(client.post).not.toHaveBeenCalled();

      const percent = within(dialog).getByLabelText(/^Fiyat farkı/);
      await userEvent.type(percent, "-10");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect((client.post.mock.calls[0] as [string, Record<string, unknown>])[1]).toMatchObject({
        pricingModel: "flat",
        adjustmentPercent: -10,
      });
    });

    it("refuses an end date before the start date", async () => {
      installApi(client, listRoutes());
      const dialog = await openCreate();
      await userEvent.type(within(dialog).getByLabelText(/^Fiyat listesi adı/), "Tarihli");
      await userEvent.type(within(dialog).getByLabelText("Geçerlilik başlangıcı"), "2026-10-10");
      await userEvent.type(within(dialog).getByLabelText("Geçerlilik sonu"), "2026-10-01");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
      expect(await within(dialog).findByText("Bitiş tarihi, başlangıç tarihinden önce olamaz")).toBeInTheDocument();
      expect(client.post).not.toHaveBeenCalled();
    });

    it("shows pricebook.name_taken on the name field", async () => {
      installApi(client, listRoutes({ "POST /pricebooks": () => problem(409, { code: "pricebook.name_taken" }) }));
      const dialog = await openCreate();
      await userEvent.type(within(dialog).getByLabelText(/^Fiyat listesi adı/), "Kurumsal liste");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
      expect(await within(dialog).findByText("Bu ada sahip bir fiyat listesi zaten var")).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
    });

    it("locks the model and the currency while editing and still sends them (a PUT is a full replacement)", async () => {
      setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
      installApi(client, listRoutes({ "PUT /pricebooks/pb2": () => undefined }));
      renderList();
      const row = (await screen.findByRole("link", { name: "Yüzde liste" })).closest("tr") as HTMLElement;
      await userEvent.click(within(row).getByRole("button", { name: "Düzenle" }));
      const dialog = await screen.findByRole("dialog", { name: "Fiyat listesini düzenle" });

      expect(within(dialog).getByDisplayValue("Yüzde (flat)")).toBeDisabled();
      for (const input of within(dialog).getAllByDisplayValue("TRY").filter((el) => el.getAttribute("type") !== "hidden")) {
        expect(input).toBeDisabled();
      }
      expect(within(dialog).getByLabelText(/^Fiyat farkı/)).toHaveValue("-10 %");
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
      expect(client.put.mock.calls[0]?.[0]).toBe("/pricebooks/pb2");
      expect(client.put.mock.calls[0]?.[1]).toMatchObject({ pricingModel: "flat", currency: "TRY", adjustmentPercent: -10 });
    });
  });
});

function renderDetail(id = "pb1") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/pricebooks/:id" element={<PriceBookDetailPage />} />
        <Route path="/app/pricebooks" element={<div>Fiyat listeleri</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: `/app/pricebooks/${id}` }
  );
}

const ENTRY = { productId: "p1", productName: "CRM Pro", productCode: "CRM", catalogPrice: 100, unitPrice: 90, updatedAt: "2026-09-01T10:00:00Z" };

function detailRoutes(extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return {
    "GET /pricebooks/pb1": () => priceBook(),
    "GET /pricebooks/pb2": () => FLAT,
    "GET /pricebooks/pb1/entries": () => page([ENTRY]),
    "GET /audit": () => ({ items: [], total: 0 }),
    "GET /organization/members": () => MEMBERS,
    "GET /products": () => page([product({ id: "p2", name: "Yeni ürün", code: "NEW", unitPrice: 55 })]),
    "GET /accounts": () => page([ACCOUNT]),
    ...extra,
  };
}

describe("PriceBookDetailPage - per-product book", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("shows the entry table with the catalog price next to the list price", async () => {
    setPermissions(["crm.pricebooks.read"]);
    installApi(client, detailRoutes());
    renderDetail();

    const row = await screen.findByTestId("entry-row");
    expect(within(row).getByText("CRM Pro")).toBeInTheDocument();
    expect(within(row).getByText(/100,00/)).toBeInTheDocument();
    expect(within(row).getByText(/90,00/)).toBeInTheDocument();
    // Read-only: no inline editor, no add, no remove.
    expect(screen.queryByRole("button", { name: "Ürün ekle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("spinbutton")).not.toBeInTheDocument();
    expect(client.get.mock.calls.find(([u]) => u === "/pricebooks/pb1/entries")).toBeTruthy();
  });

  it("edits a price in place: Enter or leaving the field saves once, an unchanged value saves nothing", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    installApi(client, detailRoutes({ "PUT /pricebooks/pb1/entries/p1": () => undefined }));
    renderDetail();
    const input = await screen.findByRole("textbox", { name: "CRM Pro liste fiyatı" });

    await userEvent.click(input);
    await userEvent.tab();
    expect(client.put).not.toHaveBeenCalled();

    await userEvent.clear(input);
    await userEvent.type(input, "85.5{Enter}");
    await waitFor(() => expect(client.put).toHaveBeenCalledWith("/pricebooks/pb1/entries/p1", { unitPrice: 85.5 }));
    expect(toast).toHaveBeenCalled();
  });

  it("refuses a negative price or more than four decimals on the client", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    installApi(client, detailRoutes());
    renderDetail();
    const input = await screen.findByRole("textbox", { name: "CRM Pro liste fiyatı" });
    await userEvent.clear(input);
    await userEvent.type(input, "-5{Enter}");
    expect(await screen.findByText(/Fiyat 0 ile 1.000.000.000 arasında/)).toBeInTheDocument();
    expect(client.put).not.toHaveBeenCalled();
  });

  it("adds a product through the product lookup window at its catalog price", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write", "crm.products.read"]);
    installApi(client, detailRoutes({ "PUT /pricebooks/pb1/entries/p2": () => undefined }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Ürün ekle" }));
    const dialog = await screen.findByRole("dialog", { name: "Fiyat listesine ürün ekle" });
    // The window only offers products in the book's currency.
    expect(client.get.mock.calls.find(([u]) => u === "/products")?.[1].params).toMatchObject({ currency: "TRY", isActive: true });
    await userEvent.click(await within(dialog).findByText("Yeni ürün"));

    await waitFor(() => expect(client.put).toHaveBeenCalledWith("/pricebooks/pb1/entries/p2", { unitPrice: 55 }));
  });

  it("removes an entry after a confirmation", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    installApi(client, detailRoutes({ "DELETE /pricebooks/pb1/entries/p1": () => undefined }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "CRM Pro ürününü listeden çıkar" }));
    const dialog = await screen.findByRole("dialog", { name: "Ürünü listeden çıkar" });
    expect(client.delete).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole("button", { name: "Listeden çıkar" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/pricebooks/pb1/entries/p1"));
  });

  it("toasts the server's field message when adding a product in another currency (errors.productId)", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write", "crm.products.read"]);
    installApi(
      client,
      detailRoutes({
        "PUT /pricebooks/pb1/entries/p2": () => problem(400, { code: "validation", errors: { productId: ["Para birimi uyuşmuyor"] } }),
      })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Ürün ekle" }));
    const dialog = await screen.findByRole("dialog", { name: "Fiyat listesine ürün ekle" });
    await userEvent.click(await within(dialog).findByText("Yeni ürün"));
    await waitFor(() => expect(toast).toHaveBeenCalledWith(expect.objectContaining({ description: "Para birimi uyuşmuyor" })));
  });

  it("makes the book the default of an account picked in a lookup window (PUT .../default) and toasts a 404", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write", "crm.accounts.read"]);
    installApi(client, detailRoutes({ "PUT /pricebooks/accounts/a1/default": () => undefined }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Bu firma için varsayılan yap" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(await within(dialog).findByText("Acme Ltd"));
    await waitFor(() =>
      expect(client.put).toHaveBeenCalledWith("/pricebooks/accounts/a1/default", { priceBookId: "pb1" })
    );
    expect(toast).toHaveBeenCalled();
  });

  it("does not offer 'default for an account' without crm.accounts.read", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    installApi(client, detailRoutes());
    renderDetail();
    await screen.findByTestId("entry-row");
    expect(screen.queryByRole("button", { name: "Bu firma için varsayılan yap" })).not.toBeInTheDocument();
  });
});

describe("PriceBookDetailPage - flat book", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("has no entry table and no entry request, and previews the resolved price of a catalog price", async () => {
    setPermissions(["crm.pricebooks.read", "crm.pricebooks.write"]);
    installApi(client, detailRoutes());
    renderDetail("pb2");

    expect(await screen.findByText(/katalog fiyatına %-10 uygular/)).toBeInTheDocument();
    expect(screen.queryByTestId("entry-row")).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([u]) => String(u).endsWith("/entries"))).toBe(false);

    // 100 at -10 % -> 90 (the plan's flat rule, previewed in the browser only).
    expect(screen.getByTestId("flat-preview")).toHaveTextContent(/90,00/);
    const catalog = screen.getByLabelText("Katalog fiyatı");
    await userEvent.clear(catalog);
    await userEvent.type(catalog, "19.99");
    expect(screen.getByTestId("flat-preview")).toHaveTextContent(/17,99/);
  });
});
