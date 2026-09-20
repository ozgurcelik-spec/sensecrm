/** M9C gating: navigation, routes, permissions, the plan flag (`commerce`) and read-only mode, through the whole App. */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { testI18n } from "@/test-utils";
import { installApi, page, type MockClient } from "@/test/crm";
import { invoiceSummary, priceBook, vendor } from "@/test/inventory";
import { platformMe, subscription } from "@/test/platform";
import { PERMISSIONS, type Me } from "@/types";
import App from "@/App";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function renderApp(route: string, me: Me) {
  installApi(client, {
    "GET /me": () => me,
    "GET /approvals/summary": () => ({ pendingCount: 0 }),
    "GET /invoices": () => page([invoiceSummary()]),
    "GET /pricebooks": () => page([priceBook()]),
    "GET /vendors": () => page([vendor()]),
    "GET /purchase-orders": () => page([]),
    "GET /products": () => page([]),
    "GET /quotes": () => page([]),
    "GET /orders": () => page([]),
    "GET /organization/members": () => [],
    "GET /accounts": () => page([]),
    "GET /contacts": () => page([]),
    "GET /deals": () => page([]),
  });
  act(() => useAuthStore.setState({ token: "t", refreshToken: "r", hasHydrated: true, me }));
  window.history.pushState({}, "", route);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <I18nextProvider i18n={testI18n}>
      <QueryClientProvider client={queryClient}>
        <MantineProvider theme={mantineTheme} env="test">
          <App />
        </MantineProvider>
      </QueryClientProvider>
    </I18nextProvider>
  );
}

const link = (name: string) => screen.queryByRole("link", { name });
const requested = (prefix: string) => client.get.mock.calls.some(([url]) => String(url).startsWith(prefix));
const NO_ACCESS = "Erişim izniniz yok";

const AREAS = [
  { path: "/app/invoices", read: PERMISSIONS.crmInvoicesRead, write: PERMISSIONS.crmInvoicesWrite, menu: "Faturalar", api: "/invoices" },
  { path: "/app/pricebooks", read: PERMISSIONS.crmPriceBooksRead, write: PERMISSIONS.crmPriceBooksWrite, menu: "Fiyat Listeleri", api: "/pricebooks" },
  { path: "/app/vendors", read: PERMISSIONS.crmVendorsRead, write: PERMISSIONS.crmVendorsWrite, menu: "Tedarikçiler", api: "/vendors" },
  { path: "/app/purchase-orders", read: PERMISSIONS.crmPurchaseOrdersRead, write: PERMISSIONS.crmPurchaseOrdersWrite, menu: "Satın Alma Emirleri", api: "/purchase-orders" },
] as const;

