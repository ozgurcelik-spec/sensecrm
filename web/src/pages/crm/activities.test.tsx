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
import { activity } from "@/test/activities";
import ActivitiesPage from "./activities";

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
  return client.get.mock.calls.filter(([url]) => url === "/activities").at(-1)?.[1].params;
}

function renderPage(route = "/app/activities") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/activities" element={<ActivitiesPage />} />
        <Route path="/app/accounts/:id" element={<div>account</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const ROWS = [
  activity("1", {
    subject: "Teklifi gönder",
    dueAt: "2026-05-02T09:00:00Z",
    isOverdue: true,
    relatedType: "account",
    relatedId: "a1",
    relatedName: "Acme Ltd",
    priority: "high",
  }),
  activity("2", { type: "note", subject: "Toplantı notu", status: "completed" }),
  activity("3", {
    type: "call",
    subject: "Müşteriyi ara",
    startAt: "2099-01-01T09:00:00Z",
    relatedType: "deal",
    relatedId: "d1",
    relatedName: "",
  }),
];

describe("ActivitiesPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /activities": () => page(ROWS),
      "GET /organization/members": () => MEMBERS,
    });
  });
  afterEach(clearSession);

  it("renders the columns: type, related link, overdue date in red, assignee, priority and status", async () => {
    setPermissions(["crm.activities.read", "crm.accounts.read"]);
    renderPage();
    await screen.findByText("Teklifi gönder");

    const row = rowFor("Teklifi gönder");
    expect(within(row).getByText("Görev")).toBeInTheDocument();
    expect(within(row).getByRole("link", { name: "Acme Ltd" })).toHaveAttribute(
      "href",
      "/app/accounts/a1"
    );
    expect(row.querySelector("[data-overdue='true']")).not.toBeNull();
    expect(within(row).getByText("Ada Lovelace")).toBeInTheDocument();
    expect(within(row).getByText("Yüksek")).toBeInTheDocument();
    expect(within(row).getByText("Açık")).toBeInTheDocument();

    // A future call is not overdue; its related record was deleted (no name) so there is no link.
    const call = rowFor("Müşteriyi ara");
    expect(call.querySelector("[data-overdue='true']")).toBeNull();
    expect(within(call).getByText(/Silinmiş kayıt/)).toBeInTheDocument();
  });

  it("syncs the type tabs and quick filters with the URL and the request", async () => {
    setPermissions(["crm.activities.read"]);
    renderPage("/app/activities?page=2");
    await screen.findByText("Teklifi gönder");

    await userEvent.click(screen.getByRole("tab", { name: "Görev" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, type: "task" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/activities?type=task");
    expect(screen.getByRole("tab", { name: "Görev" })).toHaveAttribute("aria-selected", "true");

    await userEvent.click(screen.getByRole("button", { name: "Geciken" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, type: "task", overdue: true })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("type=task&overdue=true");
    expect(screen.getByRole("button", { name: "Geciken" })).toHaveAttribute("aria-pressed", "true");

    // "Bugün" replaces "Geciken" and sends the organization's day bounds as ISO instants.
    await userEvent.click(screen.getByRole("button", { name: "Bugün" }));
    await waitFor(() => expect(lastListParams().dueFrom).toBeDefined());
    const params = lastListParams();
    expect(params.overdue).toBeUndefined();
    expect(params.dueFrom).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
    expect(new Date(params.dueTo).getTime()).toBeGreaterThan(new Date(params.dueFrom).getTime());
    expect(screen.getByTestId("location")).toHaveTextContent("type=task&due=today");

    await userEvent.click(screen.getByRole("button", { name: "Bana atanan" }));
    await waitFor(() => expect(lastListParams().assignedUserId).toBe("user-1"));
    expect(screen.getByTestId("location")).toHaveTextContent("assignedUserId=user-1");

    // "Tümü" drops the quick filters but keeps the type tab.
    await userEvent.click(screen.getByRole("button", { name: "Tümü" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, type: "task" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/activities?type=task");
  });

  it("restores the filters from the URL on load", async () => {
    setPermissions(["crm.activities.read"]);
    renderPage("/app/activities?type=call&overdue=true&status=open");
    await screen.findByText("Teklifi gönder");

    expect(client.get.mock.calls.find(([url]) => url === "/activities")?.[1].params).toEqual({
      page: 1,
      pageSize: 25,
      type: "call",
      status: "open",
      overdue: true,
    });
    expect(screen.getByRole("tab", { name: "Arama" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("button", { name: "Geciken" })).toHaveAttribute("aria-pressed", "true");
  });

  it("offers complete and reopen per row, but none for a note", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    renderPage();
    await screen.findByText("Teklifi gönder");

    expect(within(rowFor("Teklifi gönder")).getByRole("button", { name: "Tamamla" })).toBeVisible();
    expect(
      within(rowFor("Toplantı notu")).queryByRole("button", { name: /Tamamla|Yeniden aç/ })
    ).not.toBeInTheDocument();
  });

  it("is read-only without write permission", async () => {
    setPermissions(["crm.activities.read"]);
    renderPage();
    await screen.findByText("Teklifi gönder");

    expect(screen.queryByRole("button", { name: "Yeni aktivite" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Tamamla" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
  });

  it("marks a task completed at once and restores it when the request fails", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    let rejectComplete: (error: Error) => void = () => undefined;
    installApi(client, {
      "GET /activities": () => page([activity("1", { subject: "Teklifi gönder" })]),
      "GET /organization/members": () => MEMBERS,
      "POST /activities/1/complete": () =>
        new Promise((_, reject) => {
          rejectComplete = reject;
        }),
    });
    renderPage();
    await screen.findByText("Teklifi gönder");

    await userEvent.click(
      within(rowFor("Teklifi gönder")).getByRole("button", { name: "Tamamla" })
    );
    // Optimistic: the row is already completed while the request is still pending.
    expect(await within(rowFor("Teklifi gönder")).findByText("Tamamlandı")).toBeInTheDocument();
    expect(
      within(rowFor("Teklifi gönder")).getByRole("button", { name: "Yeniden aç" })
    ).toBeInTheDocument();

    rejectComplete(new Error("boom"));
    expect(await within(rowFor("Teklifi gönder")).findByText("Açık")).toBeInTheDocument();
  });
});
