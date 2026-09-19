import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  MEMBERS,
  clearSession,
  installApi,
  page,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import LeadsPage from "./leads";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const lead = (id: string, status: string) => ({
  id,
  lastName: `Soyad ${id}`,
  fullName: `Kişi ${id}`,
  company: `Şirket ${id}`,
  source: "web",
  status,
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
});

const rowFor = (name: string) => screen.getByText(name).closest("tr") as HTMLElement;

function renderPage(route = "/app/leads") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/leads" element={<LeadsPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("LeadsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /leads": () => page([lead("1", "qualified"), lead("2", "converted")]),
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
      "GET /pipelines": () => [],
    });
  });
  afterEach(clearSession);

  it("syncs the status and source filters with the URL and the request", async () => {
    setPermissions(["crm.leads.read"]);
    renderPage("/app/leads?page=2");
    await screen.findByText("Kişi 1");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Nitelikli" }));

    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/leads").at(-1)?.[1];
      expect(last.params).toEqual({ page: 1, pageSize: 25, status: "qualified" });
    });
    expect(screen.getByTestId("location")).toHaveTextContent("/app/leads?status=qualified");

    await userEvent.click(screen.getByRole("combobox", { name: "Kaynak" }));
    await userEvent.click(await screen.findByRole("option", { name: "Tavsiye" }));
    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/leads").at(-1)?.[1];
      expect(last.params).toMatchObject({ status: "qualified", source: "referral" });
    });
  });

  it("shows convert only for open leads when the user can write leads, accounts and contacts", async () => {
    setPermissions([
      "crm.leads.read",
      "crm.leads.write",
      "crm.accounts.write",
      "crm.contacts.write",
    ]);
    renderPage();
    await screen.findByText("Kişi 1");

    expect(within(rowFor("Kişi 1")).getByRole("button", { name: "Dönüştür" })).toBeInTheDocument();
    expect(within(rowFor("Kişi 1")).getByRole("button", { name: "Düzenle" })).toBeInTheDocument();
    // A converted lead is read-only: no convert, no edit.
    expect(
      within(rowFor("Kişi 2")).queryByRole("button", { name: "Dönüştür" })
    ).not.toBeInTheDocument();
    expect(
      within(rowFor("Kişi 2")).queryByRole("button", { name: "Düzenle" })
    ).not.toBeInTheDocument();
  });

  it("hides convert when accounts or contacts cannot be written, even with lead write access", async () => {
    setPermissions(["crm.leads.read", "crm.leads.write", "crm.contacts.write"]);
    renderPage();
    await screen.findByText("Kişi 1");

    expect(screen.getByRole("button", { name: "Yeni potansiyel" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Dönüştür" })).not.toBeInTheDocument();
  });

  it("is read-only without write permission", async () => {
    setPermissions(["crm.leads.read"]);
    renderPage();
    await screen.findByText("Kişi 1");

    expect(screen.queryByRole("button", { name: "Yeni potansiyel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Dönüştür" })).not.toBeInTheDocument();
  });

  it("opens the convert dialog for a lead", async () => {
    setPermissions([
      "crm.leads.read",
      "crm.leads.write",
      "crm.accounts.write",
      "crm.contacts.write",
    ]);
    renderPage();
    await screen.findByText("Kişi 1");

    await userEvent.click(within(rowFor("Kişi 1")).getByRole("button", { name: "Dönüştür" }));

    expect(
      await screen.findByRole("dialog", { name: /Kişi 1 potansiyelini dönüştür/ })
    ).toBeInTheDocument();
  });
});
