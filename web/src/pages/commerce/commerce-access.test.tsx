import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { testI18n } from "@/test-utils";
import { installApi, meWith, page, type MockClient } from "@/test/crm";
import App from "@/App";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function renderApp(route: string, permissions: string[]) {
  const me = meWith(permissions);
  installApi(client, {
    "GET /me": () => me,
    "GET /approvals/summary": () => ({ pendingCount: 0 }),
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

const navLink = (name: string) => screen.queryByRole("link", { name });
const NO_ACCESS = "Erişim izniniz yok";

describe("commerce navigation and route permissions", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows Ürünler, Teklifler and Siparişler after Fırsatlar, each behind its own read permission", async () => {
    renderApp("/app", ["crm.deals.read", "crm.quotes.read"]);

    expect(await screen.findByRole("link", { name: "Teklifler" })).toHaveAttribute("href", "/app/quotes");
    expect(navLink("Ürünler")).not.toBeInTheDocument();
    expect(navLink("Siparişler")).not.toBeInTheDocument();
  });

  it("shows all three entries with all read permissions, in menu order", async () => {
    renderApp("/app", ["crm.deals.read", "crm.products.read", "crm.quotes.read", "crm.orders.read"]);
    await screen.findByRole("link", { name: "Siparişler" });

    const nav = screen.getByRole("navigation", { name: "Modüller" });
    const labels = Array.from(nav.querySelectorAll("a")).map((a) => a.textContent?.trim());
    const deals = labels.indexOf("Fırsatlar");
    expect(labels.slice(deals, deals + 4)).toEqual(["Fırsatlar", "Ürünler", "Teklifler", "Siparişler"]);
    expect(navLink("Ürünler")).toHaveAttribute("href", "/app/products");
    expect(navLink("Siparişler")).toHaveAttribute("href", "/app/orders");
  });

  it.each([
    ["/app/products", "crm.products.read", "Ürünler"],
    ["/app/quotes", "crm.quotes.read", "Teklifler"],
    ["/app/orders", "crm.orders.read", "Siparişler"],
  ])("%s: the page for %s, NoAccess without it", async (path, permission, title) => {
    const first = renderApp(path, [permission]);
    expect(await screen.findByRole("heading", { name: title })).toBeInTheDocument();
    first.unmount();
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    client.get.mockClear();

    renderApp(path, ["crm.deals.read"]);
    expect(await screen.findByRole("heading", { name: NO_ACCESS })).toBeInTheDocument();
    expect(navLink(title)).not.toBeInTheDocument();
    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/me"));
    expect(client.get.mock.calls.some(([url]) => url === path.replace("/app", ""))).toBe(false);
  });

  it.each([
    ["/app/quotes/new", ["crm.quotes.read"]],
    ["/app/quotes/q1/edit", ["crm.quotes.read"]],
    ["/app/orders/new", ["crm.orders.read"]],
    ["/app/orders/o1/edit", ["crm.orders.read"]],
  ])("%s needs the write permission (read-only users get NoAccess)", async (path, permissions) => {
    renderApp(path, permissions);
    expect(await screen.findByRole("heading", { name: NO_ACCESS })).toBeInTheDocument();
  });
});
