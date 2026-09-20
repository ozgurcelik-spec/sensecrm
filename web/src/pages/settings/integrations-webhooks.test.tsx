import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toast, toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, LocationDisplay, page, problem, type MockClient } from "@/test/crm";
import { deliveryDetail, EVENTS, integrationsStatus, webhook } from "@/test/integrations";
import { platformMe, setMe, subscription } from "@/test/platform";
import { PERMISSIONS, type IntegrationsStatus, type WebhookSubscription } from "@/types";
import IntegrationsPage from "./integrations";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const MANAGE = PERMISSIONS.orgIntegrationsManage;
/** The name field (the required asterisk is part of its label; the search box must not match). */
const NAME_FIELD = /^Ad\s*\*?$/;
const SECRET = "whsec_Zm9vYmFyLXNlY3JldC12YWx1ZS0xMjM0NTY3ODkw";

let hooks: WebhookSubscription[];
let status: IntegrationsStatus;
let createHandler: (body: unknown) => unknown;
let rotateHandler: (body: unknown) => unknown;
let testHandler: () => unknown;
let pingDetail: ReturnType<typeof deliveryDetail>;

const listParams = () => client.get.mock.calls.filter(([url]) => url === "/integrations/webhooks").at(-1)?.[1]?.params;
const posted = (url: string) => client.post.mock.calls.filter(([called]) => called === url);

function signIn(options: { readOnly?: boolean; permissions?: string[] } = {}) {
  setMe(
    platformMe(options.permissions ?? [MANAGE], {
      subscription: options.readOnly ? subscription({ accessLevel: "readOnly" }) : subscription(),
    })
  );
}

function render(route = "/app/settings/integrations") {
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
  window.localStorage.clear();
  window.sessionStorage.clear();
  hooks = [
    webhook("w1", { name: "ERP köprüsü", eventTypes: ["lead.created", "deal.won", "case.created", "quote.accepted"] }),
    webhook("w2", { name: "Muhasebe", health: "degraded", lastFailureAt: "2026-09-20T08:30:00Z", consecutiveFailures: 2 }),
    webhook("w3", {
      name: "Eski sistem",
      enabled: false,
      disabledReason: "failing",
      health: "disabled",
      consecutiveFailures: 10,
      lastFailureAt: "2026-09-19T08:30:00Z",
    }),
  ];
  status = integrationsStatus();
  createHandler = () => ({ ...webhook("w9", { name: "Yeni", secretHint: "…7890" }), secret: SECRET });
  rotateHandler = () => ({
    secret: SECRET,
    secretHint: "…7890",
    secretVersion: 2,
    previousSecretExpiresAt: "2026-09-21T09:00:00Z",
  });
  testHandler = () => ({ deliveryId: "d-ping" });
  pingDetail = deliveryDetail("d-ping", { kind: "ping", eventType: "ping", status: "succeeded", responseStatus: 204 });
  installApi(client, {
    "GET /integrations/status": () => status,
    "GET /integrations/webhook-events": () => EVENTS,
    "GET /integrations/webhooks": () => page(hooks),
    "POST /integrations/webhooks": ({ body }) => createHandler(body),
    "PUT /integrations/webhooks/w1": () => undefined,
    "POST /integrations/webhooks/w1/disable": () => undefined,
    "POST /integrations/webhooks/w3/enable": () => undefined,
    "POST /integrations/webhooks/w1/rotate-secret": ({ body }) => rotateHandler(body),
    "POST /integrations/webhooks/w1/test": () => testHandler(),
    "DELETE /integrations/webhooks/w1": () => undefined,
    "GET /integrations/deliveries/d-ping": () => pingDetail,
  });
  signIn();
});
afterEach(() => clearSession());

