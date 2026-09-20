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
import { PLANS, orgRow, platformMe, subscription } from "@/test/platform";
import { PERMISSIONS, type Me, type MeSubscription } from "@/types";
import App from "./App";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

/** Every permission key the web app knows: an administrator with a tenant's full permission set. */
const ALL_PERMISSIONS = Object.values(PERMISSIONS);

function renderApp(route: string, me: Me) {
  installApi(client, {
    "GET /me": () => me,
    "GET /approvals/summary": () => ({ pendingCount: 0 }),
    "GET /platform/organizations": () => page([orgRow("t1", { name: "Acme A.Ş." })]),
    "GET /platform/organizations/t1": () => ({ ...orgRow("t1"), limits: { maxRecords: {}, modules: {} } }),
    "GET /platform/plans": () => PLANS,
    "GET /platform/audit": () => page([]),
    "GET /subscription": () => ({
      planCode: "business",
      planName: "Business",
      status: "active",
      accessLevel: "full",
      modules: {},
      limits: { maxRecords: {} },
      usage: { asOf: "2026-09-20T09:00:00Z", users: 1, pendingUsers: 0, records: {} },
      overLimit: [],
    }),
    "GET /onboarding": () => ({ dismissed: true, completedCount: 0, totalCount: 0, items: [] }),
    "GET /leads": () => page([]),
    "GET /campaigns": () => page([]),
    "GET /quotes": () => page([]),
    "GET /cases": () => page([]),
    "GET /service/sla-policies": () => [],
    "GET /workflows/rules": () => [],
    "GET /workflows/executions": () => page([]),
    "GET /approvals": () => page([]),
    "GET /organization/members": () => [],
    "GET /organization/roles": () => [],
    "GET /pipelines": () => [],
    "GET /deals/board": () => ({ pipelineId: "p1", stages: [] }),
    "GET /activities": () => page([]),
    "GET /reports/funnel": () => ({ pipelineId: "p1", stages: [] }),
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
const requestedUrls = () => client.get.mock.calls.map(([url]) => String(url));

const OFF: MeSubscription["modules"] = {
  workflows: false,
  commerce: false,
  service: false,
  marketing: false,
};

describe("App - platform console access", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("gives a platform admin the Platform navigation group and opens the organization list", async () => {
    renderApp("/app/platform/organizations", platformMe([], { isPlatformAdmin: true }));

    expect(await screen.findByRole("heading", { name: "Organizasyonlar" })).toBeInTheDocument();
    expect(await screen.findByText("Acme A.Ş.")).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Platform" });
    expect(within(nav).getByRole("link", { name: "Organizasyonlar" })).toHaveAttribute("href", "/app/platform/organizations");
    expect(within(nav).getByRole("link", { name: "Planlar" })).toHaveAttribute("href", "/app/platform/plans");
    expect(within(nav).getByRole("link", { name: "Platform denetimi" })).toHaveAttribute("href", "/app/platform/audit");
  });

  it("/app/platform redirects to the organization list", async () => {
    renderApp("/app/platform", platformMe([], { isPlatformAdmin: true }));
    expect(await screen.findByRole("heading", { name: "Organizasyonlar" })).toBeInTheDocument();
    expect(window.location.pathname).toBe("/app/platform/organizations");
  });

  it.each([
    ["/app/platform/organizations"],
    ["/app/platform/organizations/t1"],
    ["/app/platform/plans"],
    ["/app/platform/audit"],
    ["/app/platform"],
  ])("hides the menu and answers %s with the no-access page for a tenant admin holding every permission", async (route) => {
    renderApp(route, platformMe(ALL_PERMISSIONS, { isPlatformAdmin: false }));

    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Platform" })).not.toBeInTheDocument();
    expect(link("Platform denetimi")).not.toBeInTheDocument();
    expect(link("Planlar")).not.toBeInTheDocument();
    expect(requested("/platform")).toBe(false);
  });

  it("a platform admin without any tenant permission still sees the console but no tenant module entries", async () => {
    renderApp("/app/platform/plans", platformMe([], { isPlatformAdmin: true }));
    expect(await screen.findByRole("heading", { name: "Planlar" })).toBeInTheDocument();
    expect(link("Potansiyeller")).not.toBeInTheDocument();
  });

  it("does not show the Platform group to a user whose profile has no platform flag", async () => {
    renderApp("/app", platformMe(["crm.leads.read"], { isPlatformAdmin: false }));
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    expect(screen.queryByRole("navigation", { name: "Platform" })).not.toBeInTheDocument();
  });
});

describe("App - plan and usage settings", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("adds 'Plan ve kullanım' to the settings menu for org.settings.manage and opens the page", async () => {
    renderApp("/app/settings/plan", platformMe(["org.settings.manage"], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Plan ve kullanım" })).toBeInTheDocument();
    expect(await screen.findByTestId("plan-card")).toBeInTheDocument();
    expect(within(screen.getByRole("navigation", { name: "Ayarlar" })).getByRole("link", { name: "Plan ve kullanım" })).toHaveAttribute("href", "/app/settings/plan");
  });

  it("answers with the no-access page and no request without org.settings.manage", async () => {
    renderApp("/app/settings/plan", platformMe(["crm.leads.read"], { subscription: subscription() }));
    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(link("Plan ve kullanım")).not.toBeInTheDocument();
    expect(requested("/subscription")).toBe(false);
  });
});

describe("App - modules the plan lacks", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  const off = () => platformMe(ALL_PERMISSIONS, { subscription: subscription({ modules: OFF }) });

  it("hides every gated menu and settings entry", async () => {
    renderApp("/app", off());
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    for (const name of [
      "Kampanyalar",
      "Ürünler",
      "Teklifler",
      "Siparişler",
      "Talepler",
      "SLA politikaları",
      "İş akışları",
      "Onaylarım",
    ]) {
      expect(link(name), name).not.toBeInTheDocument();
    }
    // Core entries and "Plan ve kullanım" stay.
    expect(screen.getAllByRole("link", { name: "Potansiyeller" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Aktiviteler" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Plan ve kullanım" }).length).toBeGreaterThan(0);
  });

  it("makes no request at all for the gated modules, including the top bar approvals badge", async () => {
    renderApp("/app", off());
    await screen.findAllByRole("link", { name: "Potansiyeller" });
    await waitFor(() => expect(requested("/leads") || requested("/deals") || requested("/onboarding")).toBe(true));

    for (const prefix of ["/approvals", "/campaigns", "/quotes", "/orders", "/products", "/cases", "/service", "/workflows", "/reports/marketing", "/reports/commerce", "/reports/service"]) {
      expect(requested(prefix), prefix).toBe(false);
    }
    expect(screen.queryByRole("link", { name: /Bekleyen onaylar/ })).not.toBeInTheDocument();
  });

  it.each([
    ["/app/campaigns", "Pazarlama"],
    ["/app/campaigns/c1", "Pazarlama"],
    ["/app/products", "Ticaret"],
    ["/app/quotes", "Ticaret"],
    ["/app/quotes/new", "Ticaret"],
    ["/app/orders", "Ticaret"],
    ["/app/cases", "Servis"],
    ["/app/cases/x", "Servis"],
    ["/app/settings/sla", "Servis"],
    ["/app/settings/workflows", "İş akışları"],
    ["/app/approvals", "İş akışları"],
  ])("answers %s with the module-disabled page (%s) and requests nothing", async (route, moduleName) => {
    renderApp(route, off());

    const card = await screen.findByTestId("module-disabled");
    expect(within(card).getByRole("heading", { name: "Bu modül planınıza dahil değil" })).toBeInTheDocument();
    expect(card).toHaveTextContent(moduleName);
    expect(within(card).getByRole("link", { name: "Plan ve kullanım" })).toHaveAttribute("href", "/app/settings/plan");
    for (const prefix of ["/campaigns", "/quotes", "/orders", "/products", "/cases", "/service", "/workflows", "/approvals"]) {
      expect(requested(prefix), prefix).toBe(false);
    }
  });

  it("leaves a module that is on reachable while another is off", async () => {
    renderApp(
      "/app/quotes",
      platformMe(ALL_PERMISSIONS, {
        subscription: subscription({ modules: { workflows: false, commerce: true, service: false, marketing: false } }),
      })
    );
    expect(await screen.findByRole("heading", { name: "Teklifler" })).toBeInTheDocument();
    expect(link("Teklifler")).toBeInTheDocument();
    expect(link("Kampanyalar")).not.toBeInTheDocument();
    expect(screen.queryByTestId("module-disabled")).not.toBeInTheDocument();
  });

  it("the Reports page offers no Commerce / Service / Marketing tab and never requests their reports", async () => {
    renderApp("/app/reports?tab=service", off());
    expect(await screen.findByRole("tab", { name: "Satış hunisi" })).toBeInTheDocument();
    for (const name of ["Ticaret", "Servis", "Pazarlama"]) {
      expect(screen.queryByRole("tab", { name })).not.toBeInTheDocument();
    }
    // A stale ?tab=service falls back to the default tab.
    expect(screen.getByRole("tab", { name: "Satış hunisi" })).toHaveAttribute("aria-selected", "true");
    for (const url of requestedUrls()) {
      expect(url.startsWith("/reports/service") || url.startsWith("/reports/commerce") || url.startsWith("/reports/marketing")).toBe(false);
    }
  });

  it("without the subscription field (older server) every module stays available", async () => {
    renderApp("/app/campaigns", platformMe(ALL_PERMISSIONS));
    expect(await screen.findByRole("heading", { name: "Kampanyalar" })).toBeInTheDocument();
    expect(link("Talepler")).toBeInTheDocument();
    expect(screen.queryByTestId("module-disabled")).not.toBeInTheDocument();
  });
});

describe("App - banners, read-only and blocked tenants", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows the trial banner above every page", async () => {
    renderApp(
      "/app/leads",
      platformMe(["crm.leads.read", "org.settings.manage"], {
        subscription: subscription({ status: "trial", trialDaysLeft: 2 }),
      })
    );
    expect(await screen.findByTestId("subscription-banner")).toHaveTextContent("Deneme sürenizin bitmesine 2 gün kaldı.");
    expect(await screen.findByRole("heading", { name: "Potansiyeller" })).toBeInTheDocument();
  });

  it("read-only access hides the create buttons (.write) but keeps reading and the banner", async () => {
    renderApp(
      "/app/leads",
      platformMe(["crm.leads.read", "crm.leads.write"], {
        subscription: subscription({ status: "trial_expired", accessLevel: "readOnly" }),
      })
    );
    expect(await screen.findByRole("heading", { name: "Potansiyeller" })).toBeInTheDocument();
    expect(screen.getByTestId("subscription-banner")).toHaveTextContent("kayıtlar salt okunur");
    expect(screen.queryByRole("button", { name: "Yeni potansiyel" })).not.toBeInTheDocument();
  });

  it("full access shows the create button", async () => {
    renderApp(
      "/app/leads",
      platformMe(["crm.leads.read", "crm.leads.write"], { subscription: subscription() })
    );
    expect(await screen.findByRole("button", { name: "Yeni potansiyel" })).toBeInTheDocument();
  });

  it("a blocked tenant sees only the blocked screen and the app requests nothing but /me", async () => {
    renderApp(
      "/app/leads",
      platformMe(ALL_PERMISSIONS, {
        subscription: subscription({ status: "suspended", accessLevel: "none" }),
      })
    );

    expect(await screen.findByTestId("blocked-screen")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /erişim kapalı/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Organizasyon değiştir" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Potansiyeller" })).not.toBeInTheDocument();
    expect(link("Potansiyeller")).not.toBeInTheDocument();
    await waitFor(() => expect(requested("/me")).toBe(true));
    // /me (the startup profile refresh) is the only request; no page, no badge, no onboarding.
    expect([...new Set(requestedUrls())]).toEqual(["/me"]);
    expect(client.post).not.toHaveBeenCalled();
  });

  it("leaves the blocked screen when /me reports the tenant is reachable again", async () => {
    const me = platformMe(ALL_PERMISSIONS, { subscription: subscription({ status: "suspended", accessLevel: "none" }) });
    renderApp("/app/leads", me);
    await screen.findByTestId("blocked-screen");

    installApi(client, {
      "GET /me": () => platformMe(ALL_PERMISSIONS, { subscription: subscription() }),
      "GET /leads": () => page([]),
      "GET /approvals/summary": () => ({ pendingCount: 0 }),
      "GET /onboarding": () => ({ dismissed: true, completedCount: 0, totalCount: 0, items: [] }),
    });
    await act(async () => {
      await useAuthStore.getState().refreshMe();
    });
    expect(await screen.findByRole("heading", { name: "Potansiyeller" })).toBeInTheDocument();
    expect(screen.queryByTestId("blocked-screen")).not.toBeInTheDocument();
  });
});
