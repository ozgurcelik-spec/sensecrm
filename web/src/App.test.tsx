import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { mantineTheme } from "@/lib/mantine-theme";
import { useAuthStore } from "@/store/auth.store";
import { testI18n } from "@/test-utils";
import { installApi, meWith, type MockClient } from "@/test/crm";
import App from "./App";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function renderApp(route: string, permissions: string[], pendingCount = 0) {
  const me = meWith(permissions);
  installApi(client, {
    "GET /me": () => me,
    "GET /approvals/summary": () => ({ pendingCount }),
    "GET /approvals": () => ({ items: [], page: 1, pageSize: 25, totalCount: 0 }),
    "GET /workflows/rules": () => [],
    "GET /workflows/executions": () => ({ items: [], page: 1, pageSize: 25, totalCount: 0 }),
    "GET /organization/roles": () => [],
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

describe("App - workflow navigation and route permissions", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => {
    cleanup();
    act(() => useAuthStore.setState({ token: null, refreshToken: null, me: null }));
    window.history.pushState({}, "", "/");
  });

  it("shows the workflows settings entry and page with org.workflows.manage", async () => {
    renderApp("/app/settings/workflows", ["org.workflows.manage"]);

    expect(await screen.findByRole("heading", { name: "İş akışları" })).toBeInTheDocument();
    expect(navLink("İş akışları")).toHaveAttribute("href", "/app/settings/workflows");
    expect(screen.getByRole("tab", { name: "Kurallar" })).toBeInTheDocument();
  });

  it("hides the workflows entry and answers a deep link with the no-access page without the permission", async () => {
    renderApp("/app/settings/workflows", ["crm.deals.read"]);

    expect(await screen.findByRole("heading", { name: "Erişim izniniz yok" })).toBeInTheDocument();
    expect(navLink("İş akışları")).not.toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: "Kurallar" })).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalledWith("/workflows/rules", expect.anything());
  });

  it("shows the approvals entry to approvers even without pending approvals", async () => {
    renderApp("/app", ["crm.approvals.decide"], 0);
    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/approvals/summary"));
    expect(await screen.findByRole("link", { name: "Onaylarım" })).toHaveAttribute(
      "href",
      "/app/approvals"
    );
  });

  it("shows the approvals entry and the top bar badge to a user with pending approvals but no permission", async () => {
    renderApp("/app", ["crm.leads.read"], 2);

    expect(await screen.findByRole("link", { name: "Onaylarım" })).toHaveAttribute(
      "href",
      "/app/approvals"
    );
    expect(await screen.findByRole("link", { name: "Bekleyen onaylar: 2" })).toBeInTheDocument();
  });

  it("hides the approvals entry and the badge without the permission and without pending approvals", async () => {
    renderApp("/app", ["crm.leads.read"], 0);

    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/approvals/summary"));
    await screen.findByRole("link", { name: "Potansiyeller" });
    expect(navLink("Onaylarım")).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /^Bekleyen onaylar/ })).not.toBeInTheDocument();
  });

  it("opens the approvals page for any signed-in user (mine=true needs no permission)", async () => {
    renderApp("/app/approvals", []);
    expect(await screen.findByRole("heading", { name: "Onaylarım" })).toBeInTheDocument();
    expect(await screen.findByText("Bekleyen onayınız yok")).toBeInTheDocument();
  });

  it("opens the settings area from the header gear and swaps the left rail to the settings pages", async () => {
    renderApp("/app", ["org.workflows.manage", "crm.deals.read"]);

    const modules = await screen.findByRole("navigation", { name: "Modüller" });
    expect(within(modules).getByRole("link", { name: "Fırsatlar" })).toBeInTheDocument();
    expect(within(modules).queryByRole("link", { name: "İş akışları" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("link", { name: "Ayarlar" }));

    const settings = await screen.findByRole("navigation", { name: "Ayarlar" });
    expect(within(settings).getByRole("link", { name: "İş akışları" })).toHaveAttribute(
      "href",
      "/app/settings/workflows"
    );
    expect(screen.queryByRole("navigation", { name: "Modüller" })).not.toBeInTheDocument();
    expect(navLink("Uygulamaya dön")).toHaveAttribute("href", "/app");
  });
});