describe("M9C navigation and route permissions", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows each new menu entry only with its own read permission", async () => {
    renderApp("/app", platformMe([PERMISSIONS.crmDealsRead, PERMISSIONS.crmInvoicesRead]));
    expect(await screen.findByRole("link", { name: "Faturalar" })).toHaveAttribute("href", "/app/invoices");
    for (const name of ["Fiyat Listeleri", "Tedarikçiler", "Satın Alma Emirleri"]) {
      expect(link(name), name).not.toBeInTheDocument();
    }
  });

  it("shows all four entries with all four read permissions", async () => {
    renderApp("/app", platformMe([PERMISSIONS.crmDealsRead, ...AREAS.map((a) => a.read)]));
    for (const area of AREAS) {
      expect(await screen.findByRole("link", { name: area.menu })).toHaveAttribute("href", area.path);
    }
  });

  it.each(AREAS)("$path: the page for $read, NoAccess without it and no request", async (area) => {
    const first = renderApp(area.path, platformMe([area.read]));
    expect(await screen.findByRole("heading", { name: area.menu === "Fiyat Listeleri" ? "Fiyat listeleri" : area.menu === "Satın Alma Emirleri" ? "Satın alma emirleri" : area.menu })).toBeInTheDocument();
    first.unmount();
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    client.get.mockClear();

    renderApp(area.path, platformMe([PERMISSIONS.crmDealsRead]));
    expect(await screen.findByRole("heading", { name: NO_ACCESS })).toBeInTheDocument();
    expect(link(area.menu)).not.toBeInTheDocument();
    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/me"));
    expect(requested(area.api)).toBe(false);
  });

  it.each([
    ["/app/invoices/new", [PERMISSIONS.crmInvoicesRead]],
    ["/app/invoices/i1/edit", [PERMISSIONS.crmInvoicesRead]],
    ["/app/purchase-orders/new", [PERMISSIONS.crmPurchaseOrdersRead]],
    ["/app/purchase-orders/po1/edit", [PERMISSIONS.crmPurchaseOrdersRead]],
  ])("%s needs the write permission (read-only users get NoAccess)", async (path, permissions) => {
    renderApp(path, platformMe(permissions));
    expect(await screen.findByRole("heading", { name: NO_ACCESS })).toBeInTheDocument();
  });

  it("the detail routes need only the read permission", async () => {
    renderApp("/app/pricebooks/pb1", platformMe([PERMISSIONS.crmPriceBooksRead]));
    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/pricebooks/pb1"));
    expect(screen.queryByRole("heading", { name: NO_ACCESS })).not.toBeInTheDocument();
  });
});

describe("M9C plan flag: the commerce module covers all four areas", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  const off = () =>
    platformMe(Object.values(PERMISSIONS), {
      subscription: subscription({ modules: { workflows: true, commerce: false, service: true, marketing: true } }),
    });

  it("hides the four menu entries", async () => {
    renderApp("/app", off());
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    for (const area of AREAS) expect(link(area.menu), area.menu).not.toBeInTheDocument();
  });

  it.each(["/app/invoices", "/app/invoices/i1", "/app/pricebooks", "/app/pricebooks/pb1", "/app/vendors", "/app/vendors/v1", "/app/purchase-orders", "/app/purchase-orders/new"])(
    "answers %s with the module-disabled page and requests nothing",
    async (route) => {
      renderApp(route, off());
      const card = await screen.findByTestId("module-disabled");
      expect(within(card).getByRole("heading", { name: "Bu modül planınıza dahil değil" })).toBeInTheDocument();
      expect(card).toHaveTextContent("Ticaret");
      for (const prefix of ["/invoices", "/pricebooks", "/vendors", "/purchase-orders"]) {
        expect(requested(prefix), prefix).toBe(false);
      }
    }
  );

  it("makes no request for the new areas on the home page either", async () => {
    renderApp("/app", off());
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    for (const prefix of ["/invoices", "/pricebooks", "/vendors", "/purchase-orders"]) {
      expect(requested(prefix), prefix).toBe(false);
    }
  });
});

describe("M9C read-only mode (trial over / suspended)", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  const readOnly = () =>
    platformMe(Object.values(PERMISSIONS), { subscription: subscription({ accessLevel: "readOnly", status: "trial_expired" }) });

  it.each([
    ["/app/invoices", "Yeni fatura"],
    ["/app/pricebooks", "Yeni fiyat listesi"],
    ["/app/vendors", "Yeni tedarikçi"],
    ["/app/purchase-orders", "Yeni satın alma emri"],
  ])("%s stays readable and offers no write action (%s)", async (route, createLabel) => {
    renderApp(route, readOnly());
    await waitFor(() => expect(client.get.mock.calls.some(([url]) => String(url) === route.replace("/app", ""))).toBe(true));
    await screen.findAllByRole("table");
    expect(screen.queryByRole("button", { name: createLabel })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
  });

  it("the editor routes are closed in read-only mode (write permissions are withdrawn)", async () => {
    renderApp("/app/invoices/new", readOnly());
    expect(await screen.findByRole("heading", { name: NO_ACCESS })).toBeInTheDocument();
  });
});
