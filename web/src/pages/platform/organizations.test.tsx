import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { LocationDisplay, clearSession, installApi, page, problem, type MockClient } from "@/test/crm";
import { PLANS, orgRow } from "@/test/platform";
import OrganizationsPage from "./organizations";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ROWS = [
  orgRow("1", { name: "Acme A.Ş.", slug: "acme", status: "trial" }),
  orgRow("2", {
    name: "Beta Ltd",
    slug: "beta",
    planCode: "business",
    planName: "Business",
    status: "suspended",
    source: "platform",
    trialEndsOn: undefined,
    usage: undefined,
  }),
];

const lastListParams = () =>
  client.get.mock.calls.filter(([url]) => url === "/platform/organizations").at(-1)?.[1].params;

function renderPage(route = "/app/platform/organizations") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/platform/organizations" element={<OrganizationsPage />} />
        <Route path="/app/platform/organizations/:tenantId" element={<div>detail-stub</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("Platform organizations list", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /platform/organizations": () => page(ROWS, { totalCount: 60 }),
      "GET /platform/plans": () => PLANS,
    });
  });
  afterEach(clearSession);

  it("lists organizations with plan, status badge, trial end, users, record counts and source", async () => {
    renderPage();

    const acme = (await screen.findByText("Acme A.Ş.")).closest("tr") as HTMLElement;
    expect(within(acme).getByText("acme")).toBeInTheDocument();
    expect(within(acme).getByText("Starter")).toBeInTheDocument();
    expect(within(acme).getByTestId("status-badge")).toHaveTextContent("Deneme");
    expect(within(acme).getByText(/4 Eki/)).toBeInTheDocument();
    expect(within(acme).getByText("3 (+1 bekleyen)")).toBeInTheDocument();
    expect(within(acme).getByText(/Satış 340 · Aktiviteler 120/)).toBeInTheDocument();
    expect(within(acme).getByText("Kayıt")).toBeInTheDocument();
    expect(within(acme).getByRole("link", { name: "Acme A.Ş." })).toHaveAttribute(
      "href",
      "/app/platform/organizations/1"
    );

    const beta = screen.getByText("Beta Ltd").closest("tr") as HTMLElement;
    expect(within(beta).getByTestId("status-badge")).toHaveTextContent("Askıda");
    expect(within(beta).getByText("Platform")).toBeInTheDocument();
    expect(lastListParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("keeps status, plan, source, search, sort and page in the URL and sends them to the server", async () => {
    renderPage();
    await screen.findByText("Acme A.Ş.");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Askıda" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "suspended" }));
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/platform/organizations?status=suspended"
    );

    await userEvent.click(screen.getByRole("combobox", { name: "Plan" }));
    await userEvent.click(await screen.findByRole("option", { name: "Business" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ status: "suspended", planCode: "business" }));
    await userEvent.click(screen.getByRole("combobox", { name: "Kaynak" }));
    await userEvent.click(await screen.findByRole("option", { name: "Platform" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ source: "platform" }));

    await userEvent.type(screen.getByRole("searchbox"), "acme");
    await waitFor(() => expect(lastListParams()).toMatchObject({ q: "acme" }));
    expect(screen.getByTestId("location")).toHaveTextContent("q=acme");

    await userEvent.click(screen.getByRole("button", { name: "Kullanıcı sütununa göre sırala" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "users" }));
    await userEvent.click(screen.getByRole("button", { name: "Kullanıcı sütununa göre sırala" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "-users" }));

    await userEvent.click(screen.getByRole("button", { name: "2" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ page: 2 }));
    expect(screen.getByTestId("location")).toHaveTextContent("page=2");

    await userEvent.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "-users" }));
    expect(lastListParams()).not.toHaveProperty("status");
    expect(lastListParams()).not.toHaveProperty("planCode");
    expect(lastListParams()).not.toHaveProperty("q");
  });

  it("restores every filter from a shared URL", async () => {
    renderPage("/app/platform/organizations?status=trial_expired&planCode=starter&source=signup&q=acme&sort=-createdAt&page=2&pageSize=50");
    await screen.findByText("Acme A.Ş.");

    expect(lastListParams()).toEqual({
      page: 2,
      pageSize: 50,
      q: "acme",
      sort: "-createdAt",
      status: "trial_expired",
      planCode: "starter",
      source: "signup",
    });
    expect(screen.getByRole("searchbox")).toHaveValue("acme");
    expect(screen.getByRole("combobox", { name: "Durum" })).toHaveValue("Deneme bitti");
    expect(screen.getByRole("combobox", { name: "Plan" })).toHaveValue("Starter");
  });

  it("shows a retry after a load error and an empty state for no organizations", async () => {
    installApi(client, {
      "GET /platform/organizations": () => problem(500, { title: "Sunucu hatası" }),
      "GET /platform/plans": () => PLANS,
    });
    renderPage();
    expect(await screen.findByText("Veriler yüklenemedi")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });

  describe("create organization", () => {
    async function openDialog() {
      await userEvent.click(await screen.findByRole("button", { name: "Organizasyon aç" }));
      return screen.findByRole("dialog");
    }

    it("offers only active plans, posts plan and trial date, and shows the generated password once", async () => {
      installApi(client, {
        "GET /platform/organizations": () => page(ROWS),
        "GET /platform/plans": () => PLANS,
        "POST /platform/organizations": () => ({
          organizationId: "new-1",
          name: "Yeni A.Ş.",
          slug: "yeni",
          adminUserId: "u1",
          adminEmail: "admin@yeni.com",
          adminAccountCreated: true,
          generatedPassword: "Gen-Pass-987654",
          planCode: "business",
        }),
      });
      renderPage();
      await screen.findByText("Acme A.Ş.");
      const dialog = await openDialog();

      await userEvent.type(within(dialog).getByLabelText(/Organizasyon adı/), " Yeni A.Ş. ");
      await userEvent.type(within(dialog).getByLabelText(/Yönetici adı/), "Ada Yönetici");
      await userEvent.type(within(dialog).getByLabelText(/Yönetici e-postası/), "admin@yeni.com");
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Plan" }));
      const options = await screen.findAllByRole("option");
      expect(options.map((o) => o.textContent)).toEqual(["Ic kullanim", "Starter", "Business"]);
      await userEvent.click(screen.getByRole("option", { name: "Business" }));
      await userEvent.type(within(dialog).getByLabelText("Deneme bitiş tarihi"), "2026-12-31");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post).toHaveBeenCalledWith("/platform/organizations", {
        organizationName: "Yeni A.Ş.",
        adminDisplayName: "Ada Yönetici",
        adminEmail: "admin@yeni.com",
        locale: "tr",
        planCode: "business",
        trialEndsOn: "2026-12-31",
      });

      // One-time password with a copy button; it is gone as soon as the dialog is closed.
      expect(await screen.findByDisplayValue("Gen-Pass-987654")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Kopyala" })).toBeInTheDocument();
      await userEvent.click(screen.getByRole("button", { name: /Parolayı kaydettim/ }));
      expect(screen.queryByDisplayValue("Gen-Pass-987654")).not.toBeInTheDocument();
      expect(screen.queryByText("Gen-Pass-987654")).not.toBeInTheDocument();
      expect(await screen.findByText("detail-stub")).toBeInTheDocument();
      expect(screen.getByTestId("location")).toHaveTextContent("/app/platform/organizations/new-1");
    });

    it("omits plan and trial when left at the default", async () => {
      installApi(client, {
        "GET /platform/organizations": () => page(ROWS),
        "GET /platform/plans": () => PLANS,
        "POST /platform/organizations": () => ({
          organizationId: "new-2",
          name: "X",
          slug: "x",
          adminUserId: "u1",
          adminEmail: "a@x.com",
          adminAccountCreated: false,
          adminInvitationPending: true,
        }),
      });
      renderPage();
      await screen.findByText("Acme A.Ş.");
      const dialog = await openDialog();
      await userEvent.type(within(dialog).getByLabelText(/Organizasyon adı/), "X");
      await userEvent.type(within(dialog).getByLabelText(/Yönetici adı/), "A");
      await userEvent.type(within(dialog).getByLabelText(/Yönetici e-postası/), "a@x.com");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post.mock.calls[0]?.[1]).toEqual({
        organizationName: "X",
        adminDisplayName: "A",
        adminEmail: "a@x.com",
        locale: "tr",
      });
      // An existing account only gets a pending invitation: no password dialog, straight to the detail page.
      expect(await screen.findByText("detail-stub")).toBeInTheDocument();
    });

    it("puts a server validation error (unknown plan) on the plan field and keeps the dialog open", async () => {
      installApi(client, {
        "GET /platform/organizations": () => page(ROWS),
        "GET /platform/plans": () => PLANS,
        "POST /platform/organizations": () =>
          problem(400, { code: "validation", errors: { PlanCode: ["Plan geçersiz"] } }),
      });
      renderPage();
      await screen.findByText("Acme A.Ş.");
      const dialog = await openDialog();
      await userEvent.type(within(dialog).getByLabelText(/Organizasyon adı/), "X");
      await userEvent.type(within(dialog).getByLabelText(/Yönetici adı/), "A");
      await userEvent.type(within(dialog).getByLabelText(/Yönetici e-postası/), "a@x.com");
      await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

      expect(await within(dialog).findByText("Plan geçersiz")).toBeInTheDocument();
      expect(screen.queryByText("detail-stub")).not.toBeInTheDocument();
    });
  });

  it("opens the usage export dialog", async () => {
    renderPage();
    await userEvent.click(await screen.findByRole("button", { name: "Kullanım dışa aktar" }));
    expect(await screen.findByRole("dialog", { name: "Kullanımı dışa aktar" })).toBeInTheDocument();
  });
});
