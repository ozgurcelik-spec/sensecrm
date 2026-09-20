import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toast, toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, LocationDisplay, MEMBERS, page, problem, type MockClient } from "@/test/crm";
import { delivery, tenantSettings } from "@/test/notifications";
import { platformMe, setMe, subscription } from "@/test/platform";
import { PERMISSIONS, type NotificationDelivery, type NotificationTenantSettings } from "@/types";
import NotificationSettingsPage from "./notifications";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const MANAGE = PERMISSIONS.orgNotificationsManage;

let settings: NotificationTenantSettings;
let deliveries: NotificationDelivery[];
let issues: unknown[];
let putSettings: (body: unknown) => unknown;
let testEmail: () => unknown;
let deliveryById: Record<string, NotificationDelivery>;
let retry: (body: unknown) => unknown;

const listCalls = () => client.get.mock.calls.filter(([url]) => url === "/notifications/deliveries");
const lastListParams = () => listCalls().at(-1)?.[1]?.params;
const calls = (method: "get" | "post" | "put", url: string) =>
  client[method].mock.calls.filter(([called]) => called === url);

function signIn(options: { readOnly?: boolean; permissions?: string[] } = {}) {
  setMe(
    platformMe(options.permissions ?? [MANAGE], {
      subscription: options.readOnly ? subscription({ accessLevel: "readOnly" }) : subscription(),
    })
  );
}

