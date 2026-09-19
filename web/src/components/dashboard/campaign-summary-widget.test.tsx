import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { MARKETING_SUMMARY } from "@/test/campaigns";
import HomePage from "@/pages/home";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const CALLS = () => client.get.mock.calls.map(([url]) => url as string);

describe("Dashboard - campaign summary card", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /leads": () => page([], { totalCount: 0 }),
      "GET /deals/board": () => ({ pipelineId: "p1", stages: [] }),
      "GET /reports/sales/funnel": () => ({ pipelineId: "p1", stages: [] }),
      "GET /reports/sales/won-lost": () => [],
      "GET /reports/sales/leads-by-source": () => [],
      "GET /reports/marketing/summary": () => MARKETING_SUMMARY,
    });
  });
  afterEach(clearSession);

  it("shows the counts and links to the campaigns with reports and campaigns access", async () => {
    setPermissions(["crm.reports.read", "crm.campaigns.read"]);
    renderWithProviders(<HomePage />);

    const card = await screen.findByTestId("widget-campaigns");
    expect(await within(card).findByTestId("campaign-stat-count")).toHaveTextContent("12");
    expect(within(card).getByTestId("campaign-stat-active")).toHaveTextContent("5");
    expect(within(card).getByTestId("campaign-stat-response")).toHaveTextContent("28%");
    expect(within(card).getByTestId("campaign-stat-converted")).toHaveTextContent("96");
    expect(within(card).getByRole("link", { name: "Kampanyalar" })).toHaveAttribute(
      "href",
      "/app/campaigns"
    );
    // The default range: no from/to.
    const call = client.get.mock.calls.find(([url]) => url === "/reports/marketing/summary");
    expect(call?.[1].params).toEqual({});
  });

  it("is hidden without crm.campaigns.read and makes no marketing request", async () => {
    setPermissions(["crm.reports.read"]);
    renderWithProviders(<HomePage />);

    await screen.findByTestId("widget-funnel");
    expect(screen.queryByTestId("widget-campaigns")).not.toBeInTheDocument();
    expect(CALLS()).not.toContain("/reports/marketing/summary");
  });

  it("is hidden without crm.reports.read and makes no marketing request", async () => {
    setPermissions(["crm.campaigns.read", "crm.leads.read"]);
    renderWithProviders(<HomePage />);

    await screen.findByRole("link", { name: /Potansiyeller/ });
    expect(screen.queryByTestId("widget-campaigns")).not.toBeInTheDocument();
    expect(CALLS()).not.toContain("/reports/marketing/summary");
  });

  it("shows the empty state when there are no campaigns", async () => {
    installApi(client, {
      "GET /reports/marketing/summary": () => ({ ...MARKETING_SUMMARY, campaignCount: 0 }),
      "GET /reports/sales/funnel": () => ({ pipelineId: "p1", stages: [] }),
      "GET /reports/sales/won-lost": () => [],
      "GET /reports/sales/leads-by-source": () => [],
    });
    setPermissions(["crm.reports.read", "crm.campaigns.read"]);
    renderWithProviders(<HomePage />);

    expect(await screen.findByText("Bu dönemde kampanya yok")).toBeInTheDocument();
  });

  it("shows an error with a retry button when the summary fails", async () => {
    installApi(client, {
      "GET /reports/marketing/summary": () => problem(500, { code: "unknown" }),
      "GET /reports/sales/funnel": () => ({ pipelineId: "p1", stages: [] }),
      "GET /reports/sales/won-lost": () => [],
      "GET /reports/sales/leads-by-source": () => [],
    });
    setPermissions(["crm.reports.read", "crm.campaigns.read"]);
    renderWithProviders(<HomePage />);

    const card = await screen.findByTestId("widget-campaigns");
    expect(await within(card).findByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });
});
