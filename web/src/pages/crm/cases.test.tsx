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
import CasesPage from "./cases";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const rowFor = (text: string) => screen.getByText(text).closest("tr") as HTMLElement;

function lastListParams() {
  return client.get.mock.calls.filter(([url]) => url === "/cases").at(-1)?.[1].params;
}

function renderPage(route = "/app/cases") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/cases" element={<CasesPage />} />
        <Route path="/app/cases/:id" element={<div>case detail</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const ROWS = [
  caseItem("1", {
    subject: "Fatura hatalı",
    priority: "high",
    accountId: "a1",
    accountName: "Acme A.Ş.",
    slaState: "atRisk",
  }),
  caseItem("2", {
    subject: "Giriş yapamıyorum",
    status: "new",
    assignedUserId: undefined,
    assignedUserName: undefined,
    isSlaBreached: true,
    slaState: "breached",
    resolutionBreached: true,
  }),
  caseItem("3", { subject: "Eski talep", status: "closed", closedAt: "2026-09-01T10:00:00Z" }),
];

const OPEN = "new,open,pending";

describe("CasesPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /cases": () => page(ROWS),
      "GET /organization/members": () => MEMBERS,
    });
  });
  afterEach(clearSession);

  it("renders the columns with status, priority and SLA badges and detail links", async () => {
    setPermissions(["crm.cases.read", "crm.accounts.read"]);
    renderPage();
    await screen.findByText("Fatura hatalı");

    const row = rowFor("Fatura hatalı");
    expect(within(row).getByRole("link", { name: "C-2026-0001" })).toHaveAttribute(
      "href",
      "/app/cases/1"
    );
    expect(within(row).getByRole("link", { name: "Acme A.Ş." })).toHaveAttribute(
      "href",
      "/app/accounts/a1"
    );
    expect(within(row).getByText("Yüksek")).toBeInTheDocument();
    expect(within(row).getByText("Ada Lovelace")).toBeInTheDocument();
    expect(row.querySelector("[data-sla-state='atRisk']")).not.toBeNull();

    const breached = rowFor("Giriş yapamıyorum");
    expect(within(breached).getByText("Atanmamış")).toBeInTheDocument();
    expect(breached.querySelector("[data-sla-state='breached']")).not.toBeNull();

    // A closed case without a breach shows no SLA badge.
    expect(rowFor("Eski talep").querySelector("[data-sla-state]")).toBeNull();
  });

  it("requests the default sort (-createdAt) and keeps defaults out of the URL", async () => {
    setPermissions(["crm.cases.read"]);
    renderPage();
    await screen.findByText("Fatura hatalı");

    expect(lastListParams()).toEqual({ page: 1, pageSize: 25, sort: "-createdAt" });
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/cases$/);
    expect(screen.getByRole("button", { name: "Tümü" })).toHaveAttribute("aria-pressed", "true");
  });

  it("syncs sorting, paging and search with the URL and the request", async () => {
    setPermissions(["crm.cases.read"]);
    renderPage();
    await screen.findByText("Fatura hatalı");

    await userEvent.click(screen.getByRole("button", { name: /Öncelik.*sırala|sırala.*Öncelik/i }));
    await waitFor(() => expect(lastListParams().sort).toBe("priority"));
    expect(screen.getByTestId("location")).toHaveTextContent("sort=priority");

    await userEvent.click(screen.getByRole("button", { name: /Son.*sırala|Hedef.*sırala|sırala.*hedef/i }));
    await waitFor(() => expect(lastListParams().sort).toBe("dueAt"));

    await userEvent.type(screen.getByRole("searchbox"), "fatura");
    await waitFor(() => expect(lastListParams().q).toBe("fatura"));
    expect(screen.getByTestId("location")).toHaveTextContent("q=fatura");
  });

  it("restores every filter from the URL on load", async () => {
    setPermissions(["crm.cases.read"]);
    renderPage(
      "/app/cases?status=new,open&priority=high,urgent&channel=phone&assignedUserId=user-2&slaState=breached&accountId=a1&contactId=c1&page=2&pageSize=50&sort=-dueAt&q=acme"
    );
    await screen.findByText("Fatura hatalı");

    expect(client.get.mock.calls.find(([url]) => url === "/cases")?.[1].params).toEqual({
      page: 2,
      pageSize: 50,
      q: "acme",
      sort: "-dueAt",
      status: "new,open",
      priority: "high,urgent",
      channel: "phone",
      assignedUserId: "user-2",
      slaState: "breached",
      accountId: "a1",
      contactId: "c1",
    });
    expect(screen.getByRole("status")).toBeInTheDocument();
  });

  it("quick chips set the right query parameters together with the open scope", async () => {
    setPermissions(["crm.cases.read"]);
    renderPage();
    await screen.findByText("Fatura hatalı");

    await userEvent.click(screen.getByRole("button", { name: "Açık" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, sort: "-createdAt", status: OPEN })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("status=new%2Copen%2Cpending");
    expect(screen.getByRole("button", { name: "Açık" })).toHaveAttribute("aria-pressed", "true");

    await userEvent.click(screen.getByRole("button", { name: "Bana atanan" }));
    await waitFor(() => expect(lastListParams().assignedUserId).toBe("user-1"));
    expect(lastListParams()).toMatchObject({ status: OPEN, assignedUserId: "user-1" });
    expect(screen.getByRole("button", { name: "Bana atanan" })).toHaveAttribute(
      "aria-pressed",
      "true"
    );

    await userEvent.click(screen.getByRole("button", { name: "Atanmamış" }));
    await waitFor(() => expect(lastListParams().unassigned).toBe(true));
    expect(lastListParams()).toMatchObject({ status: OPEN, unassigned: true });
    expect(lastListParams().assignedUserId).toBeUndefined();

    await userEvent.click(screen.getByRole("button", { name: "SLA aşıldı" }));
    await waitFor(() => expect(lastListParams().slaState).toBe("breached"));
    expect(lastListParams()).toMatchObject({ status: OPEN, slaState: "breached" });
    expect(lastListParams().unassigned).toBeUndefined();

    await userEvent.click(screen.getByRole("button", { name: "Tümü" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, sort: "-createdAt" })
    );
  });

  it("filters by status and priority with comma separated multi values", async () => {
    setPermissions(["crm.cases.read"]);
    renderPage();
    await screen.findByText("Fatura hatalı");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Yeni" }));
    await userEvent.click(await screen.findByRole("option", { name: "Beklemede" }));
    await waitFor(() => expect(lastListParams().status).toBe("new,pending"));
  });

  it("shows the empty state and clears filters", async () => {
    installApi(client, {
      "GET /cases": () => page([]),
      "GET /organization/members": () => MEMBERS,
    });
    setPermissions(["crm.cases.read"]);
    renderPage("/app/cases?slaState=breached");
    expect(await screen.findByText("Talep bulunamadı")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/cases$/);
  });

  it("gates the New case button on crm.cases.write", async () => {
    setPermissions(["crm.cases.read"]);
    const { unmount } = renderPage();
    await screen.findByText("Fatura hatalı");
    expect(screen.queryByRole("button", { name: "Yeni talep" })).toBeNull();
    unmount();

    setPermissions(["crm.cases.read", "crm.cases.write"]);
    renderPage();
    await userEvent.click(await screen.findByRole("button", { name: "Yeni talep" }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });
});