function render(route = "/app/settings/notifications") {
  return renderWithProviders(
    <>
      <NotificationSettingsPage />
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("NotificationSettingsPage - settings tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    settings = tenantSettings();
    deliveries = [];
    issues = [];
    deliveryById = {};
    putSettings = () => undefined;
    testEmail = () => ({ deliveryId: "d-test" });
    retry = () => ({ retried: 1 });
    installApi(client, {
      "GET /notifications/settings": () => settings,
      "PUT /notifications/settings": ({ body }) => putSettings(body),
      "POST /notifications/test-email": () => testEmail(),
      "GET /notifications/deliveries": () => page(deliveries),
      "GET /notifications/deliveries/summary": () => ({
        counts: { pending: 1, sending: 0, sent: 120, dead: 2, skipped: 15 },
        sentToday: 42,
        dailyEmailLimit: 500,
        oldestPendingAt: "2026-09-20T08:00:00Z",
      }),
      "GET /notifications/deliveries/recipient-issues": () => issues,
      "POST /notifications/deliveries/retry": ({ body }) => retry(body),
      "POST /notifications/deliveries/recipient-issues/user-2/reset": () => undefined,
      "GET /notifications/deliveries/d1": () => deliveryById.d1,
      "GET /notifications/deliveries/d-test": () => deliveryById["d-test"],
      "GET /organization/members": () => MEMBERS,
    });
    signIn();
  });
  afterEach(() => {
    vi.useRealTimers();
    clearSession();
  });

  it("shows the switches, sender, reply-to and today's e-mail counter", async () => {
    render();
    expect(await screen.findByLabelText("Gönderen adı")).toHaveValue("Acme A.Ş.");
    expect(screen.getByLabelText("Yanıt adresi")).toHaveValue("destek@acme.com.tr");
    expect(screen.getByRole("switch", { name: "E-posta bildirimleri" })).toBeChecked();
    expect(screen.getByRole("switch", { name: "SMS bildirimleri" })).not.toBeChecked();
    expect(screen.getByTestId("email-usage")).toHaveTextContent("Bugün 42 e-posta gönderildi (günlük sınır: 500)");
    expect(screen.getByTestId("effective-email")).toHaveTextContent("Etkin");
    expect(screen.getByTestId("effective-sms")).toHaveTextContent("Etkin değil");
  });

  it("explains a channel the server is not configured for and disables its switch", async () => {
    settings = tenantSettings({
      email: { enabled: true, availableByPlatform: false, availableByPlan: true, effective: false },
    });
    render();
    expect(await screen.findByTestId("reason-platform-email")).toHaveTextContent("sunucuda yapılandırılmamış");
    expect(screen.getByRole("switch", { name: "E-posta bildirimleri" })).toBeDisabled();
    // The test e-mail cannot work either.
    expect(screen.getByRole("button", { name: "Test e-postası gönder" })).toBeDisabled();
  });

  it("explains a channel that is not in the plan and links to Plan ve kullanım", async () => {
    settings = tenantSettings({
      email: { enabled: true, availableByPlatform: true, availableByPlan: false, effective: false },
    });
    render();
    const reason = await screen.findByTestId("reason-plan-email");
    expect(reason).toHaveTextContent("planınıza dahil değil");
    expect(within(reason).getByRole("link", { name: "Plan ve kullanım" })).toHaveAttribute("href", "/app/settings/plan");
    expect(screen.getByRole("switch", { name: "E-posta bildirimleri" })).toBeDisabled();
  });

  it("shows the usage without a limit when the plan sets none", async () => {
    settings = tenantSettings({ usage: { emailsToday: 7 } });
    render();
    expect(await screen.findByTestId("email-usage")).toHaveTextContent("Bugün 7 e-posta gönderildi");
    expect(screen.getByTestId("email-usage")).not.toHaveTextContent("sınır");
  });

  it("keeps Save disabled until something changes and sends a full replacement", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByLabelText("Gönderen adı");
    const save = screen.getByRole("button", { name: "Kaydet" });
    expect(save).toBeDisabled();

    await user.click(screen.getByRole("switch", { name: "E-posta bildirimleri" }));
    await user.clear(screen.getByLabelText("Gönderen adı"));
    await user.type(screen.getByLabelText("Gönderen adı"), "  Acme Destek  ");
    await user.click(save);

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put).toHaveBeenCalledWith("/notifications/settings", {
      emailEnabled: false,
      smsEnabled: false,
      senderName: "Acme Destek",
      replyTo: "destek@acme.com.tr",
    });
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
  });

  it("leaves an emptied reply-to out (full replacement clears it) and does not pin the organization name as sender", async () => {
    setMe({
      ...platformMe([MANAGE], { subscription: subscription() }),
      organization: { id: "o1", name: "Acme A.Ş.", slug: "acme", defaultLocale: "tr", timeZone: "Europe/Istanbul" },
    });
    const user = userEvent.setup();
    render();
    await user.clear(await screen.findByLabelText("Yanıt adresi"));
    await user.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]?.[1]).toEqual({ emailEnabled: true, smsEnabled: false });
  });

  it.each([
    ["Gönderen adı", "Acme <b>", "Denetim karakteri veya < > \" içeremez"],
    ["Gönderen adı", 'Acme "Destek"', "Denetim karakteri veya < > \" içeremez"],
    ["Gönderen adı", "A".repeat(101), "En fazla 100 karakter olabilir"],
    ["Yanıt adresi", "yok-at-yok", "Geçerli bir e-posta adresi girin"],
    ["Yanıt adresi", `${"a".repeat(250)}@x.co`, "En fazla 254 karakter olabilir"],
  ])("rejects %s = %s on the client without calling the server", async (label, value, message) => {
    const user = userEvent.setup();
    render();
    const input = await screen.findByLabelText(label);
    await user.clear(input);
    await user.click(input);
    await user.paste(value);
    await user.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(client.put).not.toHaveBeenCalled();
  });

  it("puts a server field error on its field", async () => {
    putSettings = () =>
      problem(400, { status: 400, title: "validation", code: "validation", errors: { replyTo: ["Adres kabul edilmedi"] } }) as unknown as never;
    const user = userEvent.setup();
    render();
    const input = await screen.findByLabelText("Yanıt adresi");
    await user.clear(input);
    await user.type(input, "baska@acme.com.tr");
    await user.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(await screen.findByText("Adres kabul edilmedi")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("toasts any other server error (for example a suspended tenant)", async () => {
    putSettings = () => problem(403, { status: 403, title: "suspended", code: "tenant.suspended" }) as unknown as never;
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("switch", { name: "E-posta bildirimleri" }));
    await user.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
  });

  it("hides every write action in read-only mode", async () => {
    signIn({ readOnly: true });
    render();
    expect(await screen.findByLabelText("Gönderen adı")).toHaveAttribute("readonly");
    expect(screen.getByTestId("admin-readonly")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Kaydet" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Test e-postası gönder" })).not.toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "E-posta bildirimleri" })).toBeDisabled();
  });

  describe("test e-mail", () => {
    it("sends, follows the delivery and reports success", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      deliveryById["d-test"] = delivery("d-test", { status: "pending", kind: "system.test_email" });
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      render();
      await user.click(await screen.findByRole("button", { name: "Test e-postası gönder" }));

      expect(await screen.findByText("Test e-postası gönderiliyor...")).toBeInTheDocument();
      expect(calls("post", "/notifications/test-email")).toHaveLength(1);
      // The body carries no address: the server sends to the caller only.
      expect(client.post.mock.calls.find(([url]) => url === "/notifications/test-email")?.[1]).toBeUndefined();

      deliveryById["d-test"] = delivery("d-test", { status: "sent", kind: "system.test_email" });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2_100);
      });
      expect(await screen.findByText("Test e-postası gönderildi. Gelen kutunuzu kontrol edin.")).toBeInTheDocument();
      // Final status: polling stops.
      const reads = calls("get", "/notifications/deliveries/d-test").length;
      await act(async () => {
        await vi.advanceTimersByTimeAsync(10_000);
      });
      expect(calls("get", "/notifications/deliveries/d-test").length).toBe(reads);
    });

    it("reports a failed delivery with its error code text", async () => {
      deliveryById["d-test"] = delivery("d-test", { status: "dead", errorCode: "authFailed", kind: "system.test_email" });
      const user = userEvent.setup();
      render();
      await user.click(await screen.findByRole("button", { name: "Test e-postası gönder" }));
      expect(await screen.findByText("Test e-postası gönderilemedi: Sunucu kimlik doğrulaması başarısız")).toBeInTheDocument();
    });

    it("reports a skipped delivery with its reason", async () => {
      deliveryById["d-test"] = delivery("d-test", { status: "skipped", skipReason: "noAddress", kind: "system.test_email" });
      const user = userEvent.setup();
      render();
      await user.click(await screen.findByRole("button", { name: "Test e-postası gönder" }));
      expect(await screen.findByText("Test e-postası atlandı: Adres yok")).toBeInTheDocument();
    });

    it("explains 409 notification.email_not_configured", async () => {
      testEmail = () => problem(409, { status: 409, title: "x", code: "notification.email_not_configured" }) as unknown as never;
      const user = userEvent.setup();
      render();
      await user.click(await screen.findByRole("button", { name: "Test e-postası gönder" }));
      expect(await within(screen.getByTestId("test-email-result")).findByRole("alert")).toHaveTextContent("E-posta sunucuda yapılandırılmamış");
      expect(toastApiError).not.toHaveBeenCalled();
    });

    it("counts down after a 429 (Retry-After) and blocks the button meanwhile", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      const rateLimit = problem(429, { status: 429, title: "x", code: "general.rate_limit_exceeded" });
      (rateLimit.response as { headers: Record<string, string> }).headers = { "retry-after": "3" };
      testEmail = () => rateLimit as unknown as never;
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      render();
      await user.click(await screen.findByRole("button", { name: "Test e-postası gönder" }));

      expect(await screen.findByRole("button", { name: "Tekrar denemek için 3 sn" })).toBeDisabled();
      expect(within(screen.getByTestId("test-email-result")).getByRole("alert")).toHaveTextContent("3 saniye sonra tekrar deneyin");
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1_100);
      });
      expect(await screen.findByRole("button", { name: "Tekrar denemek için 2 sn" })).toBeDisabled();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2_200);
      });
      await waitFor(() => expect(screen.getByRole("button", { name: "Test e-postası gönder" })).toBeEnabled());
      expect(within(screen.getByTestId("test-email-result")).queryByRole("alert")).not.toBeInTheDocument();
    });

    it("falls back to a 60 second wait when the 429 has no Retry-After", async () => {
      testEmail = () => problem(429, { status: 429, title: "x", code: "general.rate_limit_exceeded" }) as unknown as never;
      const user = userEvent.setup();
      render();
      await user.click(await screen.findByRole("button", { name: "Test e-postası gönder" }));
      expect(await screen.findByRole("button", { name: /Tekrar denemek için (59|60) sn/ })).toBeDisabled();
    });
  });
});

