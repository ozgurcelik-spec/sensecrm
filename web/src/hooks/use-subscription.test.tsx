import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useAuthStore } from "@/store/auth.store";
import { SUBSCRIPTION_SYNC_INTERVAL_MS, useSubscriptionSync } from "./use-subscription";

describe("useSubscriptionSync", () => {
  const refreshMe = vi.fn(() => Promise.resolve());
  const original = useAuthStore.getState().refreshMe;

  beforeEach(() => {
    vi.useFakeTimers();
    refreshMe.mockClear();
    act(() => useAuthStore.setState({ refreshMe }));
  });
  afterEach(() => {
    act(() => useAuthStore.setState({ refreshMe: original }));
    vi.useRealTimers();
  });

  it("re-reads /me every five minutes and stops on unmount", () => {
    expect(SUBSCRIPTION_SYNC_INTERVAL_MS).toBe(5 * 60_000);
    const { unmount } = renderHook(() => useSubscriptionSync());

    vi.advanceTimersByTime(SUBSCRIPTION_SYNC_INTERVAL_MS - 1);
    expect(refreshMe).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(refreshMe).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(SUBSCRIPTION_SYNC_INTERVAL_MS);
    expect(refreshMe).toHaveBeenCalledTimes(2);

    unmount();
    vi.advanceTimersByTime(SUBSCRIPTION_SYNC_INTERVAL_MS * 3);
    expect(refreshMe).toHaveBeenCalledTimes(2);
  });

  it("skips the tick while the tab is hidden", () => {
    renderHook(() => useSubscriptionSync());
    const spy = vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    vi.advanceTimersByTime(SUBSCRIPTION_SYNC_INTERVAL_MS);
    expect(refreshMe).not.toHaveBeenCalled();
    spy.mockReturnValue("visible");
    vi.advanceTimersByTime(SUBSCRIPTION_SYNC_INTERVAL_MS);
    expect(refreshMe).toHaveBeenCalledTimes(1);
    spy.mockRestore();
  });
});
