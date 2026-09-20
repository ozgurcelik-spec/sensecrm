import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { subscriptionInfo } from "@/test/platform";
import PlanUsagePage from "./plan-usage";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("PlanUsagePage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.settings.manage"]);
  });
  afterEach(clearSession);

  it("shows plan, status badge, trial end and the days left", async () => {
    installApi(client, { "GET /subscription": () => subscriptionInfo() });
    renderWithProviders(<PlanUsagePage />);

    const card = await screen.findByTestId("plan-card");
    expect(within(card).getByText("Starter")).toBeInTheDocument();
    expect(within(card).getByTestId("status-badge")).toHaveTextContent("Deneme");
    expect(card).toHaveTextContent("Deneme bitişi: 4 Eki 2026");
    expect(card).toHaveTextContent("14 gün kaldı");
  });

  it("draws limit bars: users count active plus pending, orange from 80 %, red at 100 %", async () => {
    installApi(client, {
      "GET /subscription": () =>
        subscriptionInfo({
          limits: { maxUsers: 5, maxRecords: { sales: 5000, activities: 200 } },
          usage: {
            asOf: "2026-09-20T09:00:00Z",
            users: 3,
            pendingUsers: 1,
            records: { sales: 340, activities: 200 },
          },
        }),
    });
    renderWithProviders(<PlanUsagePage />);

    const users = await screen.findByTestId("usage-users");
    expect(users).toHaveTextContent("Kullanıcı");
    expect(users).toHaveTextContent("4 / 5 (80%)");
    expect(users).toHaveTextContent("3 aktif + 1 bekleyen davet");
    expect(users).toHaveAttribute("data-level", "warn");
    expect(within(users).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "80");

    const sales = screen.getByTestId("usage-sales");
    expect(sales).toHaveTextContent("Satış kayıtları");
    expect(sales).toHaveTextContent("340 / 5.000 (7%)");
    expect(sales).toHaveAttribute("data-level", "ok");

    const activities = screen.getByTestId("usage-activities");
    expect(activities).toHaveTextContent("200 / 200 (100%)");
    expect(activities).toHaveAttribute("data-level", "full");
  });

  it("says 'Sınırsız' where there is no limit (no bar) and hides record bars of modules that are off", async () => {
    installApi(client, {
      "GET /subscription": () =>
        subscriptionInfo({
          status: "active",
          trialEndsOn: undefined,
          trialDaysLeft: undefined,
          modules: { workflows: false, commerce: true, service: true, marketing: false },
          limits: { maxRecords: {} },
          usage: {
            asOf: "2026-09-20T09:00:00Z",
            users: 12,
            pendingUsers: 0,
            records: { sales: 340, commerce: 25, workflows: 3 },
          },
        }),
    });
    renderWithProviders(<PlanUsagePage />);

    const users = await screen.findByTestId("usage-users");
    expect(users).toHaveTextContent("12 · Sınırsız");
    expect(within(users).queryByRole("progressbar")).not.toBeInTheDocument();
    expect(screen.getByTestId("usage-sales")).toHaveTextContent("340 · Sınırsız");
    expect(screen.getByTestId("usage-commerce")).toHaveTextContent("25 · Sınırsız");
    // The workflows module is off: its count is not shown even though the server sent one.
    expect(screen.queryByTestId("usage-workflows")).not.toBeInTheDocument();
    expect(screen.queryByText(/Deneme bitişi/)).not.toBeInTheDocument();
  });

  it("lists which modules the plan includes, and when the usage was counted", async () => {
    installApi(client, { "GET /subscription": () => subscriptionInfo({ modules: { workflows: true, commerce: false, service: false, marketing: false } }) });
    renderWithProviders(<PlanUsagePage />);

    expect(await screen.findByTestId("module-workflows")).toHaveTextContent("İş akışları: Dahil");
    expect(screen.getByTestId("module-marketing")).toHaveTextContent("Pazarlama: Plana dahil değil");
    expect(screen.getByTestId("as-of")).toHaveTextContent("Şu tarihte sayıldı:");
    expect(screen.getByText(/platform yöneticisiyle iletişime geçin/)).toBeInTheDocument();
  });

  it("warns about usage above the limit (over-limit report)", async () => {
    installApi(client, {
      "GET /subscription": () =>
        subscriptionInfo({
          overLimit: [
            { limit: "users", max: 5, used: 8 },
            { limit: "records", module: "sales", max: 5000, used: 6200 },
          ],
        }),
    });
    renderWithProviders(<PlanUsagePage />);
    const alert = await screen.findByTestId("over-limit");
    expect(alert).toHaveTextContent("Plan limiti aşıldı");
    expect(alert).toHaveTextContent("Kullanıcı: 8 / 5");
    expect(alert).toHaveTextContent("Satış kayıtları: 6200 / 5000");
  });

  it("shows a retry after a load error", async () => {
    installApi(client, { "GET /subscription": () => problem(500, { title: "Sunucu hatası" }) });
    renderWithProviders(<PlanUsagePage />);
    expect(await screen.findByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });
});