describe("Webhooks tab - list", () => {
  it("lists the subscriptions with host, event chips and health badges", async () => {
    render();
    const erp = (await screen.findByText("ERP köprüsü")).closest("tr") as HTMLElement;
    expect(within(erp).getByText("hooks.acme.com.tr")).toBeInTheDocument();
    expect(within(erp).getByText("Potansiyel müşteri oluşturuldu")).toBeInTheDocument();
    expect(within(erp).getByText("+1")).toBeInTheDocument();
    expect(within(erp).getByTestId("health-healthy")).toHaveTextContent("Sağlıklı");
    expect(within(screen.getByText("Muhasebe").closest("tr") as HTMLElement).getByTestId("health-degraded")).toHaveTextContent("Sorunlu");
    expect(screen.queryByTestId("egress-disabled")).not.toBeInTheDocument();
  });

  it("explains an automatically disabled subscription and re-enables it", async () => {
    const user = userEvent.setup();
    render();
    expect(await screen.findByTestId("auto-disabled-w3")).toHaveTextContent("Art arda hata nedeniyle pasifleştirildi");
    const row = screen.getByText("Eski sistem").closest("tr") as HTMLElement;
    expect(within(row).getByTestId("health-disabled")).toHaveTextContent("Pasif");
    await user.click(within(row).getByRole("button", { name: "Yeniden etkinleştir" }));
    await waitFor(() => expect(posted("/integrations/webhooks/w3/enable")).toHaveLength(1));
  });

  it("switches a subscription off with its enabled switch", async () => {
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("switch", { name: "ERP köprüsü etkin" }));
    await waitFor(() => expect(posted("/integrations/webhooks/w1/disable")).toHaveLength(1));
  });

  it("keeps search, enabled and event type filters in the URL and sends them", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("ERP köprüsü");
    expect(listParams()).toEqual({ page: 1, pageSize: 25 });

    await user.click(screen.getByRole("combobox", { name: "Durum" }));
    await user.click(await screen.findByRole("option", { name: "Pasif" }));
    await waitFor(() => expect(listParams()).toEqual({ page: 1, pageSize: 25, enabled: "false" }));
    expect(screen.getByTestId("location")).toHaveTextContent("enabled=false");

    await user.click(screen.getByRole("combobox", { name: "Olay türü" }));
    await user.click(await screen.findByRole("option", { name: "Fırsat kazanıldı" }));
    await waitFor(() => expect(listParams()).toEqual({ page: 1, pageSize: 25, enabled: "false", eventType: "deal.won" }));

    await user.type(screen.getByRole("searchbox"), "erp");
    await waitFor(() => expect(listParams()).toMatchObject({ q: "erp" }));

    await user.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(listParams()).toEqual({ page: 1, pageSize: 25 }));
  });

  it("restores sort and filters from the URL", async () => {
    render("/app/settings/integrations?enabled=true&eventType=lead.created&sort=-lastFailureAt&page=2");
    await screen.findByText("ERP köprüsü");
    expect(listParams()).toEqual({ page: 2, pageSize: 25, enabled: "true", eventType: "lead.created", sort: "-lastFailureAt" });
  });

  it("shows the empty state", async () => {
    hooks = [];
    render();
    expect(await screen.findByText("Henüz webhook yok")).toBeInTheDocument();
  });
});

