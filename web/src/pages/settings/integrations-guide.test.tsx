import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, type MockClient } from "@/test/crm";
import { eventInfo, EVENTS, integrationsStatus } from "@/test/integrations";
import { platformMe, setMe, subscription } from "@/test/platform";
import { PERMISSIONS, type WebhookEventInfo } from "@/types";
import IntegrationsPage from "./integrations";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

let events: WebhookEventInfo[];
let openApi: () => unknown;

function render() {
  return renderWithProviders(<IntegrationsPage />, { route: "/app/settings/integrations?tab=developer" });
}

beforeEach(() => {
  vi.clearAllMocks();
  events = [...EVENTS, eventInfo("lead.converted", "sales", { deprecated: true, sunsetOn: "2027-03-01", description: "Eski tür" })];
  openApi = () => ({ openapi: "3.1.0", info: { title: "Sense CRM" }, paths: {} });
  installApi(client, {
    "GET /integrations/status": () => integrationsStatus({ signatureToleranceSeconds: 300, maxAttempts: 8, timeoutSeconds: 10, deliveryRetentionDays: 30 }),
    "GET /integrations/webhook-events": () => events,
    "GET /integrations/openapi.json": () => openApi(),
  });
  setMe(platformMe([PERMISSIONS.orgIntegrationsManage], { subscription: subscription() }));
});
afterEach(() => clearSession());

describe("Developer tab", () => {
  it("explains key usage with a copyable curl example that carries only a placeholder key", async () => {
    render();
    const curl = await screen.findByTestId("guide-curl");
    expect(curl).toHaveTextContent("Authorization: Bearer crmk_<...>");
    expect(curl).toHaveTextContent("/api/v1/leads?page=1&pageSize=25");
    const api = screen.getByTestId("guide-api");
    expect(within(api).getByText(/X-Api-Key başlığı desteklenmez/)).toBeInTheDocument();
    expect(await within(api).findByText(/Anahtar başına dakikada 120 istek/)).toBeInTheDocument();
    expect(within(api).getByRole("button", { name: "İlk çağrı kopyala" })).toBeInTheDocument();
  });

  it("documents paging, errors, versioning and the webhook headers", async () => {
    render();
    const conventions = await screen.findByTestId("guide-conventions");
    expect(conventions).toHaveTextContent("pageSize (varsayılan 25, en çok 100)");
    expect(conventions).toHaveTextContent("/api/v2");
    const headers = screen.getByTestId("guide-headers");
    for (const name of ["Content-Type", "User-Agent", "X-Crm-Event-Id", "X-Crm-Event-Type", "X-Crm-Delivery-Id", "X-Crm-Delivery-Attempt", "X-Crm-Signature"]) {
      expect(within(headers).getByText(name)).toBeInTheDocument();
    }
    expect(screen.getByTestId("guide-envelope")).toHaveTextContent('"type": "lead.created"');
  });

  it("shows the signature rule with the server's tolerance and the retry policy from the status", async () => {
    render();
    const webhooks = await screen.findByTestId("guide-webhooks");
    await waitFor(() => expect(within(webhooks).getByTestId("guide-retry")).toHaveTextContent("en çok 8 kez"));
    expect(within(webhooks).getByTestId("guide-retry")).toHaveTextContent("Zaman aşımı 10 sn");
    expect(within(webhooks).getByTestId("guide-retry")).toHaveTextContent("30 gün");
    expect(webhooks).toHaveTextContent("en çok 300 sn");
    expect(webhooks).toHaveTextContent("sabit zamanlı");
  });

  it("gives copyable signature verification examples in Node, Python and C#", async () => {
    const user = userEvent.setup();
    render();
    expect(await screen.findByTestId("verify-node")).toHaveTextContent("crypto.timingSafeEqual");
    await user.click(screen.getByRole("tab", { name: "Python" }));
    expect(await screen.findByTestId("verify-python")).toHaveTextContent("hmac.compare_digest");
    await user.click(screen.getByRole("tab", { name: "C#" }));
    expect(await screen.findByTestId("verify-csharp")).toHaveTextContent("CryptographicOperations.FixedTimeEquals");
    // Every block has its own copy button.
    await user.click(screen.getByRole("button", { name: "C# doğrulama örneği kopyala" }));
    expect(await navigator.clipboard.readText()).toContain("FixedTimeEquals");
  });

  it("lists the live event catalog with sample envelopes, plan availability and deprecation", async () => {
    const user = userEvent.setup();
    render();
    const catalog = await screen.findByTestId("event-catalog");
    for (const type of ["lead.created", "deal.won", "quote.accepted", "case.created", "lead.converted"]) {
      expect(within(catalog).getByRole("button", { name: `${type} olayının ayrıntısı` })).toBeInTheDocument();
    }
    expect(within(catalog).getByText("Planınızda bu modül yok")).toBeInTheDocument();
    expect(within(catalog).getByText(/Kaldırılma:/)).toBeInTheDocument();
    await user.click(within(catalog).getByRole("button", { name: "lead.created olayının ayrıntısı" }));
    expect(await screen.findByTestId("catalog-sample-lead.created")).toHaveTextContent('"type": "lead.created"');
  });

  it("downloads the OpenAPI document through an authenticated request and a temporary link", async () => {
    const user = userEvent.setup();
    const created: Blob[] = [];
    const createObjectURL = vi.fn((blob: Blob) => {
      created.push(blob);
      return "blob:openapi-1";
    });
    const revokeObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    let downloaded: { name: string; href: string } | undefined;
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      downloaded = { name: this.download, href: this.href };
    });
    render();
    await user.click(await screen.findByRole("button", { name: "OpenAPI belgesini indir" }));
    await waitFor(() => expect(downloaded).toEqual({ name: "openapi.json", href: "blob:openapi-1" }));
    // The request goes through the authenticated client (Bearer header), never a plain link.
    expect(client.get).toHaveBeenCalledWith("/integrations/openapi.json");
    expect(created[0]?.type).toBe("application/json");
    expect(JSON.parse(await created[0]!.text())).toMatchObject({ openapi: "3.1.0" });
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:openapi-1");
    expect(toastApiError).not.toHaveBeenCalled();
    expect(screen.queryByRole("link", { name: /OpenAPI/ })).not.toBeInTheDocument();
    click.mockRestore();
  });

  it("toasts a failed download (plan.module_disabled) and saves nothing", async () => {
    const user = userEvent.setup();
    const failure = problem(403, { code: "plan.module_disabled", args: { module: "integrations" } });
    openApi = () => failure;
    const createObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL });
    render();
    await user.click(await screen.findByRole("button", { name: "OpenAPI belgesini indir" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
    expect(createObjectURL).not.toHaveBeenCalled();
  });
});
