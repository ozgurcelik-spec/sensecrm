import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toast, toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, LocationDisplay, page, problem, type MockClient } from "@/test/crm";
import { delivery, deliveryDetail, EVENTS, webhook } from "@/test/integrations";
import { platformMe, setMe, subscription } from "@/test/platform";
import { PERMISSIONS, type WebhookDelivery, type WebhookDeliveryDetail } from "@/types";
import IntegrationsPage from "./integrations";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const EVENT_ID = "0192f0a1-0000-7000-8000-00000000e001";

let deliveries: WebhookDelivery[];
let details: Record<string, WebhookDeliveryDetail>;
let redeliverHandler: (id: string) => unknown;

const listParams = () => client.get.mock.calls.filter(([url]) => url === "/integrations/deliveries").at(-1)?.[1]?.params;
const listCalls = () => client.get.mock.calls.filter(([url]) => url === "/integrations/deliveries");

function render(route = "/app/settings/integrations?tab=deliveries") {
  return renderWithProviders(
    <>
      <IntegrationsPage />
      <LocationDisplay />
    </>,
    { route }
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  deliveries = [
    delivery("d1"),
    delivery("d2", {
      status: "failed",
      attempts: 8,
      responseStatus: 503,
      failureReason: "retries_exhausted",
      eventType: "deal.won",
      eventId: "0192f0a1-0000-7000-8000-00000000e002",
      durationMs: 3400,
    }),
    delivery("d3", { status: "pending", attempts: 2, nextAttemptAt: "2026-09-20T09:05:00Z", responseStatus: undefined, durationMs: undefined, completedAt: undefined }),
    delivery("d4", { kind: "ping", eventType: "ping", status: "failed", failureReason: "blocked_destination", responseStatus: undefined }),
  ];
  details = {
    d1: deliveryDetail("d1"),
    d2: deliveryDetail("d2", {
      ...delivery("d2", { status: "failed", attempts: 2, responseStatus: 503, failureReason: "retries_exhausted", eventType: "deal.won" }),
      attemptLog: [
        { attemptNo: 1, startedAt: "2026-09-20T09:00:04Z", durationMs: 200, responseStatus: 503, failureReason: "http_error", errorDetail: "upstream unavailable", responseSnippet: "Service Unavailable" },
        { attemptNo: 2, startedAt: "2026-09-20T09:00:20Z", durationMs: 10000, failureReason: "timeout", responseSnippet: "x".repeat(2048) },
      ],
    }),
    d3: deliveryDetail("d3", { ...delivery("d3", { status: "pending", attempts: 2 }), attemptLog: [] }),
  };
  redeliverHandler = () => ({ deliveryId: "d-new" });
  installApi(client, {
    "GET /integrations/status": () => ({}),
    "GET /integrations/webhook-events": () => EVENTS,
    "GET /integrations/webhooks": () => page([webhook("w1", { name: "ERP köprüsü" }), webhook("w2", { name: "Muhasebe" })]),
    "GET /integrations/deliveries": () => page(deliveries, { totalCount: deliveries.length }),
    "GET /integrations/deliveries/d1": () => details.d1,
    "GET /integrations/deliveries/d2": () => details.d2,
    "GET /integrations/deliveries/d3": () => details.d3,
    "POST /integrations/deliveries/d2/redeliver": () => redeliverHandler("d2"),
    "POST /integrations/deliveries/d1/redeliver": () => redeliverHandler("d1"),
    "POST /integrations/deliveries/d3/redeliver": () => redeliverHandler("d3"),
    "POST /integrations/deliveries/d4/redeliver": () => redeliverHandler("d4"),
  });
  setMe(platformMe([PERMISSIONS.orgIntegrationsManage], { subscription: subscription() }));
});
afterEach(() => clearSession());

