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
import { preferences, tenantSettings } from "@/test/notifications";
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
    "GET /notifications/unread-count": () => ({ unreadCount: 4, criticalUnreadCount: 0 }),
    "GET /notifications": () => page([]),
    "GET /notifications/preferences": () => preferences(),
    "GET /notifications/settings": () => tenantSettings(),
    "GET /notifications/deliveries": () => page([]),
    "GET /notifications/deliveries/summary": () => ({ counts: { pending: 0, sending: 0, sent: 0, dead: 0, skipped: 0 }, sentToday: 0 }),
    "GET /notifications/deliveries/recipient-issues": () => [],
    "GET /organization/members": () => [],
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

const MANAGE = PERMISSIONS.orgNotificationsManage;

describe("App - notifications (routes, navigation, gating)", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows the bell to every member, even one without any permission", async () => {
    renderApp("/app", platformMe([], { subscription: subscription() }));
    const bell = await screen.findByRole("button", { name: "Bildirimler: 4 okunmamış" });
    expect(bell.querySelector("svg.lucide-bell")).not.toBeNull();
  });

  it("uses the clipboard icon for the approvals bell", async () => {
    installApi(client, {
      "GET /me": () => platformMe([PERMISSIONS.crmApprovalsDecide], { subscription: subscription() }),
      "GET /approvals/summary": () => ({ pendingCount: 2 }),
      "GET /notifications/unread-count": () => ({ unreadCount: 0, criticalUnreadCount: 0 }),
    });
    const { ApprovalsBell } = await import("@/components/shell/approvals-bell");
    const { renderWithProviders } = await import("@/test-utils");
    const { setPermissions } = await import("@/test/crm");
    setPermissions([PERMISSIONS.crmApprovalsDecide]);
    renderWithProviders(<ApprovalsBell />);
    const link = await screen.findByRole("link", { name: "Bekleyen onaylar: 2" });
    expect(link.querySelector("svg.lucide-clipboard-check")).not.toBeNull();
    expect(link.querySelector("svg.lucide-bell")).toBeNull();
  });

  it("adds 'Bildirimler' to the settings menu for org.notifications.manage and opens the page", async () => {
    renderApp("/app/settings/notifications", platformMe([MANAGE], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Bildirimler" })).toBeInTheDocument();
    expect(await screen.findByLabelText("Gönderen adı")).toBeInTheDocument();
    expect(
      within(screen.getByRole("navigation", { name: "Ayarlar" })).getByRole("link", { name: "Bildirimler" })
    ).toHaveAttribute("href", "/app/settings/notifications");
  });

  it("hides the menu entry and answers the URL with the no-access page and no request without the permission", async () => {
    renderApp("/app/settings/notifications", platformMe(["crm.leads.read"], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Bildirimler" })).not.toBeInTheDocument();
    expect(requested("/notifications/settings")).toBe(false);
    expect(requested("/notifications/deliveries")).toBe(false);
  });

  it("does not offer the settings entry to an ordinary member on any page", async () => {
    renderApp("/app", platformMe(["crm.leads.read"], { subscription: subscription() }));
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    expect(screen.queryByRole("navigation", { name: "Ayarlar" })?.textContent ?? "").not.toContain("Bildirimler");
  });

  it("opens the notification page and the preferences page without any permission", async () => {
    renderApp("/app/notifications", platformMe([], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Bildirimler" })).toBeInTheDocument();
    expect(await screen.findByText("Bildirim yok")).toBeInTheDocument();
  });

  it("opens the preferences page without any permission", async () => {
    renderApp("/app/notifications/preferences", platformMe([], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Bildirim tercihleri" })).toBeInTheDocument();
    expect(await screen.findByTestId("preferences-matrix")).toBeInTheDocument();
  });

  it("makes no notification request while the tenant is blocked (blocked screen)", async () => {
    renderApp("/app/notifications", platformMe([], { subscription: subscription({ accessLevel: "none" }) }));
    await screen.findByRole("heading", { level: 3 });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 30));
    });
    expect(requested("/notifications")).toBe(false);
  });
});
