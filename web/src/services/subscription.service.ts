/** Tenant side of the plan: `/subscription` and `/onboarding` (own organization only). */
import { apiClient } from "@/lib/api-client";
import type { OnboardingState, SubscriptionInfo } from "@/types";
import { getOne } from "./crm-http";

export const subscriptionKeys = {
  all: ["subscription"] as const,
  current: ["subscription", "current"] as const,
  onboarding: ["subscription", "onboarding"] as const,
};

export const getSubscription = (): Promise<SubscriptionInfo> =>
  getOne<SubscriptionInfo>("/subscription");

export const getOnboarding = (): Promise<OnboardingState> =>
  getOne<OnboardingState>("/onboarding");

export async function dismissOnboarding(): Promise<void> {
  await apiClient.post("/onboarding/dismiss");
}