describe("NotificationSettingsPage - delivery log", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    settings = tenantSettings();
    issues = [];
    deliveryById = {};
    retry = () => ({ retried: 2 });
    deliveries = [
      delivery("d1", { status: "dead", attempts: 8, errorCode: "temporaryFailure" }),
      delivery("d2", { status: "dead", attempts: 8, errorCode: "recipientRejected", kind: "case.assigned" }),
      delivery("d3", { status: "sent" }),
      delivery("d4", { status: "skipped", skipReason: "preferenceOff", kind: "approval.requested" }),
    ];
    installApi(client, {
      "GET /notifications/settings": () => settings,
      "GET /notifications/deliveries": () => page(deliveries),
      "GET /notifications/deliveries/summary": () => ({
        counts: { pending: 1, sending: 0, sent: 120, dead: 2, skipped: 15 },
        sentToday: 42,
        dailyEmailLimit: 500,
        oldestPendingAt: "2026-09-20T08:00:00Z",
      }),
      "GET /notifications/deliveries/recipient-issues": () => issues,
      "POST /notifications/deliveries/retry": ({ body }) => retry(body),
      "GET /notifications/deliveries/d1": () => ({
        ...delivery("d1", { status: "dead", attempts: 8, errorCode: "temporaryFailure" }),
      }),
      "GET /organization/members": () => MEMBERS,
    });
    signIn();
  });
  afterEach(() => clearSession());

  it("shows the summary cards and the rows with status badges, masked address and reasons", async () => {
    render("/app/settings/notifications?tab=deliveries");
    const summary = await screen.findByTestId("delivery-summary");
    expect(within(summary).getByTestId("summary-sent")).toHaveTextContent("120");
    expect(within(summary).getByTestId("summary-dead")).toHaveTextContent("2");
    expect(within(summary).getByTestId("summary-sentToday")).toHaveTextContent("42");
    expect(within(summary).getByTestId("summary-sentToday")).toHaveTextContent("500");

    const rows = await screen.findAllByRole("row");
    const deadRow = rows.find((row) => within(row).queryByText("Geçici hata (denemeler tükendi)")) as HTMLElement;
    expect(within(deadRow).getByText("Başarısız")).toBeInTheDocument();
    expect(within(deadRow).getAllByText("m***@acme.com.tr").length).toBe(1);
    expect(within(deadRow).getByText("Mert Kaya")).toBeInTheDocument();
    const skippedRow = rows.find((row) => within(row).queryByText("Kullanıcı tercihi kapalı")) as HTMLElement;
    expect(within(skippedRow).getByText("Atlandı")).toBeInTheDocument();
  });

  it("keeps channel, status, kind, recipient and day range in the URL and sends them", async () => {
    const user = userEvent.setup();
    render("/app/settings/notifications?tab=deliveries");
    await screen.findByTestId("delivery-summary");
    expect(lastListParams()).toEqual({ page: 1, pageSize: 25 });

    await user.click(screen.getByRole("combobox", { name: "Durum" }));
    await user.click(await screen.findByRole("option", { name: "Başarısız" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "dead" }));
    expect(screen.getByTestId("location")).toHaveTextContent("status=dead");

    await user.click(screen.getByRole("combobox", { name: "Kanal" }));
    await user.click(await screen.findByRole("option", { name: "E-posta" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "dead", channel: "email" }));

    await user.click(screen.getByRole("combobox", { name: "Tür" }));
    await user.click(await screen.findByRole("option", { name: "SLA aşıldı" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "dead", channel: "email", kind: "case.sla_breached" })
    );

    await user.click(screen.getByRole("combobox", { name: "Alıcı" }));
    await user.click(await screen.findByRole("option", { name: "Grace Hopper" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ recipientUserId: "user-2" }));

    await user.type(screen.getByLabelText("Başlangıç"), "2026-09-01");
    await user.type(screen.getByLabelText("Bitiş"), "2026-09-20");
    await waitFor(() => expect(lastListParams()).toMatchObject({ from: "2026-09-01", to: "2026-09-20" }));
    // The summary follows the day range.
    await waitFor(() =>
      expect(client.get).toHaveBeenCalledWith("/notifications/deliveries/summary", { params: { from: "2026-09-01", to: "2026-09-20" } })
    );

    await user.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25 }));
  });

  it("restores the filters from the URL", async () => {
    render("/app/settings/notifications?tab=deliveries&status=dead&channel=sms&from=2026-09-01&page=2");
    await screen.findByTestId("delivery-summary");
    expect(lastListParams()).toEqual({ page: 2, pageSize: 25, status: "dead", channel: "sms", from: "2026-09-01" });
  });

  it("offers a checkbox only on dead rows and retries exactly the selected dead rows", async () => {
    const user = userEvent.setup();
    render("/app/settings/notifications?tab=deliveries");
    await screen.findByTestId("delivery-summary");
    await screen.findAllByText("Mert Kaya");

    // 2 dead rows + the "select all" checkbox: sent and skipped rows have none.
    const boxes = screen.getAllByRole("checkbox");
    expect(boxes).toHaveLength(3);
    const retryButton = screen.getByRole("button", { name: "Yeniden dene (0)" });
    expect(retryButton).toBeDisabled();

    await user.click(screen.getByRole("checkbox", { name: "Seç: SLA aşıldı" }));
    expect(screen.getByRole("button", { name: "Yeniden dene (1)" })).toBeEnabled();
    await user.click(screen.getByRole("checkbox", { name: "Seç: Talep atandı" }));
    await user.click(screen.getByRole("button", { name: "Yeniden dene (2)" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/deliveries/retry", { ids: ["d1", "d2"] }));
    await waitFor(() => expect(toast).toHaveBeenCalledWith(expect.objectContaining({ description: "2 teslimat yeniden kuyruğa alındı" })));
    // The selection is cleared.
    expect(screen.getByRole("button", { name: "Yeniden dene (0)" })).toBeDisabled();
  });

  it("selects every dead row of the page with 'select all' (and only those)", async () => {
    const user = userEvent.setup();
    render("/app/settings/notifications?tab=deliveries");
    await screen.findAllByText("Mert Kaya");
    await user.click(screen.getByRole("checkbox", { name: "Sayfadaki tümünü seç" }));
    await user.click(screen.getByRole("button", { name: "Yeniden dene (2)" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/deliveries/retry", { ids: ["d1", "d2"] }));
  });

  it("words 409 notification.delivery_not_retryable", async () => {
    retry = () => problem(409, { status: 409, title: "x", code: "notification.delivery_not_retryable" }) as unknown as never;
    const user = userEvent.setup();
    render("/app/settings/notifications?tab=deliveries");
    await screen.findAllByText("Mert Kaya");
    await user.click(screen.getByRole("checkbox", { name: "Sayfadaki tümünü seç" }));
    await user.click(screen.getByRole("button", { name: "Yeniden dene (2)" }));
    await waitFor(() =>
      expect(toast).toHaveBeenCalledWith(
        expect.objectContaining({ variant: "destructive", description: "Yeniden denenebilecek başarısız teslimat yok." })
      )
    );
  });

  it("has no retry button or checkboxes without full access (read-only tenant)", async () => {
    signIn({ readOnly: true });
    render("/app/settings/notifications?tab=deliveries");
    await screen.findAllByText("Mert Kaya");
    expect(screen.queryByRole("button", { name: /Yeniden dene/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
  });

  it("opens the payload viewer: error code and JSON, without personal data", async () => {
    const user = userEvent.setup();
    render("/app/settings/notifications?tab=deliveries");
    await screen.findAllByText("Mert Kaya");
    await user.click(screen.getAllByRole("button", { name: "Teslimat ayrıntısı: SLA aşıldı" })[0] as HTMLElement);

    const payload = await screen.findByTestId("delivery-payload");
    const json = JSON.parse(payload.textContent ?? "{}") as Record<string, unknown>;
    expect(json).toMatchObject({ id: "d1", status: "dead", errorCode: "temporaryFailure", attempts: 8 });
    expect(payload.textContent).not.toContain("Mert Kaya");
    expect(payload.textContent).not.toContain("m***@acme.com.tr");
    expect(json).not.toHaveProperty("recipientName");
    expect(json).not.toHaveProperty("addressMasked");
    expect(json).not.toHaveProperty("recipientUserId");
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Geçici hata (denemeler tükendi)")).toBeInTheDocument();
    expect(within(dialog).queryByText("Mert Kaya")).not.toBeInTheDocument();
  });

  it("shows an empty state and a retryable error", async () => {
    deliveries = [];
    render("/app/settings/notifications?tab=deliveries");
    expect(await screen.findByText("Teslimat kaydı yok")).toBeInTheDocument();
  });
});

describe("NotificationSettingsPage - recipient issues and tabs", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    settings = tenantSettings();
    issues = [
      { userId: "user-2", userName: "Mert Kaya", channel: "email", hardFailures: 3, invalidSince: "2026-09-19T10:00:00Z", lastFailureAt: "2026-09-19T10:00:00Z" },
    ];
    installApi(client, {
      "GET /notifications/settings": () => settings,
      "GET /notifications/deliveries/recipient-issues": () => issues,
      "POST /notifications/deliveries/recipient-issues/user-2/reset": () => undefined,
    });
    signIn();
  });
  afterEach(() => clearSession());

  it("lists the problem recipients and resets one (channel sent as a query parameter)", async () => {
    const user = userEvent.setup();
    render("/app/settings/notifications?tab=issues");
    const row = await screen.findByTestId("issue-row");
    expect(within(row).getByText("Mert Kaya")).toBeInTheDocument();
    expect(within(row).getByText("3")).toBeInTheDocument();

    await user.click(within(row).getByRole("button", { name: "Sıfırla: Mert Kaya" }));
    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/notifications/deliveries/recipient-issues/user-2/reset", undefined, {
        params: { channel: "email" },
      })
    );
  });

  it("shows an empty state", async () => {
    issues = [];
    render("/app/settings/notifications?tab=issues");
    expect(await screen.findByText("Sorunlu alıcı yok")).toBeInTheDocument();
  });

  it("has no reset button in read-only mode", async () => {
    signIn({ readOnly: true });
    render("/app/settings/notifications?tab=issues");
    await screen.findByTestId("issue-row");
    expect(screen.queryByRole("button", { name: /Sıfırla/ })).not.toBeInTheDocument();
  });

  it("switches tabs through the URL and drops the other tab's filters", async () => {
    const user = userEvent.setup();
    installApi(client, {
      "GET /notifications/settings": () => settings,
      "GET /notifications/deliveries": () => page([]),
      "GET /notifications/deliveries/summary": () => ({ counts: { pending: 0, sending: 0, sent: 0, dead: 0, skipped: 0 }, sentToday: 0 }),
      "GET /notifications/deliveries/recipient-issues": () => [],
      "GET /organization/members": () => MEMBERS,
    });
    render("/app/settings/notifications?tab=deliveries&status=dead&page=3");
    await screen.findByTestId("delivery-summary");

    await user.click(screen.getByRole("tab", { name: "Alıcı sorunları" }));
    expect(screen.getByTestId("location")).toHaveTextContent("?tab=issues");
    expect(screen.getByTestId("location")).not.toHaveTextContent("status=dead");

    await user.click(screen.getByRole("tab", { name: "Ayarlar" }));
    expect(screen.getByTestId("location").textContent).toBe("/app/settings/notifications");
    expect(await screen.findByLabelText("Gönderen adı")).toBeInTheDocument();
  });

  it("an unknown tab falls back to the settings tab", async () => {
    render("/app/settings/notifications?tab=bogus");
    expect(await screen.findByLabelText("Gönderen adı")).toBeInTheDocument();
  });
});
