import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  clearSession,
  installApi,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { MARKETING_SUMMARY } from "@/test/campaigns";
import ReportsPage from "./reports";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const SUMMARY_URL = "/reports/marketing/summary";
const summaryCalls = () => client.get.mock.calls.filter(([url]) => url === SUMMARY_URL);
const lastParams = () => summaryCalls().at(-1)?.[1].params;

function renderPage(route = "/app/reports?tab=marketing") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/reports" element={<ReportsPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("ReportsPage - Marketing tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // 19 Sep 2026 (afternoon: the same calendar day in every zone).
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-09-19T12:00:00Z"));
    setPermissions(["crm.reports.read"]);
    installApi(client, {
      "GET /pipelines": () => [],
      "GET /reports/sales/funnel": () => ({
        pipelineId: "p1",
        stages: [
          { id: "s1", name: "Aday", kind: "open", order: 1, probability: 10, count: 5, totalAmount: 500 },
        ],
      }),
      [`GET ${SUMMARY_URL}`]: () => MARKETING_SUMMARY,
    });
  });
  afterEach(() => {
    vi.useRealTimers();
    clearSession();
  });

  it("shows the total cards computed by the server and the budget - cost difference", async () => {
    renderPage();

    expect(await screen.findByTestId("total-campaigns")).toHaveTextContent("12");
    expect(screen.getByTestId("total-response-rate")).toHaveTextContent("28%");
    expect(screen.getByTestId("total-conversion-rate")).toHaveTextContent("5,33%");
    expect(screen.getByTestId("total-cost-per-lead")).toHaveTextContent(/97,22/);
    expect(screen.getByTestId("total-budget")).toHaveTextContent(/250\.000/);
    expect(screen.getByTestId("total-actual-cost")).toHaveTextContent(/175\.000/);
    // Budget - cost is derived on the client.
    expect(screen.getByTestId("total-budget-gap")).toHaveTextContent(/75\.000/);
    expect(screen.getByTestId("total-expected-revenue")).toHaveTextContent(/900\.000/);
  });

  it("shows a dash for the cost per lead when the server omits it", async () => {
    installApi(client, {
      [`GET ${SUMMARY_URL}`]: () => ({
        ...MARKETING_SUMMARY,
        totals: { ...MARKETING_SUMMARY.totals, costPerLead: undefined },
      }),
    });
    renderPage();

    expect(await screen.findByTestId("total-cost-per-lead")).toHaveTextContent("—");
  });

  it("charts the status and type distribution and the budget against the cost per type", async () => {
    renderPage();
    await screen.findByTestId("total-campaigns");

    const donuts = screen.getAllByTestId("chart-donut");
    expect(donuts).toHaveLength(2);
    const [status, type] = donuts.map(
      (d) => JSON.parse(d.dataset.points ?? "[]") as { name: string; value: number }[]
    );
    expect(status?.map((c) => [c.name, c.value])).toEqual([
      ["Planlandı", 2],
      ["Aktif", 5],
      ["Tamamlandı", 4],
      ["İptal edildi", 1],
    ]);
    expect(type?.map((c) => c.name)).toEqual([
      "E-posta",
      "Etkinlik",
      "Web semineri",
      "Reklam",
      "Diğer",
    ]);
    const bars = JSON.parse(screen.getByTestId("chart-bar").dataset.points ?? "[]");
    expect(bars[0]).toMatchObject({ label: "E-posta", budget: 80000, actualCost: 61000 });
  });

  it("lists the top campaigns with a link to each", async () => {
    renderPage();

    const link = await screen.findByRole("link", { name: "Sonbahar E-posta" });
    expect(link).toHaveAttribute("href", "/app/campaigns/c1");
    const row = within(link.closest("tr") as HTMLElement);
    expect(row.getByText("41,5%")).toBeInTheDocument();
    expect(row.getByText("22")).toBeInTheDocument();
  });

  it("turns the date range into from/to request params", async () => {
    renderPage();
    await screen.findByTestId("total-campaigns");
    // Default: last 12 months.
    expect(lastParams()).toEqual({ from: "2025-10-01", to: "2026-09-19" });

    await userEvent.click(screen.getByText("Bu ay"));
    await waitFor(() => expect(lastParams()).toEqual({ from: "2026-09-01", to: "2026-09-19" }));
    expect(screen.getByTestId("location")).toHaveTextContent("tab=marketing");
    expect(screen.getByTestId("location")).toHaveTextContent("range=thisMonth");
  });

  it("is reachable from the tab list and only the active tab is requested", async () => {
    renderPage("/app/reports");
    await screen.findByTestId("chart-funnel");
    expect(summaryCalls()).toHaveLength(0);

    await userEvent.click(screen.getByRole("tab", { name: "Pazarlama" }));
    await screen.findByTestId("total-campaigns");
    expect(summaryCalls()).toHaveLength(1);
    expect(screen.getByTestId("location")).toHaveTextContent("tab=marketing");
  });

  it("shows the empty state for a period without campaigns", async () => {
    installApi(client, {
      [`GET ${SUMMARY_URL}`]: () => ({ ...MARKETING_SUMMARY, campaignCount: 0, topCampaigns: [] }),
    });
    renderPage();

    expect(await screen.findByText("Bu dönem için veri yok")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "CSV indir" })).toBeDisabled();
  });

  it("hides the Marketing tab and never requests it without crm.reports.read", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage("/app/reports?tab=marketing");

    // The unknown tab falls back to the default one.
    await screen.findByTestId("chart-funnel");
    expect(screen.queryByRole("tab", { name: "Pazarlama" })).not.toBeInTheDocument();
    expect(summaryCalls()).toHaveLength(0);
  });
});