describe("Delivery log - table", () => {
  it("shows time, event, subscription, status badge, attempts n/8, HTTP code, duration and next attempt", async () => {
    render();
    const ok = (await screen.findByTestId("delivery-status-succeeded")).closest("tr") as HTMLElement;
    expect(within(ok).getByText("Potansiyel müşteri oluşturuldu")).toBeInTheDocument();
    expect(within(ok).getByText("ERP köprüsü")).toBeInTheDocument();
    expect(within(ok).getByText("hooks.acme.com.tr")).toBeInTheDocument();
    expect(within(ok).getByText("1/8")).toBeInTheDocument();
    expect(within(ok).getByText("200")).toBeInTheDocument();
    expect(within(ok).getByText("120 ms")).toBeInTheDocument();

    const failed = screen.getAllByTestId("delivery-status-failed")[0]?.closest("tr") as HTMLElement;
    expect(within(failed).getByText("8/8")).toBeInTheDocument();
    expect(within(failed).getByText("503")).toBeInTheDocument();
    expect(within(failed).getByText("Deneme hakkı bitti")).toBeInTheDocument();

    const pending = screen.getByTestId("delivery-status-pending").closest("tr") as HTMLElement;
    expect(within(pending).getByText("2/8")).toBeInTheDocument();
    expect(within(pending).getByText(/12:05/)).toBeInTheDocument();

    const ping = screen.getByText("Hedef engellendi").closest("tr") as HTMLElement;
    expect(within(ping).getByText("Test")).toBeInTheDocument();
    expect(within(ping).getByText("Test ping'i")).toBeInTheDocument();
  });

  it("shows the empty state", async () => {
    deliveries = [];
    render();
    expect(await screen.findByText("Teslimat kaydı yok")).toBeInTheDocument();
  });

  it("offers the quick resend only on failed rows", async () => {
    render();
    await screen.findByTestId("delivery-status-succeeded");
    expect(screen.getAllByRole("button", { name: /teslimatını yeniden gönder$/ })).toHaveLength(2);
    const okRow = screen.getByTestId("delivery-status-succeeded").closest("tr") as HTMLElement;
    expect(within(okRow).queryByRole("button", { name: /yeniden gönder/ })).not.toBeInTheDocument();
  });
});

describe("Delivery log - filters", () => {
  it("keeps subscription, status chips, event type, kind, event id and day range in the URL and sends them", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("delivery-status-succeeded");
    expect(listParams()).toEqual({ page: 1, pageSize: 25 });

    await user.click(screen.getByRole("combobox", { name: "Abonelik" }));
    await user.click(await screen.findByRole("option", { name: "Muhasebe" }));
    await waitFor(() => expect(listParams()).toEqual({ page: 1, pageSize: 25, subscriptionId: "w2" }));

    // Status chips build a comma separated list in the canonical order.
    await user.click(screen.getByRole("checkbox", { name: "Başarısız" }));
    await user.click(screen.getByRole("checkbox", { name: "Bekliyor" }));
    await waitFor(() => expect(listParams()).toMatchObject({ status: "pending,failed" }));
    expect(screen.getByTestId("location")).toHaveTextContent("status=pending%2Cfailed");

    await user.click(screen.getByRole("combobox", { name: "Olay türü" }));
    await user.click(await screen.findByRole("option", { name: "Fırsat kazanıldı" }));
    await waitFor(() => expect(listParams()).toMatchObject({ eventType: "deal.won" }));

    await user.click(screen.getByRole("combobox", { name: "Tür" }));
    await user.click(await screen.findByRole("option", { name: "Test" }));
    await waitFor(() => expect(listParams()).toMatchObject({ kind: "ping" }));

    await user.type(screen.getByRole("textbox", { name: "Olay kimliği" }), EVENT_ID);
    await waitFor(() => expect(listParams()).toMatchObject({ eventId: EVENT_ID }));

    await user.type(screen.getByLabelText("Başlangıç"), "2026-09-01");
    await user.type(screen.getByLabelText("Bitiş"), "2026-09-20");
    await waitFor(() => expect(listParams()).toMatchObject({ from: "2026-09-01", to: "2026-09-20" }));

    await user.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(listParams()).toEqual({ page: 1, pageSize: 25 }));
  });

  it("restores the filters from the URL (including the sort)", async () => {
    render(`/app/settings/integrations?tab=deliveries&subscriptionId=w1&status=failed&eventId=${EVENT_ID}&from=2026-09-01&sort=-attempts&page=2`);
    await screen.findByTestId("delivery-status-succeeded");
    expect(listParams()).toEqual({ page: 2, pageSize: 25, subscriptionId: "w1", status: "failed", eventId: EVENT_ID, from: "2026-09-01", sort: "-attempts" });
    expect(screen.getByRole("checkbox", { name: "Başarısız" })).toBeChecked();
  });

  it("does not send an event id that is not a full id, and says so", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("delivery-status-succeeded");
    await user.type(screen.getByRole("textbox", { name: "Olay kimliği" }), "0192f0a1");
    expect(await screen.findByText("Geçerli bir kimlik girin")).toBeInTheDocument();
    expect(listParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("refuses a day range over 90 days without asking the server", async () => {
    render("/app/settings/integrations?tab=deliveries&from=2026-01-01&to=2026-06-30");
    expect(await screen.findByText("Aralık en çok 90 gün olabilir")).toBeInTheDocument();
    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/integrations/webhooks", expect.anything()));
    expect(listCalls().some(([, config]) => config?.params?.to === "2026-06-30")).toBe(false);
  });

  it("sorts by a column header", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("delivery-status-succeeded");
    await user.click(screen.getByRole("button", { name: /Deneme/ }));
    await waitFor(() => expect(listParams()).toMatchObject({ sort: "attempts" }));
  });
});

