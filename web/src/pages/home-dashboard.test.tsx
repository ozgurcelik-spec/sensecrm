import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
import { activity } from "@/test/activities";
import HomePage from "./home";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const SUMMARY = { openCount: 12, overdueCount: 3, dueTodayCount: 4, completedThisWeek: 9 };

const FUNNEL = {
  pipelineId: "p1",
  stages: [
    { id: "s1", name: "Aday", kind: "open", order: 1, probability: 10, count: 5, totalAmount: 500 },
    {
      id: "s2",
      name: "Teklif",
      kind: "open",
      order: 2,
      probability: 50,
      count: 2,
      totalAmount: 900,
    },
    {
      id: "s3",
      name: "Kazanıldı",
      kind: "won",
      order: 3,
      probability: 100,
      count: 1,
      totalAmount: 300,
    },
    {
      id: "s4",
      name: "Kaybedildi",
      kind: "lost",
      order: 4,
      probability: 0,
      count: 4,
      totalAmount: 100,
    },
  ],
};

const WON_LOST = [
  { period: "2026-08", wonCount: 2, wonAmount: 1000, lostCount: 1, lostAmount: 200 },
  { period: "2026-09", wonCount: 1, wonAmount: 300, lostCount: 0, lostAmount: 0 },
];

const SOURCES = [
  { source: "web", count: 7, convertedCount: 2 },
  { source: "referral", count: 3, convertedCount: 1 },
];

const TASKS = [
  activity("t1", { subject: "Teklifi gönder", dueAt: "2026-05-02T09:00:00Z", isOverdue: true }),
  activity("t2", { subject: "Sözleşmeyi imzala", dueAt: "2099-01-01T09:00:00Z" }),
];

const CALLS = () => client.get.mock.calls.map(([url]) => url as string);

