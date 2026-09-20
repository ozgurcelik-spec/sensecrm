import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { preferences } from "@/test/notifications";
import { platformMe, setMe, subscription } from "@/test/platform";
import type { NotificationPreferences } from "@/types";
import NotificationPreferencesPage from "./notifications-preferences";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

let current: NotificationPreferences;
let putHandler: (body: unknown) => unknown;

const toggle = (name: string) => screen.getByRole("switch", { name });
const putBody = () => client.put.mock.calls.at(-1)?.[1] as { items: { kind: string; channel: string; enabled: boolean }[] };

function render() {
  return renderWithProviders(<NotificationPreferencesPage />);
}

describe("NotificationPreferencesPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    current = preferences();
    putHandler = () => undefined;
    installApi(client, {
      "GET /notifications/preferences": () => current,
      "PUT /notifications/preferences": ({ body }) => putHandler(body),
    });
    setPermissions([]);
  });
  afterEach(() => clearSession());

  it("shows the kinds grouped, with in-app and e-mail columns", async () => {
    render();
    const matrix = await screen.findByTestId("preferences-matrix");
    expect(within(matrix).getByText("Onay istendi")).toBeInTheDocument();
    for (const group of ["Onaylar", "Servis", "Aktiviteler", "Hesap ve plan"]) {
      expect(within(matrix).getByText(group)).toBeInTheDocument();
    }
    expect(within(matrix).getByRole("columnheader", { name: /Uygulama içi/ })).toBeInTheDocument();
    expect(within(matrix).getByRole("columnheader", { name: /E-posta/ })).toBeInTheDocument();
    expect(toggle("Onay istendi - E-posta")).toBeChecked();
  });

  it("locks the mandatory cells (on, disabled, marked) and leaves the others editable", async () => {
    render();
    await screen.findByTestId("preferences-matrix");
    for (const channel of ["Uygulama içi", "E-posta"]) {
      const locked = toggle(`Organizasyon askıya alındı - ${channel} (zorunlu)`);
      expect(locked).toBeChecked();
      expect(locked).toBeDisabled();
    }
    expect(within(screen.getByTestId("pref-row-tenant.suspended")).getByText("Zorunlu")).toBeInTheDocument();
    expect(toggle("Onay istendi - E-posta")).toBeEnabled();
  });

  it("shows a dash instead of a switch for a channel the kind does not use", async () => {
    render();
    const row = await screen.findByTestId("pref-row-activity.daily_agenda");
    expect(within(row).getAllByRole("switch")).toHaveLength(1);
    expect(within(row).getByLabelText("Bu tür bu kanalı kullanmaz")).toBeInTheDocument();
  });

  it("hides the SMS column while the platform offers no SMS", async () => {
    render();
    await screen.findByTestId("preferences-matrix");
    expect(screen.queryByRole("columnheader", { name: /SMS/ })).not.toBeInTheDocument();
  });

  it("shows the SMS column when it is available", async () => {
    current = preferences({
      channels: { inApp: { available: true }, email: { available: true }, sms: { available: true } },
    });
    render();
    expect(await screen.findByRole("columnheader", { name: /SMS/ })).toBeInTheDocument();
    // The mandatory kind supports SMS but it is not mandatory: a normal, editable switch.
    expect(toggle("Organizasyon askıya alındı - SMS")).toBeEnabled();
  });

  it.each([
    ["platform", "Sunucuda yapılandırılmamış"],
    ["plan", "Planınıza dahil değil"],
    ["tenant", "Yöneticiniz kapattı"],
  ] as const)("disables the e-mail column with the reason %s", async (reason, text) => {
    current = preferences({
      channels: { inApp: { available: true }, email: { available: false, reason }, sms: { available: false, reason: "platform" } },
    });
    render();
    expect(await screen.findByTestId("unavailable-email")).toHaveTextContent(text);
    expect(toggle("Onay istendi - E-posta")).toBeDisabled();
    // In-app stays usable.
    expect(toggle("Onay istendi - Uygulama içi")).toBeEnabled();
  });

  it("hides the group of a module the plan switches off", async () => {
    setMe(platformMe([], { subscription: subscription({ modules: { workflows: false, commerce: true, service: true, marketing: true } }) }));
    render();
    const matrix = await screen.findByTestId("preferences-matrix");
    expect(within(matrix).queryByText("Onay istendi")).not.toBeInTheDocument();
    expect(within(matrix).queryByText("Onaylar")).not.toBeInTheDocument();
    expect(within(matrix).getByText("Talep atandı")).toBeInTheDocument();
  });

  it("keeps Save disabled until something changes, then sends only the changed cells", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("preferences-matrix");
    const save = screen.getByRole("button", { name: "Kaydet" });
    expect(save).toBeDisabled();

    await user.click(toggle("Onay istendi - E-posta"));
    await user.click(toggle("Talep atandı - Uygulama içi"));
    expect(save).toBeEnabled();
    await user.click(save);

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]?.[0]).toBe("/notifications/preferences");
    expect(putBody().items).toEqual(
      expect.arrayContaining([
        { kind: "approval.requested", channel: "email", enabled: false },
        { kind: "case.assigned", channel: "inApp", enabled: false },
      ])
    );
    expect(putBody().items).toHaveLength(2);
  });

  it("does not send a cell that was toggled back to its stored value", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("preferences-matrix");
    await user.click(toggle("Onay istendi - E-posta"));
    await user.click(toggle("Onay istendi - E-posta"));
    expect(screen.getByRole("button", { name: "Kaydet" })).toBeDisabled();
  });

  it("'E-postaların tümünü kapat' turns off only the non-mandatory e-mail cells", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByTestId("preferences-matrix");
    await user.click(screen.getByRole("button", { name: "E-postaların tümünü kapat" }));

    expect(toggle("Onay istendi - E-posta")).not.toBeChecked();
    expect(toggle("Talep atandı - E-posta")).not.toBeChecked();
    // The mandatory e-mail stays on and locked; in-app cells are untouched.
    expect(toggle("Organizasyon askıya alındı - E-posta (zorunlu)")).toBeChecked();
    expect(toggle("Onay istendi - Uygulama içi")).toBeChecked();

    await user.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.put).toHaveBeenCalled());
    expect(putBody().items).toEqual([
      { kind: "approval.requested", channel: "email", enabled: false },
      { kind: "case.assigned", channel: "email", enabled: false },
    ]);
  });

  it("'Varsayılana dön' restores the defaults of the unlocked cells", async () => {
    current = preferences();
    // The user turned e-mail of the approvals off and in-app of the case kind off earlier.
    const approvals = current.kinds[0]!;
    approvals.channels.email = { enabled: false, default: true, locked: false };
    const cases = current.kinds[1]!;
    cases.channels.inApp = { enabled: false, default: true, locked: false };
    const user = userEvent.setup();
    render();
    await screen.findByTestId("preferences-matrix");
    expect(toggle("Onay istendi - E-posta")).not.toBeChecked();

    await user.click(screen.getByRole("button", { name: "Varsayılana dön" }));
    expect(toggle("Onay istendi - E-posta")).toBeChecked();
    expect(toggle("Talep atandı - Uygulama içi")).toBeChecked();

    await user.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.put).toHaveBeenCalled());
    expect(putBody().items).toEqual(
      expect.arrayContaining([
        { kind: "approval.requested", channel: "email", enabled: true },
        { kind: "case.assigned", channel: "inApp", enabled: true },
      ])
    );
    expect(putBody().items).toHaveLength(2);
  });

  it("maps 422 notification.mandatory_preference to a message naming the kind and channel", async () => {
    putHandler = () =>
      problem(422, {
        status: 422,
        title: "mandatory",
        code: "notification.mandatory_preference",
        args: { kind: "tenant.suspended", channel: "email" },
      }) as unknown as never;
    const user = userEvent.setup();
    render();
    await screen.findByTestId("preferences-matrix");
    await user.click(toggle("Onay istendi - E-posta"));
    await user.click(screen.getByRole("button", { name: "Kaydet" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent('"Organizasyon askıya alındı" bildirimi zorunludur; E-posta kanalı kapatılamaz.');
    // The screen reloads the server matrix (the refused change is dropped).
    await waitFor(() => expect(client.get.mock.calls.filter(([url]) => url === "/notifications/preferences").length).toBeGreaterThan(1));
    expect(toggle("Onay istendi - E-posta")).toBeChecked();
  });

  it("maps a 400 validation answer to its field messages and keeps the draft", async () => {
    putHandler = () =>
      problem(400, {
        status: 400,
        title: "validation",
        code: "validation",
        errors: { "items[0].kind": ["Bilinmeyen bildirim türü"] },
      }) as unknown as never;
    const user = userEvent.setup();
    render();
    await screen.findByTestId("preferences-matrix");
    await user.click(toggle("Onay istendi - E-posta"));
    await user.click(screen.getByRole("button", { name: "Kaydet" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Lütfen işaretli alanları düzeltin");
    expect(alert).toHaveTextContent("items[0].kind: Bilinmeyen bildirim türü");
    expect(toggle("Onay istendi - E-posta")).not.toBeChecked();
  });

  it("warns when the e-mail address cannot be reached", async () => {
    current = preferences({
      channels: { inApp: { available: true }, email: { available: true, addressIssue: true }, sms: { available: false } },
    });
    render();
    expect(await screen.findByTestId("address-issue")).toHaveTextContent("E-posta adresinize ulaşılamıyor");
  });

  it("shows no warning without an address issue", async () => {
    render();
    await screen.findByTestId("preferences-matrix");
    expect(screen.queryByTestId("address-issue")).not.toBeInTheDocument();
  });

  it("is read-only while the tenant is read-only (the server refuses the write)", async () => {
    setMe(platformMe([], { subscription: subscription({ accessLevel: "readOnly" }) }));
    render();
    await screen.findByTestId("preferences-matrix");
    expect(screen.getByTestId("prefs-readonly")).toBeInTheDocument();
    expect(toggle("Onay istendi - E-posta")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Kaydet" })).toBeDisabled();
  });

  it("shows a retryable error when the matrix cannot be loaded", async () => {
    installApi(client, { "GET /notifications/preferences": () => problem(500, { status: 500, title: "boom" }) as unknown as never });
    render();
    expect(await screen.findByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });
});
