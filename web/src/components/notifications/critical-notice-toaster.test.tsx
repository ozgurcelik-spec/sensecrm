import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { navigateApp } from "@/lib/app-navigator";
import { toast } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, page, setPermissions, type MockClient } from "@/test/crm";
import { notification } from "@/test/notifications";
import type { AppNotification, NotificationUnreadCount } from "@/types";
import { CriticalNoticeToaster } from "./critical-notice-toaster";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(() => ({ id: "t", dismiss: vi.fn() })), toastApiError: vi.fn() }));
vi.mock("@/lib/app-navigator", () => ({ navigateApp: vi.fn(), setAppNavigator: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const toastMock = vi.mocked(toast);
const listCalls = () => client.get.mock.calls.filter(([url]) => url === "/notifications").length;

const critical = (id: string, overrides: Partial<AppNotification> = {}) =>
  notification(id, {
    kind: "case.sla_breached",
    severity: "critical",
    title: `Kritik ${id}`,
    body: `Talep ${id} SLA süresini aştı`,
    link: `/app/cases/${id}`,
    ...overrides,
  });

const counts = (newestCriticalId?: string): NotificationUnreadCount => ({
  unreadCount: 2,
  criticalUnreadCount: newestCriticalId ? 1 : 0,
  ...(newestCriticalId ? { newestCriticalId } : {}),
});

let unread: AppNotification[];

describe("CriticalNoticeToaster", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.sessionStorage.clear();
    unread = [critical("c1")];
    installApi(client, {
      "GET /notifications": () => page(unread),
      "POST /notifications/c2/read": () => undefined,
      "POST /notifications/c3/read": () => undefined,
      "GET /notifications/unread-count": () => counts(),
    });
    setPermissions([]);
  });
  afterEach(() => clearSession());

  it("does not toast what is already unread when the page opens (first poll only remembers it)", async () => {
    renderWithProviders(<CriticalNoticeToaster counts={counts("c1")} />);
    await waitFor(() => expect(listCalls()).toBe(1));
    expect(toastMock).not.toHaveBeenCalled();
    expect(JSON.parse(window.sessionStorage.getItem("notifications.toasted") ?? "[]")).toContain("c1");
  });

  it("does nothing while there is no critical notification", async () => {
    renderWithProviders(<CriticalNoticeToaster counts={counts()} />);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(listCalls()).toBe(0);
    expect(toastMock).not.toHaveBeenCalled();
  });

  it("shows one toast for a new critical notification and never for the same id twice", async () => {
    const view = renderWithProviders(<CriticalNoticeToaster counts={counts("c1")} />);
    await waitFor(() => expect(listCalls()).toBe(1));

    unread = [critical("c2"), critical("c1")];
    view.rerender(<CriticalNoticeToaster counts={counts("c2")} />);
    await waitFor(() => expect(toastMock).toHaveBeenCalledTimes(1));
    expect(toastMock.mock.calls[0]?.[0]).toMatchObject({ title: "Kritik c2", variant: "destructive" });

    // The same newest id again (next poll) and an unrelated re-render: no second toast, no new lookup.
    const lookups = listCalls();
    view.rerender(<CriticalNoticeToaster counts={{ ...counts("c2"), unreadCount: 3 }} />);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(toastMock).toHaveBeenCalledTimes(1);
    expect(listCalls()).toBe(lookups);
  });

  it("asks for unread critical notifications only", async () => {
    renderWithProviders(<CriticalNoticeToaster counts={counts("c1")} />);
    await waitFor(() => expect(listCalls()).toBe(1));
    const call = client.get.mock.calls.find(([url]) => url === "/notifications");
    expect(call?.[1]).toEqual({ params: { status: "unread", severity: "critical", page: 1, pageSize: 10 } });
  });

  it("summarizes several new critical notifications of one round in a single toast", async () => {
    const view = renderWithProviders(<CriticalNoticeToaster counts={counts("c1")} />);
    await waitFor(() => expect(listCalls()).toBe(1));

    unread = [critical("c3"), critical("c2"), critical("c1")];
    view.rerender(<CriticalNoticeToaster counts={counts("c3")} />);
    await waitFor(() => expect(toastMock).toHaveBeenCalledTimes(1));
    expect(toastMock.mock.calls[0]?.[0]).toMatchObject({ title: "2 yeni kritik bildirim" });
  });

  it("remembers toasted ids for the browser session", async () => {
    window.sessionStorage.setItem("notifications.toasted", JSON.stringify(["c2"]));
    const view = renderWithProviders(<CriticalNoticeToaster counts={counts()} />);
    unread = [critical("c2")];
    view.rerender(<CriticalNoticeToaster counts={counts("c2")} />);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(toastMock).not.toHaveBeenCalled();
  });

  it("works when session storage is unavailable (memory only)", async () => {
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    const setSpy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    try {
      const view = renderWithProviders(<CriticalNoticeToaster counts={counts("c1")} />);
      await waitFor(() => expect(listCalls()).toBe(1));
      unread = [critical("c2"), critical("c1")];
      view.rerender(<CriticalNoticeToaster counts={counts("c2")} />);
      await waitFor(() => expect(toastMock).toHaveBeenCalledTimes(1));
    } finally {
      spy.mockRestore();
      setSpy.mockRestore();
    }
  });

  it("the toast's View action marks the notification read and opens its link", async () => {
    const user = userEvent.setup();
    const view = renderWithProviders(<CriticalNoticeToaster counts={counts("c1")} />);
    await waitFor(() => expect(listCalls()).toBe(1));
    unread = [critical("c2"), critical("c1")];
    view.rerender(<CriticalNoticeToaster counts={counts("c2")} />);
    await waitFor(() => expect(toastMock).toHaveBeenCalled());

    // The toast body is a React element: render it to reach the action.
    const description = toastMock.mock.calls[0]?.[0].description;
    const shown = renderWithProviders(<>{description}</>);
    await user.click(shown.getByRole("button", { name: "Görüntüle" }));
    expect(navigateApp).toHaveBeenCalledWith("/app/cases/c2");
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/notifications/c2/read"));
    expect(screen.queryByText("Kritik c2")).not.toBeInTheDocument();
  });
});
