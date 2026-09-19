import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { createTestI18n, testI18n } from "@/test-utils";
import { installApi, meWith, page, type MockClient } from "@/test/crm";
import { campaign } from "@/test/campaigns";
import App from "./App";

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
    "GET /campaigns": () => page([campaign("c1", { name: "Sonbahar E-posta" })]),
    "GET /campaigns/c1": () => campaign("c1", { name: "Sonbahar E-posta" }),
    "GET /campaigns/c1/metrics": () => ({
      campaignId: "c1",
      memberCount: 0,
      leadCount: 0,
      contactCount: 0,
      statusCounts: { added: 0, sent: 0, responded: 0, converted: 0, unsubscribed: 0 },
      contactedCount: 0,
      responseCount: 0,
      responseRate: 0,
      convertedCount: 0,
      conversionRate: 0,
      currency: "TRY",
    }),
    "GET /organization/members": () => [],
    "GET /leads": () => page([]),
    "GET /deals/board": () => ({ pipelineId: "p1", stages: [] }),
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

describe("App - campaigns navigation and route permissions", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows the Campaigns entry between Deals and Activities and opens the list with crm.campaigns.read", async () => {
    renderApp("/app/campaigns", ["crm.campaigns.read", "crm.deals.read", "crm.activities.read"]);

    expect(await screen.findByRole("heading", { name: "Kampanyalar" })).toBeInTheDocument();
    expect(await screen.findByText("Sonbahar E-posta")).toBeInTheDocument();
    expect(navLink("Kampanyalar")).toHaveAttribute("href", "/app/campaigns");

    const labels = screen
      .getAllByRole("link")
      .map((link) => link.textContent)
      .filter((text) => ["Fırsatlar", "Kampanyalar", "Aktiviteler"].includes(text ?? ""));
    expect(labels.indexOf("Fırsatlar")).toBeLessThan(labels.indexOf("Kampanyalar"));
    expect(labels.indexOf("Kampanyalar")).toBeLessThan(labels.indexOf("Aktiviteler"));
  });

  it("opens the detail route with crm.campaigns.read", async () => {
    renderApp("/app/campaigns/c1", ["crm.campaigns.read"]);
    expect(await screen.findByRole("heading", { name: "Sonbahar E-posta" })).toBeInTheDocument();
  });

  it.each(["/app/campaigns", "/app/campaigns/c1"])(
    "hides the menu entry and answers %s with the no-access page without crm.campaigns.read",
    async (route) => {
      renderApp(route, ["crm.deals.read", "crm.campaigns.write"]);

      expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
      expect(navLink("Kampanyalar")).not.toBeInTheDocument();
      expect(client.get.mock.calls.some(([u]) => String(u).startsWith("/campaigns"))).toBe(false);
    }
  );

  it("keeps the English labels of the new navigation entry and permissions", () => {
    const en = createTestI18n("en");
    expect(en.t("navigation:campaigns")).toBe("Campaigns");
    expect(en.t("users:permissions.crm.campaigns.read")).toBe("View campaigns");
    expect(en.t("users:permissions.crm.campaigns.write")).toBeTruthy();
    const tr = createTestI18n("tr");
    expect(tr.t("navigation:campaigns")).toBe("Kampanyalar");
    expect(tr.t("users:permissions.crm.campaigns.read")).toBe("Kampanyaları görüntüleme");
  });
});
