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
import { campaign } from "@/test/campaigns";
import { toast } from "@/hooks/use-toast";
import CampaignsPage from "./campaigns";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const rowFor = (name: string) => screen.getByText(name).closest("tr") as HTMLElement;
const lastListParams = () =>
  client.get.mock.calls.filter(([u]) => u === "/campaigns").at(-1)?.[1].params;

function renderPage(route = "/app/campaigns") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/campaigns" element={<CampaignsPage />} />
        <Route path="/app/campaigns/:id" element={<div>detail page</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("CampaignsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /campaigns": () =>
        page(
          [
            campaign("1", {
              status: "active",
              startDate: "2026-09-01",
              endDate: "2026-09-30",
              budget: 50000,
              memberCount: 12,
            }),
            campaign("2", { type: "webinar", status: "completed" }),
          ],
          { totalCount: 60 }
        ),
      "GET /organization/members": () => MEMBERS,
      "DELETE /campaigns/1": () => undefined,
      "POST /campaigns": () => campaign("new-id"),
    });
  });
  afterEach(clearSession);

  it("lists the campaigns with type, status, dates, budget, member count and owner", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage();
    await screen.findByText("Kampanya 1");

    const row = within(rowFor("Kampanya 1"));
    expect(row.getByRole("link", { name: "Kampanya 1" })).toHaveAttribute(
      "href",
      "/app/campaigns/1"
    );
    expect(row.getByText("E-posta")).toBeInTheDocument();
    expect(row.getByText("Aktif")).toBeInTheDocument();
    expect(row.getByText(/1 Eyl 2026 - 30 Eyl 2026/)).toBeInTheDocument();
    expect(row.getByText(/50\.000/)).toBeInTheDocument();
    expect(row.getByText("12")).toBeInTheDocument();
    expect(row.getByText("Ada Lovelace")).toBeInTheDocument();
    expect(within(rowFor("Kampanya 2")).getByText("Web semineri")).toBeInTheDocument();
    expect(lastListParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("syncs the multi-value type and status filters with the URL and the request", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage("/app/campaigns?page=2");
    await screen.findByText("Kampanya 1");
    expect(lastListParams()).toEqual({ page: 2, pageSize: 25 });

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Planlandı" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "planned" })
    );
    await userEvent.click(await screen.findByRole("option", { name: "Aktif" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "planned,active" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/campaigns?status=planned%2Cactive"
    );

    await userEvent.click(screen.getByRole("combobox", { name: "Tür" }));
    await userEvent.click(await screen.findByRole("option", { name: "Etkinlik" }));
    await waitFor(() =>
      expect(lastListParams()).toMatchObject({ status: "planned,active", type: "event" })
    );

    await userEvent.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25 }));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/campaigns$/);
  });

  it("reads filters, sort and search from the URL on load", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage("/app/campaigns?status=planned,active&type=email&sort=-budget&q=sonbahar&page=3");
    await screen.findByText("Kampanya 1");

    expect(lastListParams()).toEqual({
      page: 3,
      pageSize: 25,
      q: "sonbahar",
      sort: "-budget",
      status: "planned,active",
      type: "email",
    });
  });

  it("sorts by a column (ascending, descending) and paginates through the URL", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage();
    await screen.findByText("Kampanya 1");

    await userEvent.click(screen.getByRole("button", { name: "Bütçe sütununa göre sırala" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "budget" }));
    await userEvent.click(screen.getByRole("button", { name: "Bütçe sütununa göre sırala" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "-budget" }));
    expect(screen.getByTestId("location")).toHaveTextContent("sort=-budget");

    await userEvent.click(screen.getByRole("button", { name: "2" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ page: 2, sort: "-budget" }));
    expect(screen.getByTestId("location")).toHaveTextContent("page=2");
  });

  it("searches by name through the q parameter", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage();
    await screen.findByText("Kampanya 1");

    await userEvent.type(screen.getByPlaceholderText("Kampanya adı veya açıklama ara"), "bahar");
    await waitFor(() => expect(lastListParams()).toMatchObject({ q: "bahar" }));
    expect(screen.getByTestId("location")).toHaveTextContent("q=bahar");
  });

  it("is read-only without crm.campaigns.write", async () => {
    setPermissions(["crm.campaigns.read"]);
    renderPage();
    await screen.findByText("Kampanya 1");

    expect(screen.queryByRole("button", { name: "Yeni kampanya" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
  });

  it("deletes a campaign after a confirmation", async () => {
    setPermissions(["crm.campaigns.read", "crm.campaigns.write"]);
    renderPage();
    await screen.findByText("Kampanya 1");

    await userEvent.click(within(rowFor("Kampanya 1")).getByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog", { name: "Kampanya silinsin mi?" });
    expect(client.delete).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));

    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/campaigns/1"));
    expect(toast).toHaveBeenCalledWith(
      expect.objectContaining({ description: "Kampanya silindi" })
    );
  });

  it("opens the create dialog and goes to the new campaign", async () => {
    setPermissions(["crm.campaigns.read", "crm.campaigns.write"]);
    renderPage();
    await screen.findByText("Kampanya 1");

    await userEvent.click(screen.getByRole("button", { name: "Yeni kampanya" }));
    const dialog = await screen.findByRole("dialog", { name: "Yeni kampanya" });
    await userEvent.type(within(dialog).getByLabelText(/Kampanya adı/), "Sonbahar");
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));

    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/app/campaigns/new-id")
    );
  });

  it("shows the empty state", async () => {
    setPermissions(["crm.campaigns.read"]);
    client.get.mockImplementation(async (url: string) => {
      if (url === "/campaigns") return { data: page([]) };
      return { data: [] };
    });
    renderPage();
    expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
  });
});
