import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { FILES_USAGE } from "@/test/files";
import { subscriptionInfo } from "@/test/platform";
import { getApiErrorMessage } from "@/lib/api-error";
import PlanUsagePage from "./plan-usage";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("Plan ve kullanım - integrations (M8B)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.settings.manage"]);
  });
  afterEach(clearSession);

  function serve(info: ReturnType<typeof subscriptionInfo>) {
    installApi(client, { "GET /subscription": () => info, "GET /files/usage": () => FILES_USAGE });
  }

  it("draws the webhook and API key bars against their limits (orange from 80 %, red at 100 %)", async () => {
    serve(
      subscriptionInfo({
        modules: { workflows: true, commerce: true, service: true, marketing: true, integrations: true },
        limits: { maxUsers: 5, maxWebhooks: 5, maxApiKeys: 3, maxRecords: {} },
        usage: { asOf: "2026-09-20T09:00:00Z", users: 1, pendingUsers: 0, records: {}, webhooks: 4, apiKeys: 3 },
      })
    );
    renderWithProviders(<PlanUsagePage />);
    const webhooks = await screen.findByTestId("usage-webhooks");
    expect(webhooks).toHaveTextContent("Webhook");
    expect(webhooks).toHaveTextContent("4 / 5 (80%)");
    expect(webhooks).toHaveAttribute("data-level", "warn");
    const keys = screen.getByTestId("usage-apiKeys");
    expect(keys).toHaveTextContent("API anahtarı");
    expect(keys).toHaveTextContent("3 / 3 (100%)");
    expect(keys).toHaveAttribute("data-level", "full");
    expect(keys).toHaveTextContent("Yalnızca etkin");
  });

  it("says 'Sınırsız' without a limit and draws nothing when the plan lacks the module or the server reports no counts", async () => {
    serve(
      subscriptionInfo({
        modules: { integrations: true },
        limits: { maxRecords: {} },
        usage: { asOf: "2026-09-20T09:00:00Z", users: 1, pendingUsers: 0, records: {}, webhooks: 2, apiKeys: 0 },
      })
    );
    const first = renderWithProviders(<PlanUsagePage />);
    expect(await screen.findByTestId("usage-webhooks")).toHaveTextContent("Sınırsız");
    expect(screen.getByTestId("usage-apiKeys")).toHaveTextContent("0 · Sınırsız");
    first.unmount();

    serve(subscriptionInfo({ modules: { integrations: false }, usage: { asOf: "2026-09-20T09:00:00Z", users: 1, pendingUsers: 0, records: {}, webhooks: 2, apiKeys: 1 } }));
    renderWithProviders(<PlanUsagePage />);
    await screen.findByTestId("usage-users");
    expect(screen.queryByTestId("usage-webhooks")).not.toBeInTheDocument();
    expect(screen.queryByTestId("usage-apiKeys")).not.toBeInTheDocument();
    expect(screen.getByTestId("module-integrations")).toHaveTextContent("Entegrasyonlar: Plana dahil değil");
  });

  it("names the new limits in the over-limit report", async () => {
    serve(
      subscriptionInfo({
        overLimit: [
          { limit: "webhooks", max: 2, used: 4 },
          { limit: "api_keys", max: 1, used: 3 },
        ],
      })
    );
    renderWithProviders(<PlanUsagePage />);
    const report = await screen.findByTestId("over-limit");
    expect(report).toHaveTextContent("webhook: 4 / 2");
    expect(report).toHaveTextContent("API anahtarı: 3 / 1");
  });
});

describe("plan and limit error texts of the integrations screens", () => {
  it("words the 402 limit and the module-disabled answers with the webhook / API key names", () => {
    expect(getApiErrorMessage(problem(402, { code: "plan.limit_exceeded", args: { limit: "webhooks", max: 5, used: 5 } }))).toBe(
      "Plan limitine ulaşıldı: webhook 5/5"
    );
    expect(getApiErrorMessage(problem(402, { code: "plan.limit_exceeded", args: { limit: "api_keys", max: 3, used: 3 } }))).toBe(
      "Plan limitine ulaşıldı: API anahtarı 3/3"
    );
    expect(getApiErrorMessage(problem(403, { code: "plan.module_disabled", args: { module: "integrations" } }))).toBe(
      "Bu modül planınıza dahil değil: Entegrasyonlar"
    );
  });

  it("words the integration error codes (found through integrations:errors.*)", () => {
    const cases: [string, string][] = [
      ["webhook.delivery_unavailable", "Bu kurulumda webhook gönderimi kapalı"],
      ["webhook.disabled", "Webhook pasif; önce etkinleştirin"],
      ["delivery.not_redeliverable", "Bu teslimat henüz tamamlanmadı; yeniden gönderilemez"],
      ["api_key.revoked", "İptal edilmiş anahtar değiştirilemez"],
      ["api_key.active", "Etkin anahtar silinemez; önce iptal edin"],
      ["role.permission_escalation", "Sahip olmadığınız bir izni anahtara veremezsiniz"],
    ];
    for (const [code, text] of cases) {
      expect(getApiErrorMessage(problem(409, { code }))).toBe(text);
    }
    expect(getApiErrorMessage(problem(400, { code: "api_key.scope_not_allowed", args: { scope: "org.x" } }))).toBe(
      "Bu kapsam bir API anahtarına verilemez: org.x"
    );
  });
});