describe("Webhooks tab - deployment and plan states", () => {
  it("shows the persistent 'webhooks are off' banner, the restricted hosts note and disables the test ping", async () => {
    status = integrationsStatus({ webhooksEnabled: false, restrictedHosts: true });
    render();
    const banner = await screen.findByTestId("egress-disabled");
    expect(banner).toHaveTextContent("Bu kurulumda webhook gönderimi kapalı");
    expect(screen.getByTestId("restricted-hosts")).toHaveTextContent("izin verdiği alan adlarına");
    expect(await screen.findByRole("button", { name: "ERP köprüsü için test ping'i gönder" })).toBeDisabled();
    // Preparing subscriptions stays possible.
    expect(screen.getByRole("button", { name: "Yeni webhook" })).toBeEnabled();
  });

  it("blocks creation and links to the plan page once the plan limit is used up", async () => {
    status = integrationsStatus({ limits: { maxWebhooks: 3 }, usage: { webhooks: 3, apiKeys: 0 } });
    render();
    const note = await screen.findByTestId("webhook-limit");
    expect(note).toHaveTextContent("Plan limitine ulaşıldı: webhook 3/3");
    expect(within(note).getByRole("link", { name: "Plan ve kullanım" })).toHaveAttribute("href", "/app/settings/plan");
    expect(screen.getByRole("button", { name: "Yeni webhook" })).toBeDisabled();
  });

  it("stays usable below the limit", async () => {
    status = integrationsStatus({ limits: { maxWebhooks: 5 }, usage: { webhooks: 3, apiKeys: 0 } });
    render();
    await screen.findByText("ERP köprüsü");
    expect(screen.queryByTestId("webhook-limit")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Yeni webhook" })).toBeEnabled();
  });

  it("read-only mode: shows the note, keeps the lists and lets the server answer writes with a toast", async () => {
    signIn({ readOnly: true });
    client.post.mockImplementation(async (url: string) => {
      if (url === "/integrations/webhooks/w1/disable") throw problem(403, { code: "tenant.suspended", args: { reason: "suspended" } });
      throw new Error(`unexpected ${url}`);
    });
    const user = userEvent.setup();
    render();
    expect(await screen.findByTestId("integrations-readonly")).toHaveTextContent("salt okunur");
    await user.click(await screen.findByRole("switch", { name: "ERP köprüsü etkin" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    expect((toastApiError as unknown as { mock: { calls: unknown[][] } }).mock.calls[0]?.[0]).toMatchObject({
      response: { data: { code: "tenant.suspended" } },
    });
  });
});

describe("Webhooks tab - create", () => {
  async function openCreate() {
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("button", { name: "Yeni webhook" }));
    await screen.findByRole("textbox", { name: NAME_FIELD });
    return user;
  }

  it("validates the URL on the client with the syntactic SSRF rules and never posts an invalid form", async () => {
    const user = await openCreate();
    const url = screen.getByLabelText(/Hedef adres/);
    const submit = () => user.click(screen.getByRole("button", { name: "Oluştur" }));
    await user.type(screen.getByRole("textbox", { name: NAME_FIELD }), "Test");

    const cases: [string, string][] = [
      ["http://hooks.example.com/x", "Adres https:// ile başlamalıdır"],
      ["https://10.1.2.3/x", "IP adresi kullanılamaz"],
      ["https://user:pw@hooks.example.com/x", "kullanıcı adı ve parola içeremez"],
      ["https://localhost/x", "Bu ana bilgisayar adı kullanılamaz"],
    ];
    for (const [value, message] of cases) {
      fireEvent.change(url, { target: { value } });
      await submit();
      expect(await screen.findByText(new RegExp(message))).toBeInTheDocument();
    }
    expect(client.post).not.toHaveBeenCalled();
  });

  it("requires at least one event type and never sends an empty list", async () => {
    const user = await openCreate();
    await user.type(screen.getByRole("textbox", { name: NAME_FIELD }), "Test");
    fireEvent.change(screen.getByLabelText(/Hedef adres/), { target: { value: "https://hooks.example.com/x" } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("En az bir olay türü seçin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("groups the catalog by Sales / Commerce / Service, dims a type the plan lacks and previews sample envelopes", async () => {
    const user = await openCreate();
    const picker = await screen.findByTestId("event-type-picker");
    expect(within(picker).getByText("Satış")).toBeInTheDocument();
    expect(within(picker).getByText("Ticaret")).toBeInTheDocument();
    expect(within(picker).getByText("Servis")).toBeInTheDocument();
    expect(within(picker).getByRole("checkbox", { name: "Teklif kabul edildi" })).toBeDisabled();
    expect(within(picker).getByTestId("unavailable-quote.accepted")).toHaveTextContent("Planınızda bu modül yok");
    expect(within(picker).getByRole("checkbox", { name: "Talep oluşturuldu" })).toBeEnabled();

    await user.click(within(picker).getByRole("button", { name: "lead.created için örnek zarf" }));
    expect(await within(picker).findByTestId("sample-lead.created")).toHaveTextContent('"type": "lead.created"');
  });

  it("creates the subscription and shows the secret exactly once", async () => {
    const user = await openCreate();
    await user.type(screen.getByRole("textbox", { name: NAME_FIELD }), "  Yeni  ");
    fireEvent.change(screen.getByLabelText(/Hedef adres/), { target: { value: " https://hooks.example.com/crm " } });
    const picker = await screen.findByTestId("event-type-picker");
    await user.click(within(picker).getByRole("checkbox", { name: "Potansiyel müşteri oluşturuldu" }));
    await user.click(within(picker).getByRole("checkbox", { name: "Talep oluşturuldu" }));
    await user.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(posted("/integrations/webhooks")).toHaveLength(1));
    expect(posted("/integrations/webhooks")[0]?.[1]).toEqual({
      name: "Yeni",
      url: "https://hooks.example.com/crm",
      eventTypes: ["lead.created", "case.created"],
      enabled: true,
    });

    const reveal = await screen.findByTestId("secret-reveal");
    expect(within(reveal).getByTestId("secret-value")).toHaveValue(SECRET);
    // The form is gone, the list refreshes.
    expect(screen.queryByLabelText(/Hedef adres/)).not.toBeInTheDocument();

    // Cannot be closed by accident: Escape does nothing and the button waits for the confirmation.
    await user.keyboard("{Escape}");
    expect(screen.getByTestId("secret-reveal")).toBeInTheDocument();
    const done = within(reveal).getByRole("button", { name: "Tamam" });
    expect(done).toBeDisabled();

    // Copy puts the exact secret on the clipboard.
    await user.click(within(reveal).getByRole("button", { name: "Kopyala" }));
    expect(await navigator.clipboard.readText()).toBe(SECRET);

    await user.click(within(reveal).getByRole("checkbox", { name: "Kaydettim" }));
    expect(done).toBeEnabled();
    await user.click(done);

    // Gone from the DOM; nothing was written to the browser storage.
    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain(SECRET);
    expect(JSON.stringify({ ...window.localStorage })).not.toContain("whsec_");
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain("whsec_");
    // Later reads only carry the hint.
    expect(JSON.stringify(hooks)).not.toContain(SECRET);
  });

  it("puts the server's url_invalid reason and a name conflict on their fields and keeps the form open", async () => {
    const user = await openCreate();
    await user.type(screen.getByRole("textbox", { name: NAME_FIELD }), "Yeni");
    fireEvent.change(screen.getByLabelText(/Hedef adres/), { target: { value: "https://hooks.example.com:9999/x" } });
    await user.click(within(await screen.findByTestId("event-type-picker")).getByRole("checkbox", { name: "Talep oluşturuldu" }));

    createHandler = () => problem(400, { code: "webhook.url_invalid", args: { reason: "port" } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText(/443, 8443/)).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();

    createHandler = () => problem(400, { code: "webhook.url_invalid", args: { reason: "not_allow_listed" } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText(/izin verilen alan adları arasında değil/)).toBeInTheDocument();

    createHandler = () => problem(409, { code: "webhook.name_taken" });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Bu adla bir webhook zaten var")).toBeInTheDocument();
    expect(screen.getByLabelText(/Hedef adres/)).toBeInTheDocument();
  });

  it("toasts the plan limit error (402) and keeps the dialog open", async () => {
    const user = await openCreate();
    await user.type(screen.getByRole("textbox", { name: NAME_FIELD }), "Yeni");
    fireEvent.change(screen.getByLabelText(/Hedef adres/), { target: { value: "https://hooks.example.com/x" } });
    await user.click(within(await screen.findByTestId("event-type-picker")).getByRole("checkbox", { name: "Talep oluşturuldu" }));
    const failure = problem(402, { code: "plan.limit_exceeded", args: { limit: "webhooks", max: 5, used: 5 } });
    createHandler = () => failure;
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
    expect(screen.getByLabelText(/Hedef adres/)).toBeInTheDocument();
    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
  });

  it("edits with a full replacement (PUT) and keeps an already chosen type that the plan lacks removable", async () => {
    const user = userEvent.setup();
    render();
    const row = (await screen.findByText("ERP köprüsü")).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: "ERP köprüsü işlemleri" }));
    await user.click(await screen.findByRole("menuitem", { name: "Düzenle" }));
    const picker = await screen.findByTestId("event-type-picker");
    // quote.accepted is selected but unavailable in the plan: it can be removed (and not added back).
    const quote = within(picker).getByRole("checkbox", { name: "Teklif kabul edildi" });
    expect(quote).toBeChecked();
    expect(quote).toBeEnabled();
    await user.click(quote);
    expect(quote).not.toBeChecked();
    expect(quote).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]?.[0]).toBe("/integrations/webhooks/w1");
    expect(client.put.mock.calls[0]?.[1]).toEqual({
      name: "ERP köprüsü",
      url: "https://hooks.acme.com.tr/w1",
      eventTypes: ["lead.created", "deal.won", "case.created"],
      enabled: true,
    });
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
  });
});

describe("Webhooks tab - rotate secret", () => {
  async function openRotate() {
    const user = userEvent.setup();
    render();
    const row = (await screen.findByText("ERP köprüsü")).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: "ERP köprüsü işlemleri" }));
    await user.click(await screen.findByRole("menuitem", { name: "Gizli anahtarı döndür" }));
    await screen.findByLabelText(/Eski anahtarın geçerlilik süresi/);
    return user;
  }

  it("defaults to 24 hours, shows the new secret exactly once and mentions the grace period", async () => {
    const user = await openRotate();
    expect(screen.getByLabelText(/Eski anahtarın geçerlilik süresi/)).toHaveDisplayValue("24");
    await user.click(screen.getByRole("button", { name: "Döndür" }));
    await waitFor(() => expect(posted("/integrations/webhooks/w1/rotate-secret")).toHaveLength(1));
    expect(posted("/integrations/webhooks/w1/rotate-secret")[0]?.[1]).toEqual({ graceHours: 24 });
    const reveal = await screen.findByTestId("secret-reveal");
    expect(within(reveal).getByTestId("secret-value")).toHaveValue(SECRET);
    expect(within(reveal).getByTestId("grace-note")).toHaveTextContent("Eski anahtar");
    expect(within(reveal).getByRole("button", { name: "Tamam" })).toBeDisabled();
    await user.click(within(reveal).getByRole("checkbox", { name: "Kaydettim" }));
    await user.click(within(reveal).getByRole("button", { name: "Tamam" }));
    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain(SECRET);
  });

  it("accepts 0 and 168 hours and refuses 169 and empty values", async () => {
    const user = await openRotate();
    const input = screen.getByLabelText(/Eski anahtarın geçerlilik süresi/);
    await user.clear(input);
    await user.type(input, "169");
    await user.click(screen.getByRole("button", { name: "Döndür" }));
    expect(await screen.findByText("0 ile 168 arasında bir tam sayı girin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    await user.clear(input);
    await user.click(screen.getByRole("button", { name: "Döndür" }));
    expect(await screen.findByText("0 ile 168 arasında bir tam sayı girin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    await user.clear(input);
    await user.type(input, "0");
    await user.click(screen.getByRole("button", { name: "Döndür" }));
    await waitFor(() => expect(posted("/integrations/webhooks/w1/rotate-secret")[0]?.[1]).toEqual({ graceHours: 0 }));
  });

  it("toasts a failed rotation and shows no secret", async () => {
    const failure = problem(403, { code: "tenant.suspended", args: { reason: "suspended" } });
    rotateHandler = () => failure;
    const user = await openRotate();
    await user.click(screen.getByRole("button", { name: "Döndür" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
  });
});

describe("Webhooks tab - test ping and delete", () => {
  it("queues a ping, looks up its result, toasts it and opens the delivery drawer", async () => {
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("button", { name: "ERP köprüsü için test ping'i gönder" }));
    await waitFor(() => expect(posted("/integrations/webhooks/w1/test")).toHaveLength(1));
    await waitFor(() =>
      expect(toast).toHaveBeenCalledWith(
        expect.objectContaining({ variant: "success", description: "ERP köprüsü: test ping'i teslim edildi (HTTP 204)" })
      )
    );
    expect(await screen.findByTestId("delivery-detail")).toBeInTheDocument();
    expect(client.get).toHaveBeenCalledWith("/integrations/deliveries/d-ping");
  });

  it("toasts a failed ping with the reason", async () => {
    pingDetail = deliveryDetail("d-ping", {
      kind: "ping",
      eventType: "ping",
      status: "failed",
      failureReason: "blocked_destination",
      responseStatus: undefined,
    });
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("button", { name: "ERP köprüsü için test ping'i gönder" }));
    await waitFor(() =>
      expect(toast).toHaveBeenCalledWith(
        expect.objectContaining({ variant: "destructive", description: "ERP köprüsü: Hedef engellendi" })
      )
    );
  });

  it("words 409 delivery_unavailable and the 429 limit through the error toast", async () => {
    const failure = problem(409, { code: "webhook.delivery_unavailable" });
    testHandler = () => failure;
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("button", { name: "ERP köprüsü için test ping'i gönder" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
    expect(toast).not.toHaveBeenCalled();
  });

  it("deletes only after the name has been typed", async () => {
    const user = userEvent.setup();
    render();
    const row = (await screen.findByText("ERP köprüsü")).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: "ERP köprüsü işlemleri" }));
    await user.click(await screen.findByRole("menuitem", { name: "Sil" }));
    const confirm = screen.getByRole("button", { name: "Sil" });
    expect(confirm).toBeDisabled();
    await user.type(screen.getByLabelText("Webhook adı"), "ERP");
    expect(confirm).toBeDisabled();
    await user.type(screen.getByLabelText("Webhook adı"), " köprüsü");
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/integrations/webhooks/w1"));
  });

  it("jumps to the delivery log of one subscription", async () => {
    const user = userEvent.setup();
    installApi(client, {
      "GET /integrations/status": () => status,
      "GET /integrations/webhook-events": () => EVENTS,
      "GET /integrations/webhooks": () => page(hooks),
      "GET /integrations/deliveries": () => page([]),
    });
    render();
    const row = (await screen.findByText("ERP köprüsü")).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: "ERP köprüsü işlemleri" }));
    await user.click(await screen.findByRole("menuitem", { name: "Teslimatları göster" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("tab=deliveries&subscriptionId=w1"));
    await waitFor(() =>
      expect(client.get).toHaveBeenCalledWith("/integrations/deliveries", { params: { page: 1, pageSize: 25, subscriptionId: "w1" } })
    );
  });
});
