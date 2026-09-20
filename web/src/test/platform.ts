/** Fixtures for the Milestone 7 tests (platform console, subscription, onboarding). */
import { act } from "@testing-library/react";
import { useAuthStore } from "@/store/auth.store";
import { meWith } from "@/test/crm";
import type {
  Me,
  MeSubscription,
  PlatformOrganization,
  PlatformOrganizationDetail,
  PlatformPlan,
  SubscriptionInfo,
} from "@/types";

export const ALL_MODULES_ON = { workflows: true, commerce: true, service: true, marketing: true };

export function subscription(overrides: Partial<MeSubscription> = {}): MeSubscription {
  return {
    planCode: "business",
    planName: "Business",
    status: "active",
    accessLevel: "full",
    modules: { ...ALL_MODULES_ON },
    ...overrides,
  };
}

/** `meWith` plus platform flag and subscription. */
export function platformMe(
  permissions: string[],
  options: { isPlatformAdmin?: boolean; subscription?: MeSubscription } = {}
): Me {
  const me = meWith(permissions);
  return {
    ...me,
    user: { ...me.user, isPlatformAdmin: options.isPlatformAdmin ?? false },
    ...(options.subscription ? { subscription: options.subscription } : {}),
  };
}

export function setMe(me: Me): void {
  act(() => useAuthStore.setState({ me }));
}

export function orgRow(id: string, overrides: Partial<PlatformOrganization> = {}): PlatformOrganization {
  return {
    tenantId: id,
    name: `Org ${id}`,
    slug: `org-${id}`,
    planCode: "starter",
    planName: "Starter",
    source: "signup",
    status: "trial",
    accessLevel: "full",
    trialEndsOn: "2026-10-04",
    isSystem: false,
    createdAt: "2026-09-01T10:00:00Z",
    usage: { day: "2026-09-20", usersActive: 3, usersPending: 1, records: { sales: 340, activities: 120 } },
    ...overrides,
  };
}

export function orgDetail(
  id: string,
  overrides: Partial<PlatformOrganizationDetail> = {}
): PlatformOrganizationDetail {
  return {
    ...orgRow(id),
    limits: {
      maxUsers: 5,
      maxRecords: { sales: 5000, activities: 10000 },
      modules: { workflows: false, commerce: false, service: false, marketing: false },
    },
    ...overrides,
  };
}

export const PLANS: PlatformPlan[] = [
  {
    code: "internal",
    name: "Ic kullanim",
    isActive: true,
    sortOrder: 0,
    limits: { maxRecords: {} },
    modules: { ...ALL_MODULES_ON },
    assignedCount: 2,
  },
  {
    code: "starter",
    name: "Starter",
    isActive: true,
    sortOrder: 10,
    trialDays: 14,
    limits: { maxUsers: 5, maxRecords: { sales: 5000, activities: 10000 } },
    modules: { workflows: false, commerce: false, service: false, marketing: false },
    assignedCount: 7,
  },
  {
    code: "business",
    name: "Business",
    isActive: true,
    sortOrder: 20,
    trialDays: 14,
    limits: { maxUsers: 25, maxRecords: { sales: 100000 } },
    modules: { workflows: true, commerce: true, service: true, marketing: false },
    assignedCount: 3,
  },
  {
    code: "legacy",
    name: "Legacy",
    isActive: false,
    sortOrder: 90,
    limits: { maxRecords: {} },
    modules: { ...ALL_MODULES_ON },
    assignedCount: 0,
  },
];

export function subscriptionInfo(overrides: Partial<SubscriptionInfo> = {}): SubscriptionInfo {
  return {
    planCode: "starter",
    planName: "Starter",
    status: "trial",
    accessLevel: "full",
    trialEndsOn: "2026-10-04",
    trialDaysLeft: 14,
    modules: { workflows: false, commerce: false, service: false, marketing: false },
    limits: { maxUsers: 5, maxRecords: { sales: 5000, activities: 10000 } },
    usage: {
      asOf: "2026-09-20T09:00:00Z",
      users: 3,
      pendingUsers: 1,
      records: { sales: 340, activities: 120 },
    },
    overLimit: [],
    ...overrides,
  };
}
