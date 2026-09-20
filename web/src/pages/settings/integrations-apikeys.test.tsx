import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toast, toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, LocationDisplay, page, problem, type MockClient } from "@/test/crm";
import { apiKey, integrationsStatus } from "@/test/integrations";
import { platformMe, setMe, subscription } from "@/test/platform";
import { PERMISSIONS, type ApiKey, type IntegrationsStatus } from "@/types";
import IntegrationsPage from "./integrations";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const DAY = 86_400_000;
const RAW_KEY = `crmk_${"0192f0a10000700080000000000000aa"}_a1b2c3d4_${"Zm9vYmFyLXNlY3JldC12YWx1ZS0xMjM0NTY3ODkwYWJj"}`;

/** The creator: integrations admin who holds leads (read + write) and accounts (read) plus approvals and org rights the key may never carry. */
const CREATOR = [
  PERMISSIONS.orgIntegrationsManage,
  PERMISSIONS.orgSettingsManage,
  PERMISSIONS.crmLeadsRead,
  PERMISSIONS.crmLeadsWrite,
  PERMISSIONS.crmAccountsRead,
  PERMISSIONS.crmApprovalsDecide,
];

let keys: ApiKey[];
let status: IntegrationsStatus;
let createHandler: (body: unknown) => unknown;
let usage: { day: string; requests: number; errors: number; throttled: number }[];
let deleteHandler: () => unknown;

const listParams = () => client.get.mock.calls.filter(([url]) => url === "/integrations/api-keys").at(-1)?.[1]?.params;
const posted = (url: string) => client.post.mock.calls.filter(([called]) => called === url);

function render(route = "/app/settings/integrations?tab=apiKeys") {
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
  keys = [
    apiKey("k1", { name: "Raporlama", expiresAt: new Date(Date.now() + 200 * DAY).toISOString(), lastUsedAt: "2026-09-20T08:00:00Z", lastUsedIp: "203.0.113.7", scopes: ["crm.leads.read", "crm.accounts.read", "crm.deals.read"] }),
    apiKey("k2", { name: "Yakında bitiyor", expiresAt: new Date(Date.now() + 5 * DAY).toISOString() }),
    apiKey("k3", { name: "Eski", status: "expired", expiresAt: new Date(Date.now() - 2 * DAY).toISOString() }),
    apiKey("k4", { name: "Devre dışı anahtar", status: "revoked", revokedAt: "2026-09-10T10:00:00Z", revokedByName: "Ada Lovelace" }),
  ];
  status = integrationsStatus();
  createHandler = () => ({ ...apiKey("k9", { name: "Yeni anahtar" }), key: RAW_KEY });
  usage = [
    { day: "2026-09-18", requests: 120, errors: 3, throttled: 0 },
    { day: "2026-09-19", requests: 80, errors: 1, throttled: 2 },
  ];
  deleteHandler = () => undefined;
  installApi(client, {
    "GET /integrations/status": () => status,
    "GET /integrations/api-keys": () => page(keys),
    "POST /integrations/api-keys": ({ body }) => createHandler(body),
    "PATCH /integrations/api-keys/k1": () => undefined,
    "POST /integrations/api-keys/k1/revoke": () => undefined,
    "DELETE /integrations/api-keys/k3": () => deleteHandler(),
    "DELETE /integrations/api-keys/k4": () => deleteHandler(),
    "GET /integrations/api-keys/k1/usage": () => ({ items: usage }),
  });
  setMe(platformMe(CREATOR, { subscription: subscription() }));
});
afterEach(() => clearSession());

