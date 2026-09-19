import type { ComponentType } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  MEMBERS,
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { campaign } from "@/test/campaigns";
import { toast, toastApiError } from "@/hooks/use-toast";
import ContactsPage from "./contacts";
import LeadsPage from "./leads";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const lead = (id: string) => ({
  id,
  lastName: `Soyad ${id}`,
  fullName: `Kişi ${id}`,
  company: `Şirket ${id}`,
  source: "web",
  status: "new",
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
});

const contact = (id: string) => ({
  id,
  lastName: `Soyad ${id}`,
  fullName: `Kişi ${id}`,
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
});

interface Case {
  name: string;
  Page: ComponentType;
  path: string;
  read: string;
  memberType: string;
  rowFactory: (id: string) => object;
}

const CASES: Case[] = [
  {
    name: "Leads",
    Page: LeadsPage,
    path: "/leads",
    read: "crm.leads.read",
    memberType: "lead",
    rowFactory: lead,
  },
  {
    name: "Contacts",
    Page: ContactsPage,
    path: "/contacts",
    read: "crm.contacts.read",
    memberType: "contact",
    rowFactory: contact,
  },
];

const campaignListCalls = () => client.get.mock.calls.filter(([u]) => u === "/campaigns");

describe.each(CASES)("$name list - bulk add to campaign", (c) => {
  let totalCount: number;

  function renderList(route = c.path === "/leads" ? "/app/leads" : "/app/contacts") {
    return renderWithProviders(
      <Routes>
        <Route path={route.split("?")[0]} element={<c.Page />} />
      </Routes>,
      { route }
    );
  }

  beforeEach(() => {
    vi.clearAllMocks();
    totalCount = 3;
    installApi(client, {
      [`GET ${c.path}`]: (request) => {
        const pageNumber = Number(request.params?.page ?? 1);
        return page(
          [c.rowFactory(`${pageNumber}a`), c.rowFactory(`${pageNumber}b`), c.rowFactory(`${pageNumber}c`)],
          { totalCount, page: pageNumber }
        );
      },
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
      "GET /campaigns": () =>
        page([campaign("c1", { name: "Sonbahar" }), campaign("c2", { name: "Kış", status: "active" })]),
      "POST /campaigns/c1/members": () => ({ addedCount: 2, alreadyMemberCount: 0, skipped: [] }),
    });
  });
  afterEach(clearSession);

  it("has no selection column and no bulk action without crm.campaigns.write", async () => {
    setPermissions([c.read]);
    renderList();
    await screen.findByText("Kişi 1a");

    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Kampanyaya ekle" })).not.toBeInTheDocument();
  });

  it("shows the bulk action only while rows are selected", async () => {
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList();
    await screen.findByText("Kişi 1a");
    expect(screen.queryByRole("button", { name: "Kampanyaya ekle" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    expect(screen.getByTestId("bulk-bar")).toHaveTextContent("1 kayıt seçili");
    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    expect(screen.queryByRole("button", { name: "Kampanyaya ekle" })).not.toBeInTheDocument();
  });

  it("adds the selected rows with the right member type and ids and summarizes the result", async () => {
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList();
    await screen.findByText("Kişi 1a");

    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1c kaydını seç" }));
    await userEvent.click(screen.getByRole("button", { name: "Kampanyaya ekle" }));

    const dialog = await screen.findByRole("dialog", { name: "Kampanyaya ekle" });
    expect(within(dialog).getByText("2 kayıt seçili")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("combobox", { name: /Kampanya/ }));
    await userEvent.click(await screen.findByRole("option", { name: "Sonbahar" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members", {
        memberType: c.memberType,
        memberIds: ["1a", "1c"],
      })
    );
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ description: "2 eklendi" }));
    // Only planned and active campaigns are offered.
    expect(campaignListCalls().at(-1)?.[1].params).toMatchObject({ status: "planned,active" });
    // The selection is cleared after the add.
    await waitFor(() => expect(screen.queryByTestId("bulk-bar")).not.toBeInTheDocument());
    expect(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" })).not.toBeChecked();
  });

  it("tells about converted and missing records that were skipped", async () => {
    client.post.mockImplementation(async () => ({
      data: {
        addedCount: 1,
        alreadyMemberCount: 1,
        skipped: [{ memberId: "1b", reason: "lead_converted" }],
      },
    }));
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList();
    await screen.findByText("Kişi 1a");

    await userEvent.click(screen.getByRole("checkbox", { name: "Sayfadaki tümünü seç" }));
    await userEvent.click(screen.getByRole("button", { name: "Kampanyaya ekle" }));
    const dialog = await screen.findByRole("dialog", { name: "Kampanyaya ekle" });
    await userEvent.click(within(dialog).getByRole("combobox", { name: /Kampanya/ }));
    await userEvent.click(await screen.findByRole("option", { name: "Sonbahar" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

    await waitFor(() =>
      expect(toast).toHaveBeenCalledWith(
        expect.objectContaining({
          description: "1 eklendi, 1 zaten üyeydi, 1 atlandı (1 dönüşmüş)",
        })
      )
    );
  });

  it("requires a campaign", async () => {
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList();
    await screen.findByText("Kişi 1a");

    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    await userEvent.click(screen.getByRole("button", { name: "Kampanyaya ekle" }));
    const dialog = await screen.findByRole("dialog", { name: "Kampanyaya ekle" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

    expect(await within(dialog).findByText("Bir kampanya seçin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("shows campaign.closed (a race with another user) as an error toast and keeps the dialog", async () => {
    client.post.mockRejectedValueOnce(problem(409, { code: "campaign.closed" }));
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList();
    await screen.findByText("Kişi 1a");

    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    await userEvent.click(screen.getByRole("button", { name: "Kampanyaya ekle" }));
    const dialog = await screen.findByRole("dialog", { name: "Kampanyaya ekle" });
    await userEvent.click(within(dialog).getByRole("combobox", { name: /Kampanya/ }));
    await userEvent.click(await screen.findByRole("option", { name: "Sonbahar" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    expect(screen.getByRole("dialog", { name: "Kampanyaya ekle" })).toBeInTheDocument();
    expect(toast).not.toHaveBeenCalled();
  });

  it("clears the selection when the page changes", async () => {
    totalCount = 60;
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList();
    await screen.findByText("Kişi 1a");

    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    expect(screen.getByTestId("bulk-bar")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "2" }));
    await screen.findByText("Kişi 2a");

    expect(screen.queryByTestId("bulk-bar")).not.toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Kişi 2a kaydını seç" })).not.toBeChecked();
  });

  it("clears the selection when a filter changes", async () => {
    setPermissions([c.read, "crm.campaigns.write"]);
    renderList(`${c.path === "/leads" ? "/app/leads" : "/app/contacts"}`);
    await screen.findByText("Kişi 1a");

    await userEvent.click(screen.getByRole("checkbox", { name: "Kişi 1a kaydını seç" }));
    expect(screen.getByTestId("bulk-bar")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("combobox", { name: "Sahip" }));
    await userEvent.click(await screen.findByRole("option", { name: "Grace Hopper" }));

    await waitFor(() => expect(screen.queryByTestId("bulk-bar")).not.toBeInTheDocument());
  });
});
