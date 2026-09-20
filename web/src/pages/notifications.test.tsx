import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, LocationDisplay, page, setPermissions, type MockClient } from "@/test/crm";
import { notification } from "@/test/notifications";
import type { AppNotification } from "@/types";
import NotificationsPage from "./notifications";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const listParams = () =>
  client.get.mock.calls.filter(([url]) => url === "/notifications").map(([, config]) => config?.params);
const lastListParams = () => listParams().at(-1);

let items: AppNotification[];
let totalCount: number | undefined;

function render(route = "/app/notifications") {
  return renderWithProviders(
    <>
      <NotificationsPage />
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("NotificationsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    items = [
      notification("n1", { title: "Onayınız bekleniyor", link: "/app/approvals" }),
      notification("n2", { title: "Talep 12 SLA süresini aştı", kind: "case.sla_breached", severity: "critical", link: "/app/cases/c1", isRead: true }),
      notification("n3", { title: "Plan askıya alındı", kind: "tenant.suspended", severity: "critical", isMandatory: true, link: "/app/settings/plan" }),
    ];
    totalCount = undefined;
    installApi(client, {
      "GET /notifications": () => page(items, { totalCount }),
      "GET /notifications/unread-count": () => ({ unreadCount: 2, criticalUnreadCount: 1 }),
      "POST /notifications/n1/read": () => undefined,
      "POST /notifications/read-all": () => ({ updatedCount: 2 }),
      "DELETE /notifications/n2": () => undefined,
    });
    setPermissions([]);
  });
  afterEach(() => clearSession());

  it("lists the notifications with severity, kind, unread marker and the mandatory badge", async () => {
    render();
    expect(await screen.findByText("Onayınız bekleniyor")).toBeInTheDocument();
    expect(screen.getByText("Talep 12 SLA süresini aştı", { selector: "button" })).toBeInTheDocument();
    // The mandatory kind carries the badge; the others do not.
    const row = screen.getByText("Plan askıya alındı").closest("tr") as HTMLElement;
    expect(within(row).getByText("Zorunlu")).toBeInTheDocument();
    expect(within(screen.getByText("Onayınız bekleniyor").closest("tr") as HTMLElement).queryByText("Zorunlu")).toBeNull();
    // Unread rows carry the "Yeni" badge, the read one does not.
    expect(within(screen.getByText("Onayınız bekleniyor").closest("tr") as HTMLElement).getByText("Yeni")).toBeInTheDocument();
    expect(within(screen.getByText("Talep 12 SLA süresini aştı").closest("tr") as HTMLElement).queryByText("Yeni")).toBeNull();
  });

  it("requests page 1 with the default page size and no filter", async () => {
    render();
    await screen.findByText("Onayınız bekleniyor");
    expect(lastListParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("keeps tab, kind and severity in the URL and sends them to the server", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("Onayınız bekleniyor");

    await user.click(screen.getByRole("tab", { name: "Okunmamış" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "unread" }));
    expect(screen.getByTestId("location")).toHaveTextContent("status=unread");

    await user.click(screen.getByRole("combobox", { name: "Önem" }));
    await user.click(await screen.findByRole("option", { name: "Kritik" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "unread", severity: "critical" }));
    expect(screen.getByTestId("location")).toHaveTextContent("severity=critical");

    await user.click(screen.getByRole("combobox", { name: "Tür" }));
    await user.click(await screen.findByRole("option", { name: "SLA aşıldı" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "unread", severity: "critical", kind: "case.sla_breached" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("kind=case.sla_breached");

    await user.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "unread" }));
  });

  it("restores the filters from the URL", async () => {
    render("/app/notifications?status=read&severity=warning&kind=case.assigned");
    await screen.findByText("Onayınız bekleniyor");
    expect(lastListParams()).toEqual({
      page: 1,
      pageSize: 25,
      status: "read",
      severity: "warning",
      kind: "case.assigned",
    });
    expect(screen.getByRole("tab", { name: "Okunmuş" })).toHaveAttribute("aria-selected", "true");
  });

  it("pages through the list (page in the URL)", async () => {
    totalCount = 60;
    const user = userEvent.setup();
    render();
    await screen.findByText("Onayınız bekleniyor");
    await user.click(screen.getByRole("button", { name: "2" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 2, pageSize: 25 }));
    expect(screen.getByTestId("location")).toHaveTextContent("page=2");
  });

  it("marks one notification read from its row", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("Onayınız bekleniyor");
    await user.click(screen.getByRole("button", { name: "Okundu işaretle: Onayınız bekleniyor" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/n1/read"));
    // Already read notifications have no such action.
    expect(screen.queryByRole("button", { name: "Okundu işaretle: Talep 12 SLA süresini aştı" })).not.toBeInTheDocument();
  });

  it("hides (deletes) a notification through DELETE", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("Talep 12 SLA süresini aştı");
    await user.click(screen.getByRole("button", { name: "Gizle: Talep 12 SLA süresini aştı" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/notifications/n2"));
  });

  it("marks everything read", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("Onayınız bekleniyor");
    await user.click(screen.getByRole("button", { name: "Tümünü okundu işaretle" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/read-all", {}));
  });

  it("marks only the filtered kind read", async () => {
    const user = userEvent.setup();
    render("/app/notifications?kind=case.sla_breached");
    await screen.findByText("Onayınız bekleniyor");
    await user.click(screen.getByRole("button", { name: "Tümünü okundu işaretle" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/read-all", { kind: "case.sla_breached" }));
  });

  it("opens the record a notification is about (and marks it read) from its title", async () => {
    const user = userEvent.setup();
    render();
    await user.click(await screen.findByRole("button", { name: "Onayınız bekleniyor" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/n1/read"));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/approvals");
  });

  it("shows no link for a notification whose link is not an in-app path", async () => {
    items = [notification("n1", { title: "Dış bağlantı", link: "https://evil.example/app/x" })];
    render();
    expect(await screen.findByText("Dış bağlantı")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Dış bağlantı" })).not.toBeInTheDocument();
  });

  it("shows the empty states", async () => {
    items = [];
    render("/app/notifications?status=unread");
    expect(await screen.findByText("Okunmamış bildirim yok")).toBeInTheDocument();
  });

  it("shows a retryable error when the list cannot be loaded", async () => {
    client.get.mockImplementation(async () => {
      throw new Error("network");
    });
    render();
    expect(await screen.findByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });

  it("links to the preferences page", async () => {
    render();
    await screen.findByText("Onayınız bekleniyor");
    expect(screen.getByRole("link", { name: "Bildirim tercihleri" })).toHaveAttribute(
      "href",
      "/app/notifications/preferences"
    );
  });
});
