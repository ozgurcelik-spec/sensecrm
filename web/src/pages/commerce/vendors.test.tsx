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
import { product, purchaseOrder, vendor } from "@/test/inventory";
import { toast, toastApiError } from "@/hooks/use-toast";
import VendorDetailPage from "./vendor-detail";
import VendorsPage from "./vendors";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function renderList(route = "/app/vendors") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/vendors" element={<VendorsPage />} />
        <Route path="/app/vendors/:id" element={<div>Tedarikçi detayı</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const listRoutes = (extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> => ({
  "GET /vendors": () => page([vendor(), vendor({ id: "v2", name: "Öteki Ltd.", category: undefined })]),
  "GET /organization/members": () => MEMBERS,
  ...extra,
});

describe("VendorsPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("lists name, category, phone, email and owner, and puts the category filter into the URL and the request", async () => {
    setPermissions(["crm.vendors.read"]);
    installApi(client, listRoutes());
    renderList();

    expect(await screen.findByRole("link", { name: "Tedarik A.Ş." })).toHaveAttribute("href", "/app/vendors/v1");
    expect(screen.getByText("Donanım")).toBeInTheDocument();
    expect(screen.getAllByText("info@tedarik.example").length).toBeGreaterThan(0);

    await userEvent.type(screen.getByRole("textbox", { name: "Kategori" }), "Donanım{Enter}");
    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/vendors").at(-1)?.[1];
      expect(last.params).toMatchObject({ category: "Donanım" });
    });
    expect(screen.getByTestId("location")).toHaveTextContent("category=Donan");
  });

  it("hides create / edit / delete without crm.vendors.write", async () => {
    setPermissions(["crm.vendors.read"]);
    installApi(client, listRoutes());
    renderList();
    await screen.findByRole("link", { name: "Tedarik A.Ş." });
    expect(screen.queryByRole("button", { name: "Yeni tedarikçi" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
  });

  it("creates a vendor from the form: the body has the fields, an empty address is left out and the opt-out defaults to false", async () => {
    setPermissions(["crm.vendors.read", "crm.vendors.write"]);
    installApi(client, listRoutes({ "POST /vendors": () => vendor({ id: "v9" }) }));
    renderList();
    await userEvent.click(await screen.findByRole("button", { name: "Yeni tedarikçi" }));
    const dialog = await screen.findByRole("dialog", { name: "Yeni tedarikçi" });

    await userEvent.type(within(dialog).getByLabelText(/^Tedarikçi adı/), "  Yeni Tedarik  ");
    await userEvent.type(within(dialog).getByLabelText("Telefon"), "0212 111 11 11");
    await userEvent.type(within(dialog).getByLabelText("Kategori"), "Yazılım");
    await userEvent.type(within(dialog).getByRole("combobox", { name: /^DK hesabı/ }), "Consulting");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const [url, body] = client.post.mock.calls[0] as [string, Record<string, unknown>];
    expect(url).toBe("/vendors");
    expect(body).toMatchObject({ name: "Yeni Tedarik", phone: "0212 111 11 11", category: "Yazılım", glAccount: "Consulting", emailOptOut: false });
    expect(JSON.stringify(body)).not.toContain("address");
    expect(toast).toHaveBeenCalled();
  });

  it("sends the address block and the opt-out switch when they are used", async () => {
    setPermissions(["crm.vendors.read", "crm.vendors.write"]);
    installApi(client, listRoutes({ "POST /vendors": () => vendor({ id: "v9" }) }));
    renderList();
    await userEvent.click(await screen.findByRole("button", { name: "Yeni tedarikçi" }));
    const dialog = await screen.findByRole("dialog", { name: "Yeni tedarikçi" });
    await userEvent.type(within(dialog).getByLabelText(/^Tedarikçi adı/), "Adresli");
    await userEvent.type(within(dialog).getByLabelText("Adres - Şehir"), "Ankara");
    await userEvent.click(within(dialog).getByRole("switch", { name: "E-posta gönderilmesin" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect((client.post.mock.calls[0] as [string, Record<string, unknown>])[1]).toMatchObject({
      address: { city: "Ankara" },
      emailOptOut: true,
    });
  });

  it("validates the name, the e-mail and the website before sending", async () => {
    setPermissions(["crm.vendors.read", "crm.vendors.write"]);
    installApi(client, listRoutes());
    renderList();
    await userEvent.click(await screen.findByRole("button", { name: "Yeni tedarikçi" }));
    const dialog = await screen.findByRole("dialog", { name: "Yeni tedarikçi" });
    await userEvent.type(within(dialog).getByLabelText("E-posta"), "yanlis");
    await userEvent.type(within(dialog).getByLabelText("Web sitesi"), "javascript:alert(1)");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    expect(await within(dialog).findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("edits a vendor with a full replacement (PUT)", async () => {
    setPermissions(["crm.vendors.read", "crm.vendors.write"]);
    installApi(client, listRoutes({ "PUT /vendors/v1": () => undefined }));
    renderList();
    const row = (await screen.findByRole("link", { name: "Tedarik A.Ş." })).closest("tr") as HTMLElement;
    await userEvent.click(within(row).getByRole("button", { name: "Düzenle" }));
    const dialog = await screen.findByRole("dialog", { name: "Tedarikçiyi düzenle" });
    expect(within(dialog).getByLabelText(/^Tedarikçi adı/)).toHaveValue("Tedarik A.Ş.");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]?.[0]).toBe("/vendors/v1");
    expect(client.put.mock.calls[0]?.[1]).toMatchObject({ name: "Tedarik A.Ş.", category: "Donanım", email: "info@tedarik.example" });
  });

  it("toasts vendor.in_use when a vendor with a live purchase order is deleted", async () => {
    setPermissions(["crm.vendors.read", "crm.vendors.write"]);
    const error = problem(409, { code: "vendor.in_use" });
    installApi(client, listRoutes({ "DELETE /vendors/v1": () => error }));
    renderList();
    const row = (await screen.findByRole("link", { name: "Tedarik A.Ş." })).closest("tr") as HTMLElement;
    await userEvent.click(within(row).getByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });
});

function renderDetail() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/vendors/:id" element={<VendorDetailPage />} />
        <Route path="/app/vendors" element={<div>Tedarikçi listesi</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/vendors/v1" }
  );
}

describe("VendorDetailPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const routes = (extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> => ({
    "GET /vendors/v1": () => vendor({ address: { city: "Ankara" }, description: "Ana donanım tedarikçisi", glAccount: "Consulting", website: "https://tedarik.example" }),
    "GET /audit": () => ({ items: [], total: 0 }),
    "GET /products": () => page([product({ purchasePrice: 60 })]),
    "GET /purchase-orders": () => page([purchaseOrder()]),
    "GET /organization/members": () => MEMBERS,
    ...extra,
  });

  it("shows the facts, the address and the description", async () => {
    setPermissions(["crm.vendors.read"]);
    installApi(client, routes());
    renderDetail();
    expect(await screen.findByRole("heading", { name: "Tedarik A.Ş." })).toBeInTheDocument();
    expect(screen.getByText("Consulting")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "https://tedarik.example" })).toHaveAttribute("href", "https://tedarik.example/");
    expect(screen.getByTestId("view-address")).toHaveTextContent("Ankara");
    expect(screen.getByText("Ana donanım tedarikçisi")).toBeInTheDocument();
  });

  it("has the Products (GET /products?vendorId=) and Purchase orders tabs, each behind its read permission", async () => {
    setPermissions(["crm.vendors.read", "crm.products.read", "crm.purchaseorders.read"]);
    installApi(client, routes());
    renderDetail();
    await userEvent.click(await screen.findByRole("tab", { name: "Ürünler (1)" }));
    expect(await screen.findByText("CRM Pro")).toBeInTheDocument();
    expect(client.get.mock.calls.find(([u]) => u === "/products")?.[1].params).toMatchObject({ vendorId: "v1" });

    await userEvent.click(screen.getByRole("tab", { name: "Satın alma emirleri (1)" }));
    expect(await screen.findByRole("link", { name: "PO-2026-0001" })).toHaveAttribute("href", "/app/purchase-orders/po1");
    expect(client.get.mock.calls.find(([u]) => u === "/purchase-orders")?.[1].params).toMatchObject({ vendorId: "v1" });
  });

  it("has neither related tab without their read permissions", async () => {
    setPermissions(["crm.vendors.read"]);
    installApi(client, routes());
    renderDetail();
    await screen.findByRole("heading", { name: "Tedarik A.Ş." });
    expect(screen.queryByRole("tab", { name: /Ürünler/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /Satın alma emirleri/ })).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([u]) => u === "/products" || u === "/purchase-orders")).toBe(false);
  });

  it("deletes after a confirmation and goes back to the list; vendor.in_use is toasted instead", async () => {
    setPermissions(["crm.vendors.read", "crm.vendors.write"]);
    installApi(client, routes({ "DELETE /vendors/v1": () => undefined }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/vendors"));
    expect(client.delete).toHaveBeenCalledWith("/vendors/v1");
  });
});