describe("API keys tab - list", () => {
  it("shows name, prefix, scope chips, last use with IP, status and colours the expiry", async () => {
    render();
    const row = (await screen.findByText("Raporlama")).closest("tr") as HTMLElement;
    expect(within(row).getByText("crmk_a1b2c3d1")).toBeInTheDocument();
    expect(within(row).getByText("crm.leads.read")).toBeInTheDocument();
    expect(within(row).getByText("+1")).toBeInTheDocument();
    expect(within(row).getByText("203.0.113.7")).toBeInTheDocument();
    expect(within(row).getByTestId("key-status-active")).toHaveTextContent("Etkin");
    expect(screen.getByTestId("expiry-k1")).toHaveAttribute("data-level", "ok");
    expect(screen.getByTestId("expiry-k2")).toHaveAttribute("data-level", "soon");
    expect(screen.getByTestId("expiry-k3")).toHaveAttribute("data-level", "expired");
    expect(screen.getByTestId("key-status-expired")).toHaveTextContent("Süresi dolmuş");
    expect(screen.getByTestId("key-status-revoked")).toHaveTextContent("İptal edilmiş");
    // Never used keys say so.
    expect(within((await screen.findByText("Yakında bitiyor")).closest("tr") as HTMLElement).getByText("Hiç kullanılmadı")).toBeInTheDocument();
  });

  it("keeps search and status in the URL and sends them", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("Raporlama");
    expect(listParams()).toEqual({ page: 1, pageSize: 25 });
    await user.click(screen.getByRole("combobox", { name: "Durum" }));
    await user.click(await screen.findByRole("option", { name: "İptal edilmiş" }));
    await waitFor(() => expect(listParams()).toEqual({ page: 1, pageSize: 25, status: "revoked" }));
    expect(screen.getByTestId("location")).toHaveTextContent("status=revoked");
    await user.type(screen.getByRole("searchbox"), "rapor");
    await waitFor(() => expect(listParams()).toMatchObject({ q: "rapor", status: "revoked" }));
  });

  it("shows the empty state and the plan limit", async () => {
    keys = [];
    status = integrationsStatus({ limits: { maxApiKeys: 2 }, usage: { webhooks: 0, apiKeys: 2 } });
    render();
    expect(await screen.findByText("Henüz API anahtarı yok")).toBeInTheDocument();
    expect(await screen.findByTestId("apikey-limit")).toHaveTextContent("Plan limitine ulaşıldı: API anahtarı 2/2");
    expect(screen.getByRole("button", { name: "Yeni API anahtarı" })).toBeDisabled();
  });
});

