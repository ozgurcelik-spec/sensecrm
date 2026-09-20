/**
 * The "Ekler" tab is wired into every record detail page of the plan (account, contact, lead, deal,
 * case, quote, order, campaign) and the activity edit dialog; the list is requested lazily and only
 * with the record type's read permission.
 */
import type { ComponentType } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { ATTACHMENT_PERMISSIONS } from "@/lib/files";
import { renderWithProviders } from "@/test-utils";
import { activity } from "@/test/activities";
import { campaign, metrics } from "@/test/campaigns";
import { FILE_LIMITS, fileItem } from "@/test/files";
import { clearSession, meWith, page, setPermissions, type MockClient } from "@/test/crm";
import { caseDetail } from "@/test/service";
import { useAuthStore } from "@/store/auth.store";
import type { AttachmentRecordType } from "@/types";
import { ActivityFormDialog } from "@/components/activities/activity-form-dialog";
import AccountDetailPage from "@/pages/crm/account-detail";
import CampaignDetailPage from "@/pages/crm/campaign-detail";
import CaseDetailPage from "@/pages/crm/case-detail";
import ContactDetailPage from "@/pages/crm/contact-detail";
import DealDetailPage from "@/pages/crm/deal-detail";
import LeadDetailPage from "@/pages/crm/lead-detail";
import OrderDetailPage from "@/pages/commerce/order-detail";
import QuoteDetailPage from "@/pages/commerce/quote-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const documentBase = {
  number: "X-1",
  subject: "Konu",
  accountId: "a1",
  accountName: "Acme",
  ownerUserId: "user-1",
  currency: "TRY",
  grandTotal: 0,
  subtotal: 0,
  discountTotal: 0,
  taxTotal: 0,
  createdAt: "2026-05-01T10:00:00Z",
  lines: [],
};

const base = { ownerUserId: "user-1", createdAt: "2026-05-01T10:00:00Z" };

interface PageCase {
  type: AttachmentRecordType;
  path: string;
  route: string;
  Component: ComponentType;
  /** GET url -> body of the record itself. */
  detail: Record<string, unknown>;
}

const PAGES: PageCase[] = [
  {
    type: "account",
    path: "/app/accounts/:id",
    route: "/app/accounts/1",
    Component: AccountDetailPage,
    detail: { "/accounts/1": { ...base, id: "1", name: "Acme Ltd", contactCount: 0, dealCount: 0 } },
  },
  {
    type: "contact",
    path: "/app/contacts/:id",
    route: "/app/contacts/1",
    Component: ContactDetailPage,
    detail: { "/contacts/1": { ...base, id: "1", lastName: "Yılmaz", fullName: "Ayşe Yılmaz" } },
  },
  {
    type: "lead",
    path: "/app/leads/:id",
    route: "/app/leads/1",
    Component: LeadDetailPage,
    detail: {
      "/leads/1": {
        ...base,
        id: "1",
        lastName: "Demir",
        fullName: "Can Demir",
        company: "Beta A.Ş.",
        source: "web",
        status: "new",
      },
    },
  },
  {
    type: "deal",
    path: "/app/deals/:id",
    route: "/app/deals/1",
    Component: DealDetailPage,
    detail: {
      "/deals/1": {
        ...base,
        id: "1",
        name: "Büyük fırsat",
        accountId: "a1",
        accountName: "Acme",
        pipelineId: "p1",
        pipelineName: "Satış",
        stageId: "s1",
        stageName: "Teklif",
        stageKind: "open",
        probability: 50,
        currency: "TRY",
      },
    },
  },
  {
    type: "case",
    path: "/app/cases/:id",
    route: "/app/cases/1",
    Component: CaseDetailPage,
    detail: { "/cases/1": caseDetail("1"), "/cases/1/timeline": page([]) },
  },
  {
    type: "quote",
    path: "/app/quotes/:id",
    route: "/app/quotes/1",
    Component: QuoteDetailPage,
    detail: { "/quotes/1": { ...documentBase, id: "1", status: "draft" } },
  },
  {
    type: "order",
    path: "/app/orders/:id",
    route: "/app/orders/1",
    Component: OrderDetailPage,
    detail: {
      "/orders/1": { ...documentBase, id: "1", status: "draft", orderDate: "2026-05-01" },
    },
  },
  {
    type: "campaign",
    path: "/app/campaigns/:id",
    route: "/app/campaigns/1",
    Component: CampaignDetailPage,
    detail: { "/campaigns/1": campaign("1"), "/campaigns/1/metrics": metrics("1") },
  },
];

/** Answers the record's own GETs, the file endpoints and an empty array for everything else. */
function install(detail: Record<string, unknown> = {}) {
  client.get.mockImplementation(async (url: string) => {
    if (url in detail) return { data: detail[url] };
    if (url === "/files") return { data: page([fileItem("f1", { name: "Sözleşme.pdf" })], { pageSize: 20 }) };
    if (url === "/files/limits") return { data: FILE_LIMITS };
    return { data: [] };
  });
}

const fileListCalls = () => client.get.mock.calls.filter(([url]) => url === "/files");
const anyFileCall = () => client.get.mock.calls.some(([url]) => String(url).startsWith("/files"));

