import type { ComponentType } from "react";
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
import { caseItem } from "@/test/service";
import AccountDetailPage from "./account-detail";
import ContactDetailPage from "./contact-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const base = { ownerUserId: "user-1", ownerName: "Ada Lovelace", createdAt: "2026-05-01T10:00:00Z" };

interface Scenario {
  name: string;
  path: string;
  Page: ComponentType;
  read: string;
  record: object;
  /** Query parameter the tab (and its request) uses. */
  param: "accountId" | "contactId";
}

const SCENARIOS: Scenario[] = [
  {
    name: "account",
    path: "accounts",
    Page: AccountDetailPage,
    read: "crm.accounts.read",
    record: { id: "r1", name: "Acme Ltd", ...base },
    param: "accountId",
  },
  {
    name: "contact",
    path: "contacts",
    Page: ContactDetailPage,
    read: "crm.contacts.read",
    record: {
      id: "r1",
      lastName: "Yılmaz",
      fullName: "Ayşe Yılmaz",
      accountId: "a1",
      accountName: "Acme Ltd",
      ...base,
    },
    param: "contactId",
  },
];

const casesRequests = (param: string) =>
  client.get.mock.calls.filter(([url, config]) => url === "/cases" && config?.params?.[param]);

describe.each(SCENARIOS)("$name detail: Talepler tab and Talep aç", (s) => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      [`GET /${s.path}/r1`]: () => s.record,
      "GET /cases": () =>
        page(
          [
            caseItem("1", { subject: "Fatura hatalı", slaState: "atRisk" }),
            caseItem("2", {
              subject: "Giriş sorunu",
              status: "closed",
              closedAt: "2026-09-01T10:00:00Z",
            }),
          ],
          { totalCount: 7 }
        ),
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
      "GET /contacts": () => page([]),
      "GET /audit": () => ({ items: [], total: 0 }),
    });
  });
  afterEach(clearSession);

  function renderPage(route = `/app/${s.path}/r1`) {
    return renderWithProviders(
      <>
        <Routes>
          <Route path={`/app/${s.path}/:id`} element={<s.Page />} />
          <Route path="/app/cases" element={<div>cases list</div>} />
          <Route path="/app/cases/:id" element={<div>case detail</div>} />
        </Routes>
        <LocationDisplay />
      </>,
      { route }
    );
  }

  it("lists the record's cases with the total in the tab label and a 'view all' link", async () => {
    setPermissions([s.read, "crm.cases.read"]);
    renderPage();

    const tab = await screen.findByRole("tab", { name: "Talepler (7)" });
    await userEvent.click(tab);

    expect(await screen.findByText("Fatura hatalı")).toBeInTheDocument();
    const row = screen.getByText("Fatura hatalı").closest("tr") as HTMLElement;
    expect(within(row).getByRole("link", { name: "C-2026-0001" })).toHaveAttribute(
      "href",
      "/app/cases/1"
    );
    expect(within(row).getByText("Risk altında")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tümünü gör" })).toHaveAttribute(
      "href",
      `/app/cases?${s.param}=r1`
    );
    // The label and the tab use the same query: 50 rows, filtered by this record.
    for (const [, config] of casesRequests(s.param)) {
      expect(config.params).toEqual({ [s.param]: "r1", page: 1, pageSize: 50 });
    }
  });

  it("has no Talepler tab and requests no cases without crm.cases.read", async () => {
    setPermissions([s.read]);
    renderPage();
    await screen.findByRole("tab", { name: "Denetim" });

    expect(screen.queryByRole("tab", { name: /Talepler/ })).toBeNull();
    expect(client.get.mock.calls.some(([url]) => url === "/cases")).toBe(false);
  });

  it("offers 'Talep aç' only with crm.cases.write and opens the dialog preset with the record", async () => {
    setPermissions([s.read, "crm.cases.read"]);
    const first = renderPage();
    await screen.findByRole("tab", { name: /Talepler/ });
    expect(screen.queryByRole("button", { name: "Talep aç" })).toBeNull();
    first.unmount();

    setPermissions([s.read, "crm.cases.read", "crm.cases.write", "crm.accounts.read", "crm.contacts.read"]);
    renderPage();
    await userEvent.click(await screen.findByRole("button", { name: "Talep aç" }));

    const dialog = await screen.findByRole("dialog");
    // The record is fixed (disabled); a contact page also fixes the contact's account.
    if (s.name === "account") expect(within(dialog).getByDisplayValue("Acme Ltd")).toBeDisabled();
    else {
      expect(within(dialog).getByDisplayValue("Ayşe Yılmaz")).toBeDisabled();
      expect(within(dialog).getByDisplayValue("Acme Ltd")).toBeDisabled();
    }

    await userEvent.type(within(dialog).getByRole("textbox", { name: /Konu/ }), "Destek");
    client.post.mockImplementationOnce(async () => ({ data: { id: "new-id" } }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(client.post.mock.calls[0]?.[0]).toBe("/cases");
    expect(client.post.mock.calls[0]?.[1]).toMatchObject(
      s.name === "account"
        ? { subject: "Destek", accountId: "r1" }
        : { subject: "Destek", accountId: "a1", contactId: "r1" }
    );
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/app/cases/new-id")
    );
  });
});