describe("Delivery log - detail drawer", () => {
  it("shows the envelope, the headers with the redacted signature, the attempt timeline and the response excerpt", async () => {
    const user = userEvent.setup();
    render();
    const row = (await screen.findAllByTestId("delivery-status-failed"))[0]?.closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: "Fırsat kazanıldı teslimatının ayrıntısı" }));
    const detail = await screen.findByTestId("delivery-detail");
    expect(within(detail).getByRole("alert")).toHaveTextContent("Deneme hakkı bitti");
    expect(within(detail).getByTestId("delivery-payload")).toHaveTextContent('"leadId": "l1"');
    const headers = within(detail).getByTestId("delivery-headers");
    expect(within(headers).getByText("X-Crm-Signature").closest("tr")).toHaveTextContent("[redacted]");

    const log = within(detail).getByTestId("attempt-log");
    const first = within(log).getByTestId("attempt-1");
    expect(first).toHaveTextContent("1. deneme");
    expect(first).toHaveTextContent("HTTP: 503");
    expect(first).toHaveTextContent("200 ms");
    expect(first).toHaveTextContent("Alıcı hata döndürdü");
    expect(first).toHaveTextContent("upstream unavailable");
    expect(within(first).getByTestId("snippet-1")).toHaveTextContent("Service Unavailable");
    expect(first).not.toHaveTextContent("Yanıt kesildi");
    const second = within(log).getByTestId("attempt-2");
    expect(second).toHaveTextContent("Zaman aşımı");
    // A snippet at the 2 KiB cut is marked as truncated.
    expect(second).toHaveTextContent("Yanıt kesildi");
  });

  it("does not offer a resend for a pending delivery", async () => {
    const user = userEvent.setup();
    render();
    const row = (await screen.findByTestId("delivery-status-pending")).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: /teslimatının ayrıntısı/ }));
    const detail = await screen.findByTestId("delivery-detail");
    expect(within(detail).getByText("Henüz deneme yapılmadı")).toBeInTheDocument();
    expect(within(detail).queryByRole("button", { name: "Yeniden gönder" })).not.toBeInTheDocument();
  });

  it("resends a failed delivery after a confirmation that explains de-duplication", async () => {
    const user = userEvent.setup();
    render();
    const row = (await screen.findAllByTestId("delivery-status-failed"))[0]?.closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: /teslimatının ayrıntısı/ }));
    const detail = await screen.findByTestId("delivery-detail");
    await user.click(within(detail).getByRole("button", { name: "Yeniden gönder" }));
    const dialog = (await screen.findByText(/tekilleştirmelidir/)).closest("section") as HTMLElement;
    expect(client.post).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole("button", { name: "Yeniden gönder" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/integrations/deliveries/d2/redeliver"));
    await waitFor(() => expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success", description: "Yeniden gönderim kuyruğa alındı" })));
  });

  it("can also resend a succeeded delivery", async () => {
    const user = userEvent.setup();
    render();
    const row = (await screen.findByTestId("delivery-status-succeeded")).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: /teslimatının ayrıntısı/ }));
    const detail = await screen.findByTestId("delivery-detail");
    expect(within(detail).getByRole("button", { name: "Yeniden gönder" })).toBeEnabled();
  });
});

describe("Delivery log - resend errors", () => {
  async function resendFirstFailedRow() {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("delivery-status-succeeded");
    await user.click(screen.getAllByRole("button", { name: /teslimatını yeniden gönder$/ })[0] as HTMLElement);
    const dialog = await screen.findByText(/tekilleştirmelidir/);
    const confirm = within(dialog.closest("section") ?? document.body).getByRole("button", { name: "Yeniden gönder" });
    await user.click(confirm);
  }

  it.each([
    ["409 delivery.not_redeliverable", 409, "delivery.not_redeliverable"],
    ["409 webhook.disabled", 409, "webhook.disabled"],
    ["409 webhook.delivery_unavailable", 409, "webhook.delivery_unavailable"],
    ["429 rate limit", 429, "general.rate_limit_exceeded"],
  ])("hands the %s answer to the error toast", async (_name, status, code) => {
    const failure = problem(status, { code });
    redeliverHandler = () => failure;
    await resendFirstFailedRow();
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
    expect(toast).not.toHaveBeenCalled();
  });

  it("refreshes the log after a resend", async () => {
    await resendFirstFailedRow();
    await waitFor(() => expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" })));
    await waitFor(() => expect(listCalls().length).toBeGreaterThan(1));
  });
});