describe.each(PAGES)("$type detail page", ({ type, path, route, Component, detail }) => {
  const { read } = ATTACHMENT_PERMISSIONS[type];

  function renderPage() {
    return renderWithProviders(
      <Routes>
        <Route path={path} element={<Component />} />
      </Routes>,
      { route }
    );
  }

  beforeEach(() => {
    vi.clearAllMocks();
    install(detail);
  });
  afterEach(clearSession);

  it("has an Ekler tab; the list is only requested once the tab is opened", async () => {
    setPermissions([read]);
    renderPage();

    const tab = await screen.findByRole("tab", { name: "Ekler" });
    expect(anyFileCall()).toBe(false);

    await userEvent.click(tab);
    expect(await screen.findByText("Sözleşme.pdf")).toBeInTheDocument();
    expect(fileListCalls()).toHaveLength(1);
    expect(fileListCalls()[0]?.[1]?.params).toMatchObject({ recordType: type, recordId: "1" });
    // The tab is in the URL like the others.
    expect(screen.getByRole("tab", { name: "Ekler" })).toHaveAttribute("aria-selected", "true");
  });

  it("has no Ekler tab (and never asks for files) without the record type's read permission", async () => {
    setPermissions(["crm.reports.read"]);
    renderPage();

    // Wait for the page itself: its audit tab is always there.
    await waitFor(() => expect(screen.getAllByRole("tab").length).toBeGreaterThan(1));
    expect(screen.queryByRole("tab", { name: "Ekler" })).not.toBeInTheDocument();
    expect(anyFileCall()).toBe(false);
  });

  it("puts Ekler before the audit tab", async () => {
    setPermissions([read]);
    renderPage();
    await screen.findByRole("tab", { name: "Ekler" });
    const names = screen.getAllByRole("tab").map((el) => el.textContent);
    const attachments = names.indexOf("Ekler");
    expect(attachments).toBeGreaterThan(0);
    expect(names.findIndex((name) => /denetim|audit/i.test(name ?? ""))).toBe(attachments + 1);
  });
});

describe("gated modules", () => {
  afterEach(clearSession);

  it.each([
    ["quote", "commerce", "/quotes/1"],
    ["case", "service", "/cases/1"],
    ["campaign", "marketing", "/campaigns/1"],
  ] as const)("no %s attachments tab while the plan switches %s off", async (type, module, url) => {
    vi.clearAllMocks();
    const pageCase = PAGES.find((p) => p.type === type) as PageCase;
    install(pageCase.detail);
    act(() =>
      useAuthStore.setState({
        me: {
          ...meWith([ATTACHMENT_PERMISSIONS[type].read]),
          subscription: {
            planCode: "starter",
            planName: "Starter",
            status: "active",
            accessLevel: "full",
            modules: { [module]: false },
          },
        },
      })
    );
    renderWithProviders(
      <Routes>
        <Route path={pageCase.path} element={<pageCase.Component />} />
      </Routes>,
      { route: pageCase.route }
    );

    await waitFor(() => expect(client.get).toHaveBeenCalledWith(url));
    await waitFor(() => expect(screen.queryAllByRole("tab").length).toBeGreaterThan(0));
    expect(screen.queryByRole("tab", { name: "Ekler" })).not.toBeInTheDocument();
    expect(anyFileCall()).toBe(false);
  });
});

describe("activity edit dialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    install();
  });
  afterEach(clearSession);

  it("has an Ekler section for an existing activity (compact), listing its files", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    renderWithProviders(<ActivityFormDialog activity={activity("9")} onClose={() => undefined} />);

    const section = await screen.findByTestId("attachments-tab");
    expect(within(section).getByText("Ekler")).toBeInTheDocument();
    expect(await within(section).findByText("Sözleşme.pdf")).toBeInTheDocument();
    expect(fileListCalls()[0]?.[1]?.params).toMatchObject({ recordType: "activity", recordId: "9" });
    // Writers can drop files right in the dialog.
    expect(within(section).getByTestId("file-dropzone")).toBeInTheDocument();
  });

  it("has no Ekler section when creating (attachments need a saved activity)", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    renderWithProviders(<ActivityFormDialog onClose={() => undefined} />);

    await screen.findByRole("dialog");
    expect(screen.queryByTestId("attachments-tab")).not.toBeInTheDocument();
    expect(anyFileCall()).toBe(false);
  });

  it("has no Ekler section without the activity read permission", async () => {
    setPermissions(["crm.activities.write"]);
    renderWithProviders(<ActivityFormDialog activity={activity("9")} onClose={() => undefined} />);

    await screen.findByRole("dialog");
    expect(screen.queryByTestId("attachments-tab")).not.toBeInTheDocument();
    expect(anyFileCall()).toBe(false);
  });

  it("renaming a file inside the dialog does not submit the activity form", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    client.patch.mockResolvedValue({ data: undefined });
    renderWithProviders(<ActivityFormDialog activity={activity("9")} onClose={() => undefined} />);

    await userEvent.click(await screen.findByRole("button", { name: "Yeniden adlandır: Sözleşme.pdf" }));
    const dialog = await screen.findByRole("dialog", { name: "Dosyayı yeniden adlandır" });
    const input = within(dialog).getByRole("textbox", { name: "Dosya adı" });
    await userEvent.clear(input);
    await userEvent.type(input, "Yeni.pdf");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.patch).toHaveBeenCalledWith("/files/f1", { name: "Yeni.pdf" }));
    // The activity itself was not saved through the outer form.
    expect(client.put).not.toHaveBeenCalled();
    expect(client.post).not.toHaveBeenCalled();
  });
});
