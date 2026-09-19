import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor } from "@testing-library/react";
import { focusManager } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, setPermissions, type MockClient } from "@/test/crm";
import { APPROVAL_POLL_INTERVAL_MS } from "@/hooks/use-approvals";
import { ApprovalsBell } from "./approvals-bell";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const summaryCalls = () =>
  client.get.mock.calls.filter(([url]) => url === "/approvals/summary").length;

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe("ApprovalsBell", () => {
  let pending = 3;

  beforeEach(() => {
    vi.clearAllMocks();
    pending = 3;
    installApi(client, { "GET /approvals/summary": () => ({ pendingCount: pending }) });
  });
  afterEach(() => {
    focusManager.setFocused(undefined);
    vi.useRealTimers();
    clearSession();
  });

  it("shows the pending count and links to the approvals page", async () => {
    setPermissions(["crm.approvals.decide"]);
    renderWithProviders(<ApprovalsBell />);

    const link = await screen.findByRole("link", { name: "Bekleyen onaylar: 3" });
    expect(link).toHaveAttribute("href", "/app/approvals");
    expect(link.parentElement).toHaveTextContent("3");
  });

  it("is visible to an approver without pending approvals, but without a count", async () => {
    pending = 0;
    setPermissions(["crm.approvals.decide"]);
    renderWithProviders(<ApprovalsBell />);

    const link = await screen.findByRole("link", { name: "Bekleyen onaylar" });
    expect(link.parentElement).not.toHaveTextContent(/\d/);
  });

  it("shows up for a user without the permission who has a pending approval", async () => {
    setPermissions([]);
    renderWithProviders(<ApprovalsBell />);
    expect(await screen.findByRole("link", { name: "Bekleyen onaylar: 3" })).toBeInTheDocument();
  });

  it("is hidden without the permission and without pending approvals", async () => {
    pending = 0;
    setPermissions([]);
    renderWithProviders(<ApprovalsBell />);
    await waitFor(() => expect(summaryCalls()).toBe(1));
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
  });

  it("polls the summary every 60 seconds while the tab is visible", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setPermissions(["crm.approvals.decide"]);
    renderWithProviders(<ApprovalsBell />);
    await screen.findByRole("link", { name: "Bekleyen onaylar: 3" });
    expect(summaryCalls()).toBe(1);
    expect(APPROVAL_POLL_INTERVAL_MS).toBe(60_000);

    await advance(30_000);
    expect(summaryCalls()).toBe(1);

    pending = 5;
    await advance(31_000);
    await waitFor(() => expect(summaryCalls()).toBe(2));
    expect(await screen.findByRole("link", { name: "Bekleyen onaylar: 5" })).toBeInTheDocument();

    await advance(60_000);
    await waitFor(() => expect(summaryCalls()).toBe(3));
  });

  it("does not poll while the tab is hidden and refetches when it becomes visible again", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setPermissions(["crm.approvals.decide"]);
    renderWithProviders(<ApprovalsBell />);
    await screen.findByRole("link", { name: "Bekleyen onaylar: 3" });
    expect(summaryCalls()).toBe(1);

    act(() => focusManager.setFocused(false));
    pending = 7;
    await advance(180_000);
    expect(summaryCalls()).toBe(1);

    act(() => focusManager.setFocused(true));
    await waitFor(() => expect(summaryCalls()).toBe(2));
    expect(await screen.findByRole("link", { name: "Bekleyen onaylar: 7" })).toBeInTheDocument();
  });
});
