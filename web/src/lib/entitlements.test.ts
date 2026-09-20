import { afterEach, describe, expect, it } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useAnyPermission, usePermission } from "@/hooks/use-permission";
import { useVisibleItems } from "@/hooks/use-nav-visibility";
import { NAV_ITEMS, SETTINGS_ITEMS } from "@/config/navigation";
import { useAuthStore } from "@/store/auth.store";
import { platformMe, subscription } from "@/test/platform";
import {
  accessLevelOf,
  isModuleOn,
  isPermissionEffective,
  permissionModule,
  usageLevel,
  usagePercent,
} from "./entitlements";

const ALL = [
  "crm.leads.read",
  "crm.leads.write",
  "crm.approvals.decide",
  "org.settings.manage",
  "org.users.manage",
  "org.workflows.manage",
  "crm.campaigns.read",
  "crm.campaigns.write",
  "crm.quotes.read",
  "crm.cases.read",
];

describe("permission -> module mapping", () => {
  it("maps the gated modules and leaves core permissions unmapped", () => {
    expect(permissionModule("crm.campaigns.read")).toBe("marketing");
    expect(permissionModule("crm.products.write")).toBe("commerce");
    expect(permissionModule("crm.quotes.read")).toBe("commerce");
    expect(permissionModule("crm.orders.write")).toBe("commerce");
    expect(permissionModule("crm.cases.read")).toBe("service");
    expect(permissionModule("org.workflows.manage")).toBe("workflows");
    expect(permissionModule("crm.approvals.decide")).toBe("workflows");
    expect(permissionModule("crm.leads.read")).toBeUndefined();
    expect(permissionModule("org.settings.manage")).toBeUndefined();
  });
});

describe("subscription helpers", () => {
  it("treats a missing subscription as everything on with full access", () => {
    expect(isModuleOn(undefined, "workflows")).toBe(true);
    expect(accessLevelOf(undefined)).toBe("full");
    const me = platformMe(["crm.leads.write"]);
    expect(isPermissionEffective(me, "crm.leads.write")).toBe(true);
  });

  it("only an explicit false switches a module off", () => {
    expect(isModuleOn(subscription({ modules: { marketing: false } }), "marketing")).toBe(false);
    expect(isModuleOn(subscription({ modules: {} }), "marketing")).toBe(true);
  });

  it("usage percent and level: orange from 80 %, red at 100 %", () => {
    expect(usagePercent(3, 5)).toBe(60);
    expect(usageLevel(3, 5)).toBe("ok");
    expect(usageLevel(4, 5)).toBe("warn");
    expect(usageLevel(79, 100)).toBe("ok");
    expect(usageLevel(80, 100)).toBe("warn");
    expect(usageLevel(99, 100)).toBe("warn");
    expect(usageLevel(100, 100)).toBe("full");
    expect(usageLevel(120, 100)).toBe("full");
    expect(usagePercent(6, 5)).toBe(120);
    // A limit of 0 means no new records at all.
    expect(usageLevel(0, 0)).toBe("full");
  });
});

describe("usePermission in plan states", () => {
  afterEach(() => {
    act(() => useAuthStore.setState({ me: null }));
  });

  function setMe(sub: ReturnType<typeof subscription>) {
    act(() => useAuthStore.setState({ me: platformMe(ALL, { subscription: sub }) }));
  }

  it("read-only access hides .write and .decide but keeps .read and .manage", () => {
    setMe(subscription({ status: "trial_expired", accessLevel: "readOnly" }));
    expect(renderHook(() => usePermission("crm.leads.write")).result.current).toBe(false);
    expect(renderHook(() => usePermission("crm.campaigns.write")).result.current).toBe(false);
    expect(renderHook(() => usePermission("crm.approvals.decide")).result.current).toBe(false);
    expect(renderHook(() => usePermission("crm.leads.read")).result.current).toBe(true);
    expect(renderHook(() => usePermission("org.settings.manage")).result.current).toBe(true);
    expect(renderHook(() => usePermission("org.users.manage")).result.current).toBe(true);
    expect(renderHook(() => useAnyPermission(["crm.leads.write", "crm.leads.read"])).result.current).toBe(true);
    expect(renderHook(() => useAnyPermission(["crm.leads.write"])).result.current).toBe(false);
  });

  it("full access leaves every permission alone", () => {
    setMe(subscription());
    expect(renderHook(() => usePermission("crm.leads.write")).result.current).toBe(true);
    expect(renderHook(() => usePermission("crm.approvals.decide")).result.current).toBe(true);
  });

  it("a disabled module withdraws all its permissions, including reads", () => {
    setMe(subscription({ modules: { workflows: false, commerce: true, service: true, marketing: false } }));
    expect(renderHook(() => usePermission("crm.campaigns.read")).result.current).toBe(false);
    expect(renderHook(() => usePermission("crm.campaigns.write")).result.current).toBe(false);
    expect(renderHook(() => usePermission("org.workflows.manage")).result.current).toBe(false);
    expect(renderHook(() => usePermission("crm.approvals.decide")).result.current).toBe(false);
    expect(renderHook(() => usePermission("crm.quotes.read")).result.current).toBe(true);
    expect(renderHook(() => usePermission("crm.cases.read")).result.current).toBe(true);
    expect(renderHook(() => usePermission("crm.leads.read")).result.current).toBe(true);
  });
});

describe("navigation hiding of disabled modules", () => {
  afterEach(() => {
    act(() => useAuthStore.setState({ me: null }));
  });

  const keys = (items: { key: string }[]) => items.map((i) => i.key);

  it("hides the items of modules the plan lacks (menu and settings) using /me.subscription", () => {
    act(() =>
      useAuthStore.setState({
        me: platformMe(
          [
            "crm.leads.read",
            "crm.campaigns.read",
            "crm.products.read",
            "crm.quotes.read",
            "crm.orders.read",
            "crm.cases.read",
            "crm.approvals.decide",
            "org.workflows.manage",
            "org.settings.manage",
          ],
          {
            subscription: subscription({
              modules: { workflows: false, commerce: false, service: false, marketing: false },
            }),
          }
        ),
      })
    );
    const { result: modules } = renderHook(() => useVisibleItems(NAV_ITEMS, { pendingApprovals: true }));
    const { result: settings } = renderHook(() => useVisibleItems(SETTINGS_ITEMS));
    expect(keys(modules.current)).toEqual(["home", "leads"]);
    // Approvals stay hidden even with a pending approval; SLA (service) and workflows are gone, plan usage stays.
    expect(keys(settings.current)).toEqual(["organization", "plan"]);
  });

  it("shows everything when the subscription is absent (older server)", () => {
    act(() =>
      useAuthStore.setState({
        me: platformMe(["crm.campaigns.read", "crm.quotes.read", "crm.cases.read", "org.settings.manage"]),
      })
    );
    const { result: modules } = renderHook(() => useVisibleItems(NAV_ITEMS));
    const { result: settings } = renderHook(() => useVisibleItems(SETTINGS_ITEMS));
    expect(keys(modules.current)).toEqual(expect.arrayContaining(["campaigns", "quotes", "cases"]));
    expect(keys(settings.current)).toEqual(expect.arrayContaining(["sla", "plan"]));
  });
});