describe("API keys tab - create", () => {
  async function openCreate() {
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("button", { name: "Yeni API anahtarı" }));
    await screen.findByTestId("scope-picker");
    return user;
  }
  const nameField = () => screen.getByRole("textbox", { name: /^Ad\s*\*?$/ });

  it("lists only crm.* scopes: no approval decision and no org.* permission, and locks what the creator lacks", async () => {
    await openCreate();
    const picker = screen.getByTestId("scope-picker");
    expect(within(picker).queryByTestId("scope-crm.approvals.decide")).not.toBeInTheDocument();
    expect(picker.querySelector('[data-testid^="scope-org."]')).toBeNull();
    for (const testId of within(picker).getAllByTestId(/^scope-/)) {
      expect(testId.getAttribute("data-testid")).toMatch(/^scope-crm\./);
    }
    // Held: enabled. Not held: locked.
    expect(within(picker).getByRole("checkbox", { name: "Potansiyelleri görüntüleme" })).toBeEnabled();
    expect(within(picker).getByRole("checkbox", { name: "Potansiyel oluşturma/düzenleme" })).toBeEnabled();
    expect(within(picker).getByRole("checkbox", { name: "Fırsatları görüntüleme" })).toBeDisabled();
    expect(within(picker).getByRole("checkbox", { name: "Müşteri oluşturma/düzenleme" })).toBeDisabled();
  });

  it("the read-only preset and the group toggles only touch scopes the creator holds", async () => {
    const user = await openCreate();
    const picker = screen.getByTestId("scope-picker");
    await user.click(within(picker).getByRole("button", { name: "Salt okuma" }));
    expect(within(picker).getByTestId("scope-crm.leads.read")).toBeChecked();
    expect(within(picker).getByTestId("scope-crm.accounts.read")).toBeChecked();
    expect(within(picker).getByTestId("scope-crm.leads.write")).not.toBeChecked();
    expect(within(picker).getByTestId("scope-crm.deals.read")).not.toBeChecked();

    await user.click(within(picker).getByRole("checkbox", { name: "Potansiyeller: tümünü seç" }));
    expect(within(picker).getByTestId("scope-crm.leads.write")).toBeChecked();
    // A group the creator holds nothing of stays locked.
    expect(within(picker).getByRole("checkbox", { name: "Fırsatlar: tümünü seç" })).toBeDisabled();

    await user.click(within(picker).getByRole("button", { name: "Temizle" }));
    expect(within(picker).getByTestId("scope-crm.leads.read")).not.toBeChecked();
  });

  it("offers 30 / 90 / 365 days capped by the server maximum and a custom date within it", async () => {
    status = integrationsStatus({ apiKeys: { maxLifetimeDays: 90, defaultLifetimeDays: 90, rateLimitPerMinute: 120 } });
    const user = await openCreate();
    await waitFor(() => expect(screen.getByRole("radio", { name: "90 gün" })).toBeChecked());
    expect(screen.getByRole("radio", { name: "30 gün" })).toBeInTheDocument();
    expect(screen.queryByRole("radio", { name: "365 gün" })).not.toBeInTheDocument();

    await user.type(nameField(), "Deneme");
    await user.click(screen.getByTestId("scope-crm.leads.read"));
    await user.click(screen.getByRole("radio", { name: "Özel tarih" }));
    const date = screen.getByLabelText("Özel tarih", { selector: 'input[type="date"]' });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Bir bitiş tarihi seçin")).toBeInTheDocument();

    fireEvent.change(date, { target: { value: "2020-01-01" } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Bitiş tarihi gelecekte olmalıdır")).toBeInTheDocument();

    fireEvent.change(date, { target: { value: new Date(Date.now() + 120 * DAY).toISOString().slice(0, 10) } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Bitiş tarihi en çok 90 gün sonra olabilir")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("validates name, scopes and CIDR lines on the client and never posts an invalid form", async () => {
    const user = await openCreate();
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Ad zorunludur")).toBeInTheDocument();
    expect(screen.getByText("En az bir kapsam seçin")).toBeInTheDocument();

    await user.type(nameField(), "Deneme");
    await user.click(screen.getByTestId("scope-crm.leads.read"));
    const cidrs = screen.getByLabelText(/IP izin listesi/);
    await user.type(cidrs, "0.0.0.0/0");
    expect(await screen.findByText(/tüm adresleri kapsar/)).toBeInTheDocument();
    await user.clear(cidrs);
    await user.type(cidrs, "203.0.113.0");
    expect(await screen.findByText(/Geçersiz CIDR: 203.0.113.0/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(client.post).not.toHaveBeenCalled();
  });

  it("creates the key and shows the raw key exactly once with a curl example", async () => {
    const user = await openCreate();
    await user.type(nameField(), "  Raporlama botu  ");
    await user.type(screen.getByRole("textbox", { name: "Açıklama" }), "Gece raporu");
    await user.click(within(screen.getByTestId("scope-picker")).getByRole("button", { name: "Salt okuma" }));
    await user.type(screen.getByLabelText(/IP izin listesi/), "203.0.113.0/24{enter}2001:db8::/32");
    await user.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(posted("/integrations/api-keys")).toHaveLength(1));
    const body = posted("/integrations/api-keys")[0]?.[1] as { name: string; scopes: string[]; expiresAt: string; allowedCidrs: string[]; description: string };
    expect(body).toMatchObject({
      name: "Raporlama botu",
      scopes: ["crm.accounts.read", "crm.leads.read"],
      allowedCidrs: ["203.0.113.0/24", "2001:db8::/32"],
      description: "Gece raporu",
    });
    // Default lifetime: 365 days from now.
    expect(Math.abs(Date.parse(body.expiresAt) - (Date.now() + 365 * DAY))).toBeLessThan(120_000);

    const reveal = await screen.findByTestId("secret-reveal");
    expect(within(reveal).getByTestId("secret-value")).toHaveValue(RAW_KEY);
    expect(within(reveal).getByTestId("reveal-curl")).toHaveTextContent(`Authorization: Bearer ${RAW_KEY}`);
    const done = within(reveal).getByRole("button", { name: "Tamam" });
    expect(done).toBeDisabled();
    await user.keyboard("{Escape}");
    expect(screen.getByTestId("secret-reveal")).toBeInTheDocument();
    await user.click(within(reveal).getByRole("checkbox", { name: "Kaydettim" }));
    await user.click(done);

    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain(RAW_KEY);
    expect(JSON.stringify({ ...window.localStorage })).not.toContain("crmk_");
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain("crmk_");
  });

  async function fillValidForm() {
    const user = await openCreate();
    await user.type(nameField(), "Deneme");
    await user.click(screen.getByTestId("scope-crm.leads.read"));
    return user;
  }

  it("puts role.permission_escalation and api_key.scope_not_allowed on the scope picker", async () => {
    const user = await fillValidForm();
    createHandler = () => problem(403, { code: "role.permission_escalation" });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Sahip olmadığınız bir izni anahtara veremezsiniz")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();

    createHandler = () => problem(400, { code: "api_key.scope_not_allowed", args: { scope: "org.roles.manage" } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Bu kapsam bir API anahtarına verilemez: org.roles.manage")).toBeInTheDocument();
    expect(screen.queryByText("Sahip olmadığınız bir izni anahtara veremezsiniz")).not.toBeInTheDocument();
  });

  it("maps a name conflict and validation errors to their fields, and toasts the plan limit", async () => {
    const user = await fillValidForm();
    createHandler = () => problem(409, { code: "api_key.name_taken" });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Bu adla bir API anahtarı zaten var")).toBeInTheDocument();

    createHandler = () => problem(400, { code: "validation", errors: { expiresAt: ["Bitiş geçersiz"], allowedCidrs: ["CIDR reddedildi"] } });
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("CIDR reddedildi")).toBeInTheDocument();
    expect(screen.getByText("Bitiş geçersiz")).toBeInTheDocument();

    const limit = problem(402, { code: "plan.limit_exceeded", args: { limit: "api_keys", max: 3, used: 3 } });
    createHandler = () => limit;
    await user.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(limit));
    expect(screen.getByRole("textbox", { name: /^Ad\s*\*?$/ })).toBeInTheDocument();
    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
  });
});

describe("API keys tab - edit, revoke, delete, usage", () => {
  async function openMenu(name: string) {
    const user = userEvent.setup();
    render();
    const row = (await screen.findByText(name)).closest("tr") as HTMLElement;
    await user.click(within(row).getByRole("button", { name: `${name} işlemleri` }));
    return user;
  }

  it("edits name, description and IP list only: scope and expiry stay fixed", async () => {
    const user = await openMenu("Raporlama");
    await user.click(await screen.findByRole("menuitem", { name: "Düzenle" }));
    expect(screen.queryByTestId("scope-picker")).not.toBeInTheDocument();
    expect(screen.getByTestId("scope-fixed")).toHaveTextContent("Kapsam ve geçerlilik süresi sonradan değiştirilemez");
    const name = screen.getByRole("textbox", { name: /^Ad\s*\*?$/ });
    await user.clear(name);
    await user.type(name, "Raporlama v2");
    await user.type(screen.getByLabelText(/IP izin listesi/), "198.51.100.0/24");
    await user.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.patch).toHaveBeenCalledTimes(1));
    expect(client.patch.mock.calls[0]).toEqual([
      "/integrations/api-keys/k1",
      { name: "Raporlama v2", description: null, allowedCidrs: ["198.51.100.0/24"] },
    ]);
  });

  it("revokes only an active key and only after a confirmation", async () => {
    const user = await openMenu("Raporlama");
    expect(screen.queryByRole("menuitem", { name: "Sil" })).not.toBeInTheDocument();
    await user.click(await screen.findByRole("menuitem", { name: "İptal et" }));
    expect(await screen.findByText(/hemen geçersiz olur/)).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
    const dialog = screen.getByText(/hemen geçersiz olur/).closest("section") as HTMLElement;
    await user.click(within(dialog).getByRole("button", { name: "İptal et" }));
    await waitFor(() => expect(posted("/integrations/api-keys/k1/revoke")).toHaveLength(1));
    await waitFor(() => expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" })));
  });

  it("offers Sil (not İptal et) for revoked and expired keys and deletes after a confirmation", async () => {
    const user = await openMenu("Devre dışı anahtar");
    expect(screen.queryByRole("menuitem", { name: "İptal et" })).not.toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Düzenle" })).not.toBeInTheDocument();
    await user.click(await screen.findByRole("menuitem", { name: "Sil" }));
    const dialog = (await screen.findByText(/Yalnızca iptal edilmiş veya süresi dolmuş/)).closest("section") as HTMLElement;
    await user.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/integrations/api-keys/k4"));
  });

  it("hands api_key.active (409) to the error toast", async () => {
    const failure = problem(409, { code: "api_key.active" });
    deleteHandler = () => failure;
    const user = await openMenu("Eski");
    await user.click(await screen.findByRole("menuitem", { name: "Sil" }));
    const dialog = (await screen.findByText(/Yalnızca iptal edilmiş veya süresi dolmuş/)).closest("section") as HTMLElement;
    await user.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
  });

  it("shows the usage chart for the last 30 days and follows the range", async () => {
    const user = await openMenu("Raporlama");
    await user.click(await screen.findByRole("menuitem", { name: "Kullanım" }));
    const drawer = await screen.findByTestId("usage-drawer");
    await waitFor(() => expect(within(drawer).getByTestId("usage-total-requests")).toHaveTextContent("200"));
    expect(within(drawer).getByTestId("usage-total-errors")).toHaveTextContent("4");
    expect(within(drawer).getByTestId("usage-total-throttled")).toHaveTextContent("2");
    const chart = await within(drawer).findByTestId("chart-bar");
    expect(JSON.parse(chart.getAttribute("data-points") ?? "[]")).toHaveLength(2);
    const usageCall = () => client.get.mock.calls.filter(([url]) => url === "/integrations/api-keys/k1/usage").at(-1)?.[1]?.params;
    const wide = usageCall() as { from: string; to: string };
    expect((Date.parse(wide.to) - Date.parse(wide.from)) / DAY).toBe(29);

    await user.click(within(drawer).getByRole("combobox", { name: "Aralık" }));
    await user.click(await screen.findByRole("option", { name: "Son 7 gün" }));
    await waitFor(() => {
      const narrow = usageCall() as { from: string; to: string };
      expect((Date.parse(narrow.to) - Date.parse(narrow.from)) / DAY).toBe(6);
    });
  });

  it("shows an empty state when the key was not used", async () => {
    usage = [{ day: "2026-09-19", requests: 0, errors: 0, throttled: 0 }];
    const user = await openMenu("Raporlama");
    await user.click(await screen.findByRole("menuitem", { name: "Kullanım" }));
    expect(await screen.findByTestId("usage-empty")).toHaveTextContent("Bu aralıkta kullanım yok");
  });
});
