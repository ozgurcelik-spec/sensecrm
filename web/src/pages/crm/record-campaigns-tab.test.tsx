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
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { campaign, membership } from "@/test/campaigns";
import { toast } from "@/hooks/use-toast";
import ContactDetailPage from "./contact-detail";
import LeadDetailPage from "./lead-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const base = {
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
};

const LEAD = {
  id: "r1",
  lastName: "Demir",
  fullName: "Can Demir",
  company: "Demir A.Ş.",
  source: "web",
  status: "new",
  ...base,
};
const CONTACT = { id: "r1", lastName: "Yılmaz", fullName: "Ayşe Yılmaz", ...base };

interface Case {
  name: string;
  path: string;
  Page: ComponentType;
  read: string;
  record: () => object;
  memberType: string;
}

const CASES: Case[] = [
  {
    name: "lead",
    path: "leads",
    Page: LeadDetailPage,
    read: "crm.leads.read",
    record: () => LEAD,
    memberType: "lead",
  },
  {
    name: "contact",
    path: "contacts",
    Page: ContactDetailPage,
    read: "crm.contacts.read",
    record: () => CONTACT,
    memberType: "contact",
  },
];

let memberships: ReturnType<typeof membership>[];
let record: object;

describe.each(CASES)("$name detail - Campaigns tab", (c) => {
  function renderDetail(tab = true) {
    const route = `/app/${c.path}/r1${tab ? "?tab=campaigns" : ""}`;
    return renderWithProviders(
      <Routes>
        <Route path={`/app/${c.path}/:id`} element={<c.Page />} />
      </Routes>,
      { route }
    );
  }

  beforeEach(() => {
    vi.clearAllMocks();
    record = c.record();
    memberships = [
      membership("c1", { campaignName: "Sonbahar E-posta", memberStatus: "sent", membershipId: "m1" }),
      membership("c2", {
        campaignName: "Kış Webinarı",
        campaignType: "webinar",
        campaignStatus: "completed",
        memberStatus: "responded",
        membershipId: "m2",
      }),
    ];
    installApi(client, {
      [`GET /${c.path}/r1`]: () => record,
      "GET /campaigns/by-member": () => memberships,
      "GET /campaigns": () => page([campaign("c9", { name: "Yeni kampanya" })]),
      "GET /organization/members": () => MEMBERS,
      "GET /audit": () => ({ items: [], total: 0 }),
      "POST /campaigns/c9/members": () => ({ addedCount: 1, alreadyMemberCount: 0, skipped: [] }),
      "POST /campaigns/c1/members/remove": () => ({ removedCount: 1 }),
    });
  });
  afterEach(clearSession);

  it("hides the tab without crm.campaigns.read", async () => {
    setPermissions([c.read]);
    renderDetail(false);
    await screen.findByRole("tab", { name: "Genel" });

    expect(screen.queryByRole("tab", { name: "Kampanyalar" })).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([u]) => u === "/campaigns/by-member")).toBe(false);
  });

  it("lists the memberships of the record", async () => {
    setPermissions([c.read, "crm.campaigns.read"]);
    renderDetail();

    expect(await screen.findByRole("link", { name: "Sonbahar E-posta" })).toHaveAttribute(
      "href",
      "/app/campaigns/c1"
    );
    expect(screen.getByRole("link", { name: "Kış Webinarı" })).toHaveAttribute(
      "href",
      "/app/campaigns/c2"
    );
    const row = within(screen.getAllByTestId("record-campaign-row")[0] as HTMLElement);
    expect(row.getByText("E-posta")).toBeInTheDocument();
    expect(row.getByText("Aktif")).toBeInTheDocument();
    expect(row.getByText("Gönderildi")).toBeInTheDocument();
    const byMember = client.get.mock.calls.find(([u]) => u === "/campaigns/by-member");
    expect(byMember?.[1].params).toEqual({ memberType: c.memberType, memberId: "r1" });
  });

  it("is read-only without crm.campaigns.write", async () => {
    setPermissions([c.read, "crm.campaigns.read"]);
    renderDetail();
    await screen.findByRole("link", { name: "Sonbahar E-posta" });

    expect(screen.queryByRole("button", { name: "Kampanyaya ekle" })).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Sonbahar E-posta kampanyasından çıkar" })
    ).not.toBeInTheDocument();
  });

  it("shows the empty state", async () => {
    memberships = [];
    setPermissions([c.read, "crm.campaigns.read"]);
    renderDetail();

    expect(await screen.findByText("Bu kayıt henüz hiçbir kampanyada değil")).toBeInTheDocument();
  });

  it("adds the record to one campaign with a single-element array", async () => {
    setPermissions([c.read, "crm.campaigns.read", "crm.campaigns.write"]);
    renderDetail();
    await screen.findByRole("link", { name: "Sonbahar E-posta" });

    await userEvent.click(screen.getByRole("button", { name: "Kampanyaya ekle" }));
    const dialog = await screen.findByRole("dialog", { name: "Kampanyaya ekle" });
    await userEvent.click(within(dialog).getByRole("combobox", { name: /Kampanya/ }));
    await userEvent.click(await screen.findByRole("option", { name: "Yeni kampanya" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/campaigns/c9/members", {
        memberType: c.memberType,
        memberIds: ["r1"],
      })
    );
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ description: "1 eklendi" }));
  });

  it("removes the record from a campaign after a confirmation", async () => {
    setPermissions([c.read, "crm.campaigns.read", "crm.campaigns.write"]);
    renderDetail();
    await screen.findByRole("link", { name: "Sonbahar E-posta" });

    await userEvent.click(
      screen.getByRole("button", { name: "Sonbahar E-posta kampanyasından çıkar" })
    );
    const dialog = await screen.findByRole("dialog", { name: "Kampanyadan çıkarılsın mı?" });
    expect(client.post).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole("button", { name: "Kampanyadan çıkar" }));

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members/remove", {
        memberIds: ["m1"],
      })
    );
  });
});

describe("lead detail - converted lead", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /leads/r1": () => ({ ...LEAD, status: "converted" }),
      "GET /campaigns/by-member": () => [
        membership("c1", { campaignName: "Sonbahar E-posta", memberStatus: "converted" }),
      ],
      "GET /organization/members": () => MEMBERS,
    });
  });
  afterEach(clearSession);

  it("hides the add button and shows the converted membership", async () => {
    setPermissions(["crm.leads.read", "crm.campaigns.read", "crm.campaigns.write"]);
    renderWithProviders(
      <Routes>
        <Route path="/app/leads/:id" element={<LeadDetailPage />} />
      </Routes>,
      { route: "/app/leads/r1?tab=campaigns" }
    );

    const row = await screen.findByTestId("record-campaign-row");
    expect(within(row).getByText("Dönüştü")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Kampanyaya ekle" })).not.toBeInTheDocument();
    // Removing the membership stays possible.
    expect(
      within(row).getByRole("button", { name: "Sonbahar E-posta kampanyasından çıkar" })
    ).toBeInTheDocument();
  });
});
