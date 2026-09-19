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

const SUMMARY = {
  from: "2025-10-01",
  to: "2026-09-19",
  totalCount: 120,
  resolvedCount: 90,
  byStatus: [
    { status: "new", count: 4 },
    { status: "open", count: 20 },
    { status: "pending", count: 6 },
    { status: "resolved", count: 40 },
    { status: "closed", count: 50 },
  ],
  byPriority: [
    { priority: "low", count: 30 },
    { priority: "normal", count: 60 },
    { priority: "high", count: 22 },
    { priority: "urgent", count: 8 },
  ],
  avgFirstResponseMinutes: 143.5,
  avgResolutionMinutes: 1210,
  slaBreachedCount: 18,
  slaBreachRate: 0.15,
};

const BY_ASSIGNEE = [
  {
    assignedUserId: "user-1",
    assignedUserName: "Ada Lovelace",
    totalCount: 70,
    openCount: 10,
    resolvedCount: 55,
    avgFirstResponseMinutes: 120,
    avgResolutionMinutes: 1000,
    slaBreachedCount: 9,
  },
  { totalCount: 50, openCount: 16, resolvedCount: 35, slaBreachedCount: 9 },
];

const paramsOf = (path: string) =>
  client.get.mock.calls.filter(([url]) => url === path).at(-1)?.[1].params;

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

describe("Reports: Servis tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-09-19T12:00:00Z"));
    setPermissions(["crm.reports.read"]);
    installApi(client, {
      "GET /reports/service/summary": () => SUMMARY,
      "GET /reports/service/by-assignee": () => BY_ASSIGNEE,
    });
  });
  afterEach(() => {
    vi.useRealTimers();
    clearSession();
  });

  it("is a tab of the reports page and keeps `tab=service` in the URL", async () => {
    installApi(client, {
      "GET /reports/sales/funnel": () => ({ pipelineId: "p1", stages: [] }),
      "GET /reports/service/summary": () => SUMMARY,
      "GET /reports/service/by-assignee": () => BY_ASSIGNEE,
    });
    renderPage("/app/reports");
    await userEvent.click(await screen.findByRole("tab", { name: "Servis" }));

    expect(await screen.findByTestId("kpi-total")).toBeInTheDocument();
    expect(screen.getByTestId("location")).toHaveTextContent("tab=service");
  });

  it("sends the date range and shows the KPI cards with formatted durations", async () => {
    renderPage("/app/reports?tab=service&range=thisMonth");
    await screen.findByTestId("kpi-total");

    const range = { from: "2026-09-01", to: "2026-09-19" };
    expect(paramsOf("/reports/service/summary")).toEqual(range);
    expect(paramsOf("/reports/service/by-assignee")).toEqual(range);

    expect(screen.getByTestId("kpi-total")).toHaveTextContent("120");
    expect(screen.getByTestId("kpi-resolved")).toHaveTextContent("90");
    expect(screen.getByTestId("kpi-first-response")).toHaveTextContent("2 sa 24 dk");
    expect(screen.getByTestId("kpi-resolution")).toHaveTextContent("20 sa 10 dk");
    expect(screen.getByTestId("kpi-sla")).toHaveTextContent("18 (%15)");
  });

  it("re-requests with the new range when the preset changes", async () => {
    renderPage("/app/reports?tab=service");
    await screen.findByTestId("kpi-total");
    expect(paramsOf("/reports/service/summary")).toEqual({ from: "2025-10-01", to: "2026-09-19" });

    await userEvent.click(screen.getByText("Son 3 ay"));
    await waitFor(() => expect(paramsOf("/reports/service/summary")?.from).toBe("2026-07-01"));
    expect(paramsOf("/reports/service/by-assignee")?.from).toBe("2026-07-01");
  });

  it("charts every status and priority in order and lists assignees, unassigned included", async () => {
    renderPage("/app/reports?tab=service");
    await screen.findByTestId("kpi-total");

    const donut = JSON.parse(screen.getByTestId("chart-donut").getAttribute("data-points") ?? "[]");
    expect(donut.map((p: { name: string; value: number }) => [p.name, p.value])).toEqual([
      ["Yeni", 4],
      ["Açık", 20],
      ["Beklemede", 6],
      ["Çözüldü", 40],
      ["Kapalı", 50],
    ]);
    const bars = JSON.parse(screen.getByTestId("chart-bar").getAttribute("data-points") ?? "[]");
    expect(bars.map((p: { label: string; count: number }) => [p.label, p.count])).toEqual([
      ["Düşük", 30],
      ["Normal", 60],
      ["Yüksek", 22],
      ["Acil", 8],
    ]);

    const rows = screen.getAllByRole("row");
    const ada = rows.find((r) => within(r).queryByText("Ada Lovelace")) as HTMLElement;
    expect(within(ada).getByText("70")).toBeInTheDocument();
    expect(within(ada).getByText("2 sa")).toBeInTheDocument();
    const unassigned = rows.find((r) => within(r).queryByText("Atanmamış")) as HTMLElement;
    // No sample -> no average: shown as a dash.
    expect(within(unassigned).getAllByText("-")).toHaveLength(2);
  });

  it("shows an empty state (and a disabled CSV button) when the range has no cases", async () => {
    installApi(client, {
      "GET /reports/service/summary": () => ({
        ...SUMMARY,
        totalCount: 0,
        resolvedCount: 0,
        slaBreachedCount: 0,
        slaBreachRate: 0,
        avgFirstResponseMinutes: undefined,
        avgResolutionMinutes: undefined,
      }),
      "GET /reports/service/by-assignee": () => [],
    });
    renderPage("/app/reports?tab=service");

    expect(await screen.findByText("Bu dönem için veri yok")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "CSV indir" })).toBeDisabled();
  });

  describe("CSV", () => {
    let blobs: Blob[];
    let names: string[];

    beforeEach(() => {
      blobs = [];
      names = [];
      Object.assign(URL, {
        createObjectURL: vi.fn((blob: Blob) => {
          blobs.push(blob);
          return "blob:report";
        }),
        revokeObjectURL: vi.fn(),
      });
      vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
        this: HTMLAnchorElement
      ) {
        names.push(this.download);
      });
    });
    afterEach(() => vi.restoreAllMocks());

    it("downloads the per-assignee table in the browser with the range in the file name", async () => {
      renderPage("/app/reports?tab=service&range=thisMonth");
      await screen.findByTestId("kpi-total");

      await userEvent.click(screen.getByRole("button", { name: "CSV indir" }));

      expect(names).toEqual(["servis_2026-09-01_2026-09-19.csv"]);
      const content = new TextDecoder().decode(await blobs[0]?.arrayBuffer());
      const lines = content.replace("﻿", "").split("\r\n");
      expect(lines[0]).toBe(
        "Temsilci;Toplam;Açık;Çözülen;Ort. ilk yanıt;Ort. çözüm;SLA ihlali"
      );
      expect(lines[1]).toBe("Ada Lovelace;70;10;55;120;1000;9");
      expect(lines[2]).toBe("Atanmamış;50;16;35;;;9");
    });
  });
});
