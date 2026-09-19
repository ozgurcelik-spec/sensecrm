import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { toastApiError } from "@/hooks/use-toast";
import { activityKeys } from "@/services/activities.service";
import { installApi, page, type MockClient } from "@/test/crm";
import { activity } from "@/test/activities";
import type { Activity, ListResult } from "@/types";
import { applyStatusAction, useSetActivityStatus } from "./use-activities";

vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const LIST_KEY = activityKeys.list({ page: 1, pageSize: 25 });

function setup() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  queryClient.setQueryData(LIST_KEY, page([activity("1"), activity("2")]));
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useSetActivityStatus(), { wrapper });
  const cached = (id: string) =>
    queryClient.getQueryData<ListResult<Activity>>(LIST_KEY)?.items.find((a) => a.id === id);
  return { result, cached };
}

describe("useSetActivityStatus", () => {
  beforeEach(() => vi.clearAllMocks());

  it("updates the cached list before the request finishes", async () => {
    let finish: () => void = () => undefined;
    installApi(client, {
      "POST /activities/1/complete": () => new Promise<void>((resolve) => (finish = resolve)),
    });
    const { result, cached } = setup();

    act(() => result.current.mutate({ id: "1", action: "complete" }));

    await waitFor(() => expect(cached("1")?.status).toBe("completed"));
    expect(cached("1")?.completedAt).toBeDefined();
    expect(cached("2")?.status).toBe("open");
    expect(client.post).toHaveBeenCalledWith("/activities/1/complete");

    finish();
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it("restores the previous cache and shows the error when the request fails", async () => {
    const failure = new Error("server down");
    installApi(client, { "POST /activities/1/complete": () => failure });
    const { result, cached } = setup();
    const before = cached("1");

    act(() => result.current.mutate({ id: "1", action: "complete" }));

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(cached("1")).toEqual(before);
    expect(cached("1")?.status).toBe("open");
    expect(toastApiError).toHaveBeenCalledWith(failure);
  });

  it("reopens through its own endpoint", async () => {
    installApi(client, { "POST /activities/1/reopen": () => undefined });
    const { result, cached } = setup();

    act(() => result.current.mutate({ id: "1", action: "reopen" }));

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.post).toHaveBeenCalledWith("/activities/1/reopen");
    expect(cached("1")?.status).toBe("open");
  });
});

describe("applyStatusAction", () => {
  const now = new Date("2026-06-01T12:00:00Z");

  it("completes: status, completion time and no longer overdue", () => {
    const done = applyStatusAction(
      activity("1", { dueAt: "2026-05-01T00:00:00Z", isOverdue: true }),
      "complete",
      now
    );
    expect(done).toMatchObject({
      status: "completed",
      completedAt: "2026-06-01T12:00:00.000Z",
      isOverdue: false,
    });
  });

  it("reopens: open again and overdue again when the due date has passed", () => {
    const completed = activity("1", {
      status: "completed",
      completedAt: "2026-05-02T00:00:00Z",
      dueAt: "2026-05-01T00:00:00Z",
    });
    expect(applyStatusAction(completed, "reopen", now)).toMatchObject({
      status: "open",
      completedAt: undefined,
      isOverdue: true,
    });
    expect(
      applyStatusAction({ ...completed, dueAt: "2026-07-01T00:00:00Z" }, "reopen", now).isOverdue
    ).toBe(false);
  });
});
