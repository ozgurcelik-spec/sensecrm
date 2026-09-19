import type { ComponentType } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
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
import { activity } from "@/test/activities";
import AccountDetailPage from "./account-detail";
import ContactDetailPage from "./contact-detail";
import DealDetailPage from "./deal-detail";
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

interface Case {
  name: string;
  path: string;
  Page: ComponentType;
  read: string;
  record: object;
  relatedType: string;
  relatedName: string;
  extra?: Record<string, () => unknown>;
}

const CASES: Case[] = [
  {
    name: "account",
    path: "accounts",
    Page: AccountDetailPage,
    read: "crm.accounts.read",
    record: { id: "r1", name: "Acme Ltd", ...base },
    relatedType: "account",
    relatedName: "Acme Ltd",
  },
  {
    name: "contact",
    path: "contacts",
    Page: ContactDetailPage,
    read: "crm.contacts.read",
    record: { id: "r1", lastName: "Yılmaz", fullName: "Ayşe Yılmaz", ...base },
    relatedType: "contact",
    relatedName: "Ayşe Yılmaz",
  },
  {
    name: "lead",
    path: "leads",
    Page: LeadDetailPage,
    read: "crm.leads.read",
    record: {
      id: "r1",
      lastName: "Demir",
      fullName: "Can Demir",
      company: "Demir A.Ş.",
      source: "web",
      status: "new",
      ...base,
    },
    relatedType: "lead",
    relatedName: "Can Demir",
  },
  {
    name: "deal",
    path: "deals",
    Page: DealDetailPage,
    read: "crm.deals.read",
    record: {
      id: "r1",
      name: "Alfa fırsatı",
      accountId: "a1",
      accountName: "Acme Ltd",
      pipelineId: "p1",
      pipelineName: "Ana",
      stageId: "s1",
      stageName: "Teklif",
      stageKind: "open",
      probability: 50,
      currency: "TRY",
      ...base,
    },
    relatedType: "deal",
    relatedName: "Alfa fırsatı",
    extra: { "GET /pipelines": () => [] },
  },
];

describe.each(CASES)("$name detail: Aktiviteler tab", (c) => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      [`GET /${c.path}/r1`]: () => c.record,
      "GET /activities": () => page([activity("1", { subject: "Kayda bağlı görev" })]),
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () => ({ id: "new-id" }),
      ...c.extra,
    });
  });
  afterEach(clearSession);

  function open(route = `/app/${c.path}/r1`) {
    return renderWithProviders(
      <Routes>
        <Route path={`/app/${c.path}/:id`} element={<c.Page />} />
      </Routes>,
      { route }
    );
  }

  it("lists the record's activities and quick-adds one with the record pre-filled", async () => {
    setPermissions([c.read, "crm.activities.read", "crm.activities.write"]);
    open();

    await userEvent.click(await screen.findByRole("tab", { name: "Aktiviteler" }));
    expect(await screen.findByText("Kayda bağlı görev")).toBeInTheDocument();
    const list = client.get.mock.calls.find(([url]) => url === "/activities")?.[1].params;
    expect(list).toMatchObject({ relatedType: c.relatedType, relatedId: "r1" });

    await userEvent.type(screen.getByLabelText("Konu"), "Yeni takip");
    await userEvent.click(screen.getByRole("button", { name: "Ekle" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]?.[1]).toMatchObject({
      type: "task",
      subject: "Yeni takip",
      relatedType: c.relatedType,
      relatedId: "r1",
    });

    // The record's own name shows on the locked form of the detailed dialog.
    await userEvent.click(screen.getByRole("button", { name: "Ayrıntılı ekle" }));
    const related = await screen.findByLabelText("İlişkili kayıt");
    expect(related).toBeDisabled();
    expect((related as HTMLInputElement).value).toContain(c.relatedName);
  });

  it("restores the tab from the URL", async () => {
    setPermissions([c.read, "crm.activities.read"]);
    open(`/app/${c.path}/r1?tab=activities`);

    expect(await screen.findByText("Kayda bağlı görev")).toBeInTheDocument();
  });

  it("has no tab without crm.activities.read", async () => {
    setPermissions([c.read]);
    open();

    await screen.findByRole("tab", { name: "Genel" });
    expect(screen.queryByRole("tab", { name: "Aktiviteler" })).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([url]) => url === "/activities")).toBe(false);
  });
});
