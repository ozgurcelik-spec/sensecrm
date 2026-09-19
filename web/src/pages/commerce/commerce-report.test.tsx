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
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import ReportsPage from "@/pages/crm/reports";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const SUMMARY = {
  currencies: ["TRY"],
  quotes: {
    totalCount: 12,
    totalAmount: 84500.5,
    byStatus: [
      { status: "draft", count: 3, amount: 9000 },
      { status: "sent", count: 4, amount: 30000 },
      { status: "accepted", count: 3, amount: 35000.5 },
      { status: "rejected", count: 1, amount: 5500 },
      { status: "expired", count: 1, amount: 5000 },
    ],
  },
  orders: {
    totalCount: 4,
    totalAmount: 41000,
    byStatus: [
      { status: "draft", count: 1, amount: 6000 },
      { status: "confirmed", count: 2, amount: 20000 },
      { status: "fulfilled", count: 1, amount: 15000 },
      { status: "cancelled", count: 0, amount: 0 },
    ],
  },
  conversionRate: 0.3333,
};

const summaryParams = () =>
  client.get.mock.calls.filter(([url]) => url === "/reports/commerce/summary").at(-1)?.[1].params;

function renderPage(route: string) {
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

describe("Reports - Ticaret tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // 19 Sep 2026 (afternoon: the same calendar day in every zone).
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-09-19T12:00:00Z"));
    setPermissions(["crm.reports.read"]);
    installApi(client, { "GET /reports/commerce/summary": () => SUMMARY });
  });
  afterEach(() => {
    vi.useRealTimers();
    clearSession();
  });

  it("has a Ticaret tab that opens through ?tab=commerce and requests the summary for the range", async () => {
    installApi(client, {
      "GET /reports/commerce/summary": () => SUMMARY,
      "GET /pipelines": () => [],
      "GET /reports/sales/funnel": () => ({ pipelineId: "p", stages: [] }),
    });
    renderPage("/app/reports");
    await userEvent.click(await screen.findByRole("tab", { name: "Ticaret" }));

    await waitFor(() =>
      expect(summaryParams()).toEqual({ from: "2025-10-01", to: "2026-09-19" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("tab=commerce");
  });

  it("turns the range preset into request params", async () => {
    renderPage("/app/reports?tab=commerce");
    await screen.findByTestId("conversion-rate");
    expect(summaryParams()).toEqual({ from: "2025-10-01", to: "2026-09-19" });

    await userEvent.click(screen.getByText("Bu ay"));
    await waitFor(() => expect(summaryParams()).toEqual({ from: "2026-09-01", to: "2026-09-19" }));
  });

  it("shows the status tables with count and amount, and the conversion rate", async () => {
    renderPage("/app/reports?tab=commerce");

    expect(await screen.findByTestId("conversion-rate")).toHaveTextContent("%33,3");
    const tables = screen.getAllByRole("table");
    expect(tables).toHaveLength(2);
    const quoteRows = within(tables[0] as HTMLElement).getAllByRole("row");
    // header + 5 statuses in the server's fixed order.
    expect(quoteRows).toHaveLength(6);
    expect(within(quoteRows[2] as HTMLElement).getByText("Gönderildi")).toBeInTheDocument();
    expect(within(quoteRows[2] as HTMLElement).getByText(/30\.000,00/)).toBeInTheDocument();
    expect(within(quoteRows[5] as HTMLElement).getByText("Süresi doldu")).toBeInTheDocument();
    const orderRows = within(tables[1] as HTMLElement).getAllByRole("row");
    expect(orderRows).toHaveLength(5);
    expect(within(orderRows[4] as HTMLElement).getByText("İptal edildi")).toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("shows a dash instead of a rate when there is no non-draft quote (conversionRate absent)", async () => {
    installApi(client, {
      "GET /reports/commerce/summary": () => {
        const { conversionRate: _omitted, ...rest } = SUMMARY;
        void _omitted;
        return rest;
      },
    });
    renderPage("/app/reports?tab=commerce");
    expect(await screen.findByTestId("conversion-rate")).toHaveTextContent("-");
  });

  it("warns when the range mixes currencies", async () => {
    installApi(client, {
      "GET /reports/commerce/summary": () => ({ ...SUMMARY, currencies: ["EUR", "TRY"] }),
    });
    renderPage("/app/reports?tab=commerce");
    const alert = await screen.findByRole("status");
    expect(alert).toHaveTextContent("karışık para birimleri");
    expect(alert).toHaveTextContent("EUR, TRY");
  });

  it("shows the empty state for a range without documents and the error state with a retry", async () => {
    installApi(client, {
      "GET /reports/commerce/summary": () => ({
        currencies: [],
        quotes: { totalCount: 0, totalAmount: 0, byStatus: SUMMARY.quotes.byStatus.map((r) => ({ ...r, count: 0, amount: 0 })) },
        orders: { totalCount: 0, totalAmount: 0, byStatus: SUMMARY.orders.byStatus.map((r) => ({ ...r, count: 0, amount: 0 })) },
      }),
    });
    const first = renderPage("/app/reports?tab=commerce");
    expect(await screen.findByText("Bu dönem için veri yok")).toBeInTheDocument();
    first.unmount();

    installApi(client, { "GET /reports/commerce/summary": () => problem(500, { code: "unknown" }) });
    renderPage("/app/reports?tab=commerce");
    expect(await screen.findByText("Veriler yüklenemedi")).toBeInTheDocument();
  });
});
