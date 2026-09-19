import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { testI18n } from "@/test-utils";
import { MEMBERS, installApi, meWith, page, type MockClient } from "@/test/crm";
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
    "GET /cases": () => page([]),
    "GET /cases/summary": () => ({
      openCount: 12,
      overdueCount: 3,
      mineCount: 4,
      unassignedCount: 5,
    }),
    "GET /service/sla-policies": () => [
      { priority: "low", firstResponseMinutes: 1440, resolutionMinutes: 10080 },
      { priority: "normal", firstResponseMinutes: 480, resolutionMinutes: 4320 },
      { priority: "high", firstResponseMinutes: 240, resolutionMinutes: 1440 },
      { priority: "urgent", firstResponseMinutes: 60, resolutionMinutes: 240 },
    ],
    "GET /organization/members": () => MEMBERS,
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
const requested = (url: string) => client.get.mock.calls.some(([u]) => u === url);

describe("App - service navigation, routes and home card", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows the Talepler menu entry and page with crm.cases.read", async () => {
    renderApp("/app/cases", ["crm.cases.read"]);

    // The first lazy import of the cases page can be slow on a cold module graph.
    expect(
      await screen.findByRole("heading", { name: "Talepler" }, { timeout: 8000 })
    ).toBeInTheDocument();
    expect(navLink("Talepler")).toHaveAttribute("href", "/app/cases");
  }, 15_000);

  it("hides the menu entry and answers deep links with the no-access page without crm.cases.read", async () => {
    renderApp("/app/cases", ["crm.leads.read"]);

    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(navLink("Talepler")).not.toBeInTheDocument();
    expect(requested("/cases")).toBe(false);
  });

  it("guards the case detail route too", async () => {
    renderApp("/app/cases/abc", ["crm.cases.write"]);

    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(requested("/cases/abc")).toBe(false);
  });

  it("shows Settings > SLA politikaları only with org.settings.manage", async () => {
    const first = renderApp("/app/settings/sla", ["org.settings.manage"]);
    expect(await screen.findByRole("heading", { name: "SLA politikaları" })).toBeInTheDocument();
    expect(navLink("SLA politikaları")).toHaveAttribute("href", "/app/settings/sla");
    first.unmount();

    vi.clearAllMocks();
    renderApp("/app/settings/sla", ["crm.cases.read", "crm.cases.write"]);
    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(navLink("SLA politikaları")).not.toBeInTheDocument();
    expect(requested("/service/sla-policies")).toBe(false);
  });

  it("shows the home Servis card with counters linking to the filtered list", async () => {
    renderApp("/app", ["crm.cases.read"]);

    const card = await screen.findByTestId("widget-cases");
    await waitFor(() => expect(within(card).getByTestId("cases-open")).toHaveTextContent("12"));
    expect(within(card).getByTestId("cases-overdue")).toHaveTextContent("3");
    expect(within(card).getByTestId("cases-mine")).toHaveTextContent("4");
    expect(within(card).getByTestId("cases-unassigned")).toHaveTextContent("5");

    expect(within(card).getByTestId("cases-link-open")).toHaveAttribute(
      "href",
      "/app/cases?status=new,open,pending"
    );
    expect(within(card).getByTestId("cases-link-overdue")).toHaveAttribute(
      "href",
      "/app/cases?status=new,open,pending&slaState=breached"
    );
    expect(within(card).getByTestId("cases-link-mine")).toHaveAttribute(
      "href",
      "/app/cases?status=new,open,pending&assignedUserId=user-1"
    );
    expect(within(card).getByTestId("cases-link-unassigned")).toHaveAttribute(
      "href",
      "/app/cases?status=new,open,pending&unassigned=true"
    );
  });

  it("has no Servis card and requests no summary without crm.cases.read", async () => {
    renderApp("/app", ["crm.leads.read"]);
    await screen.findAllByRole("link", { name: "Potansiyeller" });

    expect(screen.queryByTestId("widget-cases")).not.toBeInTheDocument();
    expect(requested("/cases/summary")).toBe(false);
  });
});
