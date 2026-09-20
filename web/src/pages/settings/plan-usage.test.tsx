import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { FILES_USAGE } from "@/test/files";
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

describe("PlanUsagePage storage (M8C)", () => {
  const withStorage = (maxStorageMb: number | undefined, storageBytes: number, fileCount = 214) =>
    subscriptionInfo({
      limits: { maxUsers: 5, maxStorageMb, maxRecords: {} },
      usage: {
        asOf: "2026-09-20T09:00:00Z",
        users: 3,
        pendingUsers: 0,
        records: {},
        storageBytes,
        fileCount,
      },
    });

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.settings.manage"]);
  });
  afterEach(clearSession);

  it("draws the storage bar in readable sizes, with the file count", async () => {
    installApi(client, {
      "GET /subscription": () => withStorage(1024, 300 * 1024 * 1024),
      "GET /files/usage": () => FILES_USAGE,
    });
    renderWithProviders(<PlanUsagePage />);

    const bar = await screen.findByTestId("usage-storage");
    expect(bar).toHaveTextContent("Depolama");
    expect(bar).toHaveTextContent("300 MB / 1 GB (29%)");
    expect(bar).toHaveTextContent("214 dosya");
    expect(bar).toHaveAttribute("data-level", "ok");
    expect(within(bar).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "29");
  });

  it.each([
    [80, "warn"],
    [79, "ok"],
    [100, "full"],
    [120, "full"],
  ])("colors the storage bar by level: %s%% of a 100 MB limit is %s", async (usedMb, level) => {
    installApi(client, {
      "GET /subscription": () => withStorage(100, usedMb * 1024 * 1024),
      "GET /files/usage": () => FILES_USAGE,
    });
    renderWithProviders(<PlanUsagePage />);
    expect(await screen.findByTestId("usage-storage")).toHaveAttribute("data-level", level);
  });

  it("without a storage limit only the used amount is shown ('Sınırsız'), no bar", async () => {
    installApi(client, {
      "GET /subscription": () => withStorage(undefined, 3 * 1024 * 1024 * 1024),
      "GET /files/usage": () => FILES_USAGE,
    });
    renderWithProviders(<PlanUsagePage />);
    const bar = await screen.findByTestId("usage-storage");
    expect(bar).toHaveTextContent("3 GB · Sınırsız");
    expect(bar).toHaveAttribute("data-level", "unlimited");
    expect(within(bar).queryByRole("progressbar")).not.toBeInTheDocument();
  });

  it("a zero limit (uploads switched off) reads as full", async () => {
    installApi(client, {
      "GET /subscription": () => withStorage(0, 0, 0),
      "GET /files/usage": () => FILES_USAGE,
    });
    renderWithProviders(<PlanUsagePage />);
    const bar = await screen.findByTestId("usage-storage");
    expect(bar).toHaveTextContent("0 B / 0 B");
    expect(bar).toHaveAttribute("data-level", "full");
  });

  it("shows no storage bar on a server that does not report storage", async () => {
    installApi(client, { "GET /subscription": () => subscriptionInfo(), "GET /files/usage": () => FILES_USAGE });
    renderWithProviders(<PlanUsagePage />);
    await screen.findByTestId("usage-users");
    expect(screen.queryByTestId("usage-storage")).not.toBeInTheDocument();
  });

  it("lists the usage per record type with totals and the quarantined / unavailable counts", async () => {
    installApi(client, {
      "GET /subscription": () => withStorage(25600, 3221225472),
      "GET /files/usage": () => FILES_USAGE,
    });
    renderWithProviders(<PlanUsagePage />);

    const card = await screen.findByTestId("storage-breakdown");
    const account = await within(card).findByTestId("storage-row-account");
    expect(account).toHaveTextContent("Firma");
    expect(account).toHaveTextContent("80");
    expect(account).toHaveTextContent("1,1 GB");
    expect(within(card).getByTestId("storage-row-quote")).toHaveTextContent("Teklif");
    expect(card).toHaveTextContent("Toplam");
    expect(card).toHaveTextContent("214");
    expect(card).toHaveTextContent("3 GB");
    expect(card).toHaveTextContent("1 karantinada");
    expect(card).toHaveTextContent("2 kullanılamıyor");
  });

  it("does not ask for the per-type usage without org.settings.manage", async () => {
    setPermissions([]);
    installApi(client, { "GET /subscription": () => withStorage(1024, 1024) });
    renderWithProviders(<PlanUsagePage />);
    await screen.findByTestId("usage-storage");
    expect(screen.queryByTestId("storage-breakdown")).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalledWith("/files/usage", expect.anything());
  });

  it("says so when nothing was uploaded yet", async () => {
    installApi(client, {
      "GET /subscription": () => withStorage(1024, 0, 0),
      "GET /files/usage": () => ({ ...FILES_USAGE, byRecordType: [], usedBytes: 0, fileCount: 0 }),
    });
    renderWithProviders(<PlanUsagePage />);
    expect(await screen.findByText("Henüz dosya yüklenmemiş.")).toBeInTheDocument();
  });

  it("words a storage over-limit entry in bytes", async () => {
    installApi(client, {
      "GET /subscription": () => ({
        ...withStorage(1024, 2 * 1024 * 1024 * 1024),
        overLimit: [{ limit: "storage", module: "files", max: 1024 * 1024 * 1024, used: 2 * 1024 * 1024 * 1024 }],
      }),
      "GET /files/usage": () => FILES_USAGE,
    });
    renderWithProviders(<PlanUsagePage />);
    expect(await screen.findByTestId("over-limit")).toHaveTextContent("Depolama: 2 GB / 1 GB");
  });
});
