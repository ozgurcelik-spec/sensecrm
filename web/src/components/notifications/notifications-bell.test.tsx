import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { focusManager } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, LocationDisplay, page, problem, setPermissions, type MockClient } from "@/test/crm";
import { notification } from "@/test/notifications";
import { setMe, platformMe, subscription } from "@/test/platform";
import { NOTIFICATION_POLL_INTERVAL_MS } from "@/lib/notifications";
import type { AppNotification, NotificationUnreadCount } from "@/types";
import { NotificationsBell } from "./notifications-bell";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const calls = (method: "get" | "post", url: string) =>
  client[method].mock.calls.filter(([called]) => called === url).length;

let counts: NotificationUnreadCount;
let items: AppNotification[];

function install(extra: Record<string, () => unknown> = {}) {
  installApi(client, {
    "GET /notifications/unread-count": () => counts,
    "GET /notifications": () => page(items),
    "POST /notifications/n1/read": () => undefined,
    "POST /notifications/n2/read": () => undefined,
    "POST /notifications/read-all": () => ({ updatedCount: 2 }),
    ...extra,
  });
}

function renderBell() {
  return renderWithProviders(
    <>
      <NotificationsBell />
      <LocationDisplay />
    </>
  );
}

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe("NotificationsBell", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    counts = { unreadCount: 3, criticalUnreadCount: 0 };
    items = [
      notification("n1", { title: "Onayınız bekleniyor", link: "/app/approvals" }),
      notification("n2", { title: "Talep atandı", link: "/app/cases/c1", isRead: true }),
    ];
    install();
    setPermissions([]);
  });
  afterEach(() => {
    focusManager.setFocused(undefined);
    vi.restoreAllMocks();
    vi.useRealTimers();
    clearSession();
  });

  it("shows the unread count as a badge on the bell", async () => {
    renderBell();
    const bell = await screen.findByRole("button", { name: "Bildirimler: 3 okunmamış" });
    expect(bell.parentElement).toHaveTextContent("3");
  });

  it("shows 99+ from 100 unread", async () => {
    counts = { unreadCount: 120, criticalUnreadCount: 0 };
    renderBell();
    const bell = await screen.findByRole("button", { name: "Bildirimler: 120 okunmamış" });
    expect(bell.parentElement).toHaveTextContent("99+");
  });

  it("has no count without unread notifications", async () => {
    counts = { unreadCount: 0, criticalUnreadCount: 0 };
    renderBell();
    const bell = await screen.findByRole("button", { name: "Bildirimler" });
    await waitFor(() => expect(calls("get", "/notifications/unread-count")).toBe(1));
    expect(bell.parentElement).not.toHaveTextContent(/\d/);
  });

  it("requests the list only once the menu is opened and shows the newest items", async () => {
    const user = userEvent.setup();
    renderBell();
    const bell = await screen.findByRole("button", { name: /Bildirimler/ });
    expect(calls("get", "/notifications")).toBe(0);

    await user.click(bell);
    expect(await screen.findByText("Onayınız bekleniyor")).toBeInTheDocument();
    expect(screen.getByText("Talep atandı")).toBeInTheDocument();
    const listCall = client.get.mock.calls.find(([url]) => url === "/notifications");
    expect(listCall?.[1]).toEqual({ params: { page: 1, pageSize: 10 } });
    expect(screen.getByRole("link", { name: "Tümünü gör" })).toHaveAttribute("href", "/app/notifications");
    expect(screen.getByRole("link", { name: "Bildirim tercihleri" })).toHaveAttribute(
      "href",
      "/app/notifications/preferences"
    );
  });

  it("marks a notification read and navigates to its link when its row is clicked", async () => {
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
    await user.click(await screen.findByText("Onayınız bekleniyor"));

    await waitFor(() => expect(calls("post", "/notifications/n1/read")).toBe(1));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/approvals");
  });

  it("does not mark an already read notification again but still follows its link", async () => {
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
    await user.click(await screen.findByText("Talep atandı"));

    expect(screen.getByTestId("location")).toHaveTextContent("/app/cases/c1");
    expect(calls("post", "/notifications/n2/read")).toBe(0);
  });

  it.each([["https://evil.example/app/x"], ["//evil.example/app/x"], ["/login"], ["javascript:alert(1)"]])(
    "ignores the link %s (marks read, does not navigate)",
    async (link) => {
      items = [notification("n1", { title: "Zararlı", link })];
      const user = userEvent.setup();
      renderBell();
      await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
      await user.click(await screen.findByText("Zararlı"));

      await waitFor(() => expect(calls("post", "/notifications/n1/read")).toBe(1));
      expect(screen.getByTestId("location")).toHaveTextContent("/");
      expect(screen.getByTestId("location").textContent).toBe("/");
    }
  );

  it("marks everything read from the menu", async () => {
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
    await user.click(await screen.findByRole("button", { name: "Tümünü okundu işaretle" }));

    await waitFor(() => expect(calls("post", "/notifications/read-all")).toBe(1));
  });

  it("shows an empty state", async () => {
    items = [];
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
    expect(await screen.findByText("Henüz bildiriminiz yok")).toBeInTheDocument();
  });

  it("shows a retryable error state when the list fails", async () => {
    install({ "GET /notifications": () => problem(500, { status: 500, title: "boom" }) as unknown as never });
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
    expect(await screen.findByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });

  it("makes no request at all while the tenant is blocked (accessLevel none)", async () => {
    setMe(platformMe([], { subscription: subscription({ accessLevel: "none" }) }));
    renderBell();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalled();
  });

  describe("polling", () => {
    beforeEach(() => {
      // 0.5 = no offset: exactly 60 s.
      vi.spyOn(Math, "random").mockReturnValue(0.5);
    });

    it("polls the unread count every 60 seconds while the tab is visible and follows changes", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      renderBell();
      await screen.findByRole("button", { name: "Bildirimler: 3 okunmamış" });
      expect(NOTIFICATION_POLL_INTERVAL_MS).toBe(60_000);
      expect(calls("get", "/notifications/unread-count")).toBe(1);

      await advance(30_000);
      expect(calls("get", "/notifications/unread-count")).toBe(1);

      counts = { unreadCount: 5, criticalUnreadCount: 0 };
      await advance(31_000);
      await waitFor(() => expect(calls("get", "/notifications/unread-count")).toBe(2));
      expect(await screen.findByRole("button", { name: "Bildirimler: 5 okunmamış" })).toBeInTheDocument();
    });

    it("keeps the poll inside +-10 % of a minute", async () => {
      const { notificationPollDelay } = await import("@/lib/notifications");
      expect(notificationPollDelay(() => 0)).toBe(54_000);
      expect(notificationPollDelay(() => 0.5)).toBe(60_000);
      expect(notificationPollDelay(() => 1)).toBe(66_000);
    });

    it("does not poll while the tab is hidden and refetches when it is visible again", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      renderBell();
      await screen.findByRole("button", { name: "Bildirimler: 3 okunmamış" });

      act(() => focusManager.setFocused(false));
      counts = { unreadCount: 7, criticalUnreadCount: 0 };
      await advance(180_000);
      expect(calls("get", "/notifications/unread-count")).toBe(1);

      act(() => focusManager.setFocused(true));
      await waitFor(() => expect(calls("get", "/notifications/unread-count")).toBe(2));
      expect(await screen.findByRole("button", { name: "Bildirimler: 7 okunmamış" })).toBeInTheDocument();
    });

    it("refetches the open list when the polled count changes", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      renderBell();
      await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
      await screen.findByText("Onayınız bekleniyor");
      expect(calls("get", "/notifications")).toBe(1);

      items = [notification("n9", { title: "Yeni gelen" }), ...items];
      counts = { unreadCount: 4, criticalUnreadCount: 0 };
      await advance(61_000);
      expect(await screen.findByText("Yeni gelen")).toBeInTheDocument();
      expect(calls("get", "/notifications")).toBe(2);
    });
  });

  describe("optimistic mark read", () => {
    it("lowers the badge at once and keeps it once the server confirms", async () => {
      let release: () => void = () => undefined;
      install({
        "POST /notifications/n1/read": () => new Promise<void>((resolve) => (release = resolve)) as unknown as never,
      });
      const user = userEvent.setup();
      renderBell();
      await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
      await user.click(await screen.findByText("Onayınız bekleniyor"));

      // The request is still open, the number already dropped.
      expect(await screen.findByRole("button", { name: "Bildirimler: 2 okunmamış" })).toBeInTheDocument();
      counts = { unreadCount: 2, criticalUnreadCount: 0 };
      release();
      await waitFor(() => expect(calls("get", "/notifications/unread-count")).toBeGreaterThan(1));
      expect(screen.getByRole("button", { name: "Bildirimler: 2 okunmamış" })).toBeInTheDocument();
    });

    it("rolls the badge back and reports the error when the request fails", async () => {
      const { toastApiError } = await import("@/hooks/use-toast");
      let fail: () => void = () => undefined;
      install({
        "POST /notifications/n1/read": () =>
          new Promise((_, reject) => {
            fail = () => reject(problem(500, { status: 500, title: "boom" }));
          }) as unknown as never,
      });
      const user = userEvent.setup();
      renderBell();
      await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
      await user.click(await screen.findByText("Onayınız bekleniyor"));
      expect(await screen.findByRole("button", { name: "Bildirimler: 2 okunmamış" })).toBeInTheDocument();

      fail();
      await waitFor(() => expect(toastApiError).toHaveBeenCalled());
      expect(await screen.findByRole("button", { name: "Bildirimler: 3 okunmamış" })).toBeInTheDocument();
    });
  });

  it("styles unread rows differently from read ones", async () => {
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole("button", { name: /Bildirimler/ }));
    const list = await screen.findByTestId("bell-list");
    await within(list).findByText("Onayınız bekleniyor");
    expect(list.querySelectorAll("[data-unread]").length).toBe(1);
  });
});
