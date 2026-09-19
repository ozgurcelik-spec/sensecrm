import { afterEach, describe, expect, it } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useAuthStore } from "@/store/auth.store";
import type { Me } from "@/types";
import { useAnyPermission, usePermission } from "./use-permission";

function meWith(permissions: string[]): Me {
  return {
    user: {
      id: "u1",
      email: "ada@example.com",
      displayName: "Ada",
      locale: "tr",
      isPlatformAdmin: false,
    },
    organization: {
      id: "o1",
      name: "Acme",
      slug: "acme",
      defaultLocale: "tr",
      timeZone: "Europe/Istanbul",
    },
    role: { id: "r1", name: "Standard" },
    permissions,
    organizations: [{ id: "o1", name: "Acme", slug: "acme" }],
  };
}

describe("usePermission", () => {
  afterEach(() => {
    act(() => useAuthStore.setState({ me: null }));
  });

  it("is false when nobody is signed in", () => {
    const { result } = renderHook(() => usePermission("org.users.read"));
    expect(result.current).toBe(false);
  });

  it("reflects the permissions of the active organization's role", () => {
    act(() => useAuthStore.setState({ me: meWith(["org.users.read"]) }));

    expect(renderHook(() => usePermission("org.users.read")).result.current).toBe(true);
    expect(renderHook(() => usePermission("org.users.manage")).result.current).toBe(false);
  });

  it("re-renders when permissions change (e.g. after an organization switch)", () => {
    act(() => useAuthStore.setState({ me: meWith([]) }));
    const { result } = renderHook(() => usePermission("org.roles.manage"));
    expect(result.current).toBe(false);

    act(() => useAuthStore.setState({ me: meWith(["org.roles.manage"]) }));
    expect(result.current).toBe(true);
  });

  it("useAnyPermission passes when at least one key is granted", () => {
    act(() => useAuthStore.setState({ me: meWith(["crm.leads.read"]) }));

    expect(
      renderHook(() => useAnyPermission(["crm.deals.read", "crm.leads.read"])).result.current
    ).toBe(true);
    expect(renderHook(() => useAnyPermission(["crm.deals.read"])).result.current).toBe(false);
  });
});
