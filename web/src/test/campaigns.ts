/** Fixtures of the marketing tests (contract: docs/plan/m6c-pazarlama.md). */
import type {
  Campaign,
  CampaignMember,
  CampaignMetrics,
  MarketingSummary,
  RecordCampaignMembership,
} from "@/types";

export function campaign(id: string, overrides: Partial<Campaign> = {}): Campaign {
  return {
    id,
    name: `Kampanya ${id}`,
    type: "email",
    status: "planned",
    currency: "TRY",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    memberCount: 0,
    createdAt: "2026-05-01T10:00:00Z",
    ...overrides,
  };
}

export function member(id: string, overrides: Partial<CampaignMember> = {}): CampaignMember {
  return {
    id,
    memberType: "lead",
    memberId: `rec-${id}`,
    memberName: `Üye ${id}`,
    memberMissing: false,
    status: "added",
    addedAt: "2026-05-02T10:00:00Z",
    statusChangedAt: "2026-05-02T10:00:00Z",
    addedByName: "Ada Lovelace",
    ...overrides,
  };
}

export function metrics(campaignId: string, overrides: Partial<CampaignMetrics> = {}): CampaignMetrics {
  return {
    campaignId,
    memberCount: 40,
    leadCount: 30,
    contactCount: 10,
    statusCounts: { added: 10, sent: 12, responded: 8, converted: 6, unsubscribed: 4 },
    contactedCount: 30,
    responseCount: 14,
    responseRate: 46.67,
    convertedCount: 6,
    conversionRate: 20,
    currency: "TRY",
    costPerLead: 400,
    ...overrides,
  };
}

export function membership(
  campaignId: string,
  overrides: Partial<RecordCampaignMembership> = {}
): RecordCampaignMembership {
  return {
    campaignId,
    campaignName: `Kampanya ${campaignId}`,
    campaignType: "email",
    campaignStatus: "active",
    membershipId: `m-${campaignId}`,
    memberStatus: "sent",
    addedAt: "2026-09-19T10:00:00Z",
    ...overrides,
  };
}

export const MARKETING_SUMMARY: MarketingSummary = {
  from: "2025-10-01",
  to: "2026-09-19",
  campaignCount: 12,
  byStatus: [
    { status: "planned", count: 2 },
    { status: "active", count: 5 },
    { status: "completed", count: 4 },
    { status: "cancelled", count: 1 },
  ],
  byType: [
    { type: "email", count: 4, budget: 80000, actualCost: 61000, memberCount: 900, convertedCount: 35 },
    { type: "event", count: 0, budget: 0, actualCost: 0, memberCount: 0, convertedCount: 0 },
    { type: "webinar", count: 0, budget: 0, actualCost: 0, memberCount: 0, convertedCount: 0 },
    { type: "advertising", count: 8, budget: 170000, actualCost: 114000, memberCount: 1500, convertedCount: 61 },
    { type: "other", count: 0, budget: 0, actualCost: 0, memberCount: 0, convertedCount: 0 },
  ],
  totals: {
    budget: 250000,
    expectedRevenue: 900000,
    actualCost: 175000,
    memberCount: 2400,
    leadCount: 1800,
    contactedCount: 1500,
    responseCount: 420,
    responseRate: 28,
    convertedCount: 96,
    conversionRate: 5.33,
    costPerLead: 97.22,
  },
  topCampaigns: [
    {
      id: "c1",
      name: "Sonbahar E-posta",
      type: "email",
      status: "active",
      budget: 50000,
      actualCost: 12000,
      memberCount: 300,
      responseRate: 41.5,
      convertedCount: 22,
    },
  ],
};
