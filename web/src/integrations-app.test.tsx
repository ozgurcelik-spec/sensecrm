import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, within } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { testI18n } from "@/test-utils";
import { installApi, page, type MockClient } from "@/test/crm";
import { EVENTS, integrationsStatus, webhook } from "@/test/integrations";
import { platformMe, subscription } from "@/test/platform";
import { PERMISSIONS, type Me } from "@/types";
import App from "./App";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const requested = (prefix: string) => client.get.mock.calls.some(([url]) => String(url).startsWith(prefix));

function renderApp(route: string, me: Me) {
  installApi(client, {
    "GET /me": () => me,
    "GET /approvals/summary": () => ({ pendingCount: 0 }),
    "GET /notifications/unread-count": () => ({ unreadCount: 0, criticalUnreadCount: 0 }),
    "GET /integrations/status": () => integrationsStatus(),
    "GET /integrations/webhook-events": () => EVENTS,
    "GET /integrations/webhooks": () => page([webhook("w1", { name: "ERP köprüsü" })]),
    "GET /subscription": () => ({}),
    "GET /onboarding": () => ({ dismissed: true, completedCount: 0, totalCount: 0, items: [] }),
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

const MANAGE = PERMISSIONS.orgIntegrationsManage;

describe("App - integrations (route, navigation, gating)", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("adds 'Entegrasyonlar' to the settings menu for org.integrations.manage and opens the page", async () => {
    renderApp("/app/settings/integrations", platformMe([MANAGE], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Entegrasyonlar" })).toBeInTheDocument();
    expect(await screen.findByText("ERP köprüsü")).toBeInTheDocument();
    expect(
      within(screen.getByRole("navigation", { name: "Ayarlar" })).getByRole("link", { name: "Entegrasyonlar" })
    ).toHaveAttribute("href", "/app/settings/integrations");
  });

  it("hides the menu entry and answers the URL with the no-access page and no request without the permission", async () => {
    renderApp("/app/settings/integrations", platformMe(["crm.leads.read", PERMISSIONS.orgSettingsManage], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Entegrasyonlar" })).not.toBeInTheDocument();
    expect(requested("/integrations")).toBe(false);
  });

  it("does not offer the entry to an ordinary member on any page", async () => {
    renderApp("/app", platformMe(["crm.leads.read"], { subscription: subscription() }));
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    expect(screen.queryByRole("navigation", { name: "Ayarlar" })?.textContent ?? "").not.toContain("Entegrasyonlar");
  });

  it("plan without the integrations module: menu entry hidden, 'module disabled' page and not a single request", async () => {
    renderApp(
      "/app/settings/integrations",
      platformMe([MANAGE, PERMISSIONS.orgSettingsManage], { subscription: subscription({ modules: { integrations: false } }) })
    );
    expect(await screen.findByTestId("module-disabled")).toHaveTextContent("Bu modül planınıza dahil değil");
    expect(screen.getByTestId("module-disabled")).toHaveTextContent("Entegrasyonlar");
    expect(screen.queryByRole("link", { name: "Entegrasyonlar" })).not.toBeInTheDocument();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 30));
    });
    expect(requested("/integrations")).toBe(false);
  });

  it("read-only tenant: the page opens with the read-only note and the lists", async () => {
    renderApp("/app/settings/integrations", platformMe([MANAGE], { subscription: subscription({ accessLevel: "readOnly" }) }));
    expect(await screen.findByTestId("integrations-readonly")).toBeInTheDocument();
    expect(await screen.findByText("ERP köprüsü")).toBeInTheDocument();
    // The manage permission is kept in read-only mode (M7 rule): the menu entry stays.
    expect(screen.getAllByRole("link", { name: "Entegrasyonlar" }).length).toBeGreaterThan(0);
  });

  it("blocked tenant (accessLevel none): blocked screen and no integrations request", async () => {
    renderApp("/app/settings/integrations", platformMe([MANAGE], { subscription: subscription({ accessLevel: "none" }) }));
    await screen.findByRole("heading", { level: 3 });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 30));
    });
    expect(requested("/integrations")).toBe(false);
  });

  it("opens each tab from ?tab= and falls back to the webhook list for an unknown tab", async () => {
    renderApp("/app/settings/integrations?tab=nonsense", platformMe([MANAGE], { subscription: subscription() }));
    expect(await screen.findByText("ERP köprüsü")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Webhook'lar" })).toHaveAttribute("aria-selected", "true");
  });
});