describe("HomePage dashboard widgets", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /leads": () => page([], { totalCount: 0 }),
      "GET /deals/board": () => ({ pipelineId: "p1", stages: [] }),
      "GET /activities/summary": () => SUMMARY,
      "GET /activities": () => page(TASKS),
      "GET /reports/sales/funnel": () => FUNNEL,
      "GET /reports/sales/won-lost": () => WON_LOST,
      "GET /reports/sales/leads-by-source": () => SOURCES,
    });
  });
  afterEach(clearSession);

  it("shows every widget to a user with activities and reports access", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write", "crm.reports.read"]);
    renderWithProviders(<HomePage />);

    const myWork = await screen.findByTestId("widget-my-work");
    expect(await within(myWork).findByText("Teklifi gönder")).toBeInTheDocument();
    expect(within(myWork).getByTestId("my-work-open")).toHaveTextContent("12");
    expect(within(myWork).getByTestId("my-work-overdue")).toHaveTextContent("3");
    expect(within(myWork).getByTestId("my-work-dueToday")).toHaveTextContent("4");
    expect(within(myWork).getByTestId("my-work-completedThisWeek")).toHaveTextContent("9");

    // The task list: the caller's open tasks up to the end of today, oldest first, five at most.
    const taskRequest = client.get.mock.calls.find(([url]) => url === "/activities")?.[1].params;
    expect(taskRequest).toMatchObject({
      type: "task",
      status: "open",
      assignedUserId: "user-1",
      sort: "dueAt",
      page: 1,
      pageSize: 5,
    });
    expect(taskRequest.dueTo).toMatch(/Z$/);

    const funnel = await screen.findByTestId("chart-funnel");
    // Open stages and won, no lost step.
    expect(JSON.parse(funnel.dataset.points ?? "[]").map((c: { name: string }) => c.name)).toEqual([
      "Aday",
      "Teklif",
      "Kazanıldı",
    ]);
    const bars = await screen.findByTestId("chart-bar");
    expect(JSON.parse(bars.dataset.points ?? "[]")).toHaveLength(2);
    const donut = await screen.findByTestId("chart-donut");
    expect(JSON.parse(donut.dataset.points ?? "[]").map((c: { name: string }) => c.name)).toEqual([
      "Web",
      "Tavsiye",
    ]);

    const wonLost = client.get.mock.calls.find(([url]) => url === "/reports/sales/won-lost")?.[1]
      .params;
    expect(wonLost).toMatchObject({ groupBy: "month" });
    expect(wonLost.from).toMatch(/^\d{4}-\d{2}-01$/);
    expect(wonLost.to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it("hides the report widgets and never requests reports without crm.reports.read", async () => {
    setPermissions(["crm.activities.read"]);
    renderWithProviders(<HomePage />);

    expect(await screen.findByTestId("widget-my-work")).toBeInTheDocument();
    await screen.findByText("Teklifi gönder");
    expect(screen.queryByTestId("widget-funnel")).not.toBeInTheDocument();
    expect(screen.queryByTestId("widget-won-lost")).not.toBeInTheDocument();
    expect(screen.queryByTestId("widget-lead-sources")).not.toBeInTheDocument();
    expect(CALLS().some((url) => url.startsWith("/reports"))).toBe(false);
  });

  it("hides 'my work' and never requests activities without crm.activities.read", async () => {
    setPermissions(["crm.reports.read"]);
    renderWithProviders(<HomePage />);

    expect(await screen.findByTestId("widget-funnel")).toBeInTheDocument();
    expect(await screen.findByTestId("widget-won-lost")).toBeInTheDocument();
    expect(await screen.findByTestId("widget-lead-sources")).toBeInTheDocument();
    expect(screen.queryByTestId("widget-my-work")).not.toBeInTheDocument();
    expect(CALLS().some((url) => url.startsWith("/activities"))).toBe(false);
  });

  it("shows no widget and makes no widget request without either permission", async () => {
    setPermissions(["crm.leads.read"]);
    renderWithProviders(<HomePage />);

    await waitFor(() => expect(CALLS()).toContain("/leads"));
    for (const id of ["my-work", "funnel", "won-lost", "lead-sources"]) {
      expect(screen.queryByTestId(`widget-${id}`)).not.toBeInTheDocument();
    }
    expect(CALLS().some((url) => url.startsWith("/reports") || url.startsWith("/activities"))).toBe(
      false
    );
  });

  it("completes a task from the checkbox (optimistically) and disables it without write access", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    let finish: () => void = () => undefined;
    installApi(client, {
      "GET /activities/summary": () => SUMMARY,
      "GET /activities": () => page(TASKS),
      "POST /activities/t1/complete": () => new Promise<void>((resolve) => (finish = resolve)),
    });
    renderWithProviders(<HomePage />);

    const box = await screen.findByRole("checkbox", { name: "Teklifi gönder görevini tamamla" });
    expect(box).not.toBeChecked();
    await userEvent.click(box);

    await waitFor(() =>
      expect(
        screen.getByRole("checkbox", { name: "Teklifi gönder görevini tamamla" })
      ).toBeChecked()
    );
    expect(client.post).toHaveBeenCalledWith("/activities/t1/complete");
    finish();
  });

  it("only lets a read-only user look at the task list", async () => {
    setPermissions(["crm.activities.read"]);
    renderWithProviders(<HomePage />);

    const box = await screen.findByRole("checkbox", { name: "Teklifi gönder görevini tamamla" });
    expect(box).toBeDisabled();
  });

  it("shows empty states", async () => {
    setPermissions(["crm.activities.read", "crm.reports.read"]);
    installApi(client, {
      "GET /activities/summary": () => ({
        openCount: 0,
        overdueCount: 0,
        dueTodayCount: 0,
        completedThisWeek: 0,
      }),
      "GET /activities": () => page([]),
      "GET /reports/sales/funnel": () => ({ pipelineId: "p1", stages: [] }),
      "GET /reports/sales/won-lost": () => [],
      "GET /reports/sales/leads-by-source": () => [],
    });
    renderWithProviders(<HomePage />);

    expect(await screen.findByText("Bugün için bekleyen göreviniz yok")).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByText("Gösterilecek veri yok")).toHaveLength(3));
  });

  it("shows a loading skeleton first and a retryable error per widget", async () => {
    setPermissions(["crm.reports.read"]);
    let calls = 0;
    installApi(client, {
      "GET /reports/sales/funnel": () => {
        calls += 1;
        return calls === 1 ? problem(500, { status: 500, title: "Boom" }) : FUNNEL;
      },
      "GET /reports/sales/won-lost": () => WON_LOST,
      "GET /reports/sales/leads-by-source": () => SOURCES,
    });
    renderWithProviders(<HomePage />);

    expect(await screen.findAllByTestId("widget-skeleton")).not.toHaveLength(0);
    const funnel = await screen.findByTestId("widget-funnel");
    expect(await within(funnel).findByText("Grafik verileri yüklenemedi")).toBeInTheDocument();
    // The other widgets are unaffected.
    expect(await screen.findByTestId("chart-bar")).toBeInTheDocument();

    await userEvent.click(within(funnel).getByRole("button", { name: "Tekrar dene" }));
    expect(await screen.findByTestId("chart-funnel")).toBeInTheDocument();
  });
});
