/**
 * Milestone 6C API types (marketing: campaigns and campaign members) - see
 * docs/plan/m6c-pazarlama.md. JSON is camelCase, enums are camelCase strings, null fields are
 * absent from responses and "date only" fields are `YYYY-MM-DD`.
 */

export const CAMPAIGN_TYPES = ["email", "event", "webinar", "advertising", "other"] as const;
export type CampaignType = (typeof CAMPAIGN_TYPES)[number];

export const CAMPAIGN_STATUSES = ["planned", "active", "completed", "cancelled"] as const;
export type CampaignStatus = (typeof CAMPAIGN_STATUSES)[number];

/** Statuses a campaign can be created with. */
export const CAMPAIGN_CREATE_STATUSES = [
  "planned",
  "active",
] as const satisfies readonly CampaignStatus[];

/** Binding transition table: the valid targets of each status (the same status is a no-op). */
export const CAMPAIGN_TRANSITIONS: Record<CampaignStatus, readonly CampaignStatus[]> = {
  planned: ["active", "cancelled"],
  active: ["completed", "cancelled"],
  completed: ["active"],
  cancelled: ["planned"],
};

/** A closed campaign accepts no new members (`campaign.closed`). */
export function isCampaignClosed(status: CampaignStatus): boolean {
  return status === "completed" || status === "cancelled";
}

export const MEMBER_TYPES = ["lead", "contact"] as const;
export type MemberType = (typeof MEMBER_TYPES)[number];

export const MEMBER_STATUSES = ["added", "sent", "responded", "converted", "unsubscribed"] as const;
export type CampaignMemberStatus = (typeof MEMBER_STATUSES)[number];

/** Statuses that can be set by hand: `converted` only comes from a lead conversion. */
export const MEMBER_MANUAL_STATUSES = [
  "added",
  "sent",
  "responded",
  "unsubscribed",
] as const satisfies readonly CampaignMemberStatus[];
export type MemberManualStatus = (typeof MEMBER_MANUAL_STATUSES)[number];

export interface Campaign {
  id: string;
  name: string;
  type: CampaignType;
  status: CampaignStatus;
  /** `YYYY-MM-DD`. */
  startDate?: string;
  endDate?: string;
  currency: string;
  budget?: number;
  expectedRevenue?: number;
  actualCost?: number;
  description?: string;
  ownerUserId: string;
  ownerName?: string;
  memberCount: number;
  createdAt: string;
  updatedAt?: string;
}

/** Body of `POST /campaigns` and `PUT /campaigns/{id}` (`status` is sent on create only). */
export interface CampaignInput {
  name: string;
  type: CampaignType;
  status?: CampaignStatus;
  startDate?: string;
  endDate?: string;
  currency?: string;
  budget?: number;
  expectedRevenue?: number;
  actualCost?: number;
  description?: string;
  ownerUserId?: string;
}

export interface CampaignMember {
  /** Id of the membership row (not of the lead / contact). */
  id: string;
  memberType: MemberType;
  memberId: string;
  /** Absent when the record was deleted (`memberMissing`). */
  memberName?: string;
  memberMissing: boolean;
  status: CampaignMemberStatus;
  addedAt: string;
  statusChangedAt: string;
  addedByUserId?: string;
  addedByName?: string;
}

export type MemberSkipReason = "not_found" | "lead_converted";

export interface AddMembersResult {
  addedCount: number;
  alreadyMemberCount: number;
  skipped: { memberId: string; reason: MemberSkipReason }[];
}

export interface SetMembersStatusResult {
  updatedCount: number;
  skippedCount: number;
}

export interface RemoveMembersResult {
  removedCount: number;
}

export interface CampaignMetrics {
  campaignId: string;
  memberCount: number;
  leadCount: number;
  contactCount: number;
  statusCounts: Record<CampaignMemberStatus, number>;
  contactedCount: number;
  responseCount: number;
  /** 0-100. */
  responseRate: number;
  convertedCount: number;
  /** 0-100. */
  conversionRate: number;
  currency: string;
  /** Absent without an actual cost or without leads. */
  costPerLead?: number;
}

/** A campaign membership of one lead / contact (`GET /campaigns/by-member`). */
export interface RecordCampaignMembership {
  campaignId: string;
  campaignName: string;
  campaignType: CampaignType;
  campaignStatus: CampaignStatus;
  membershipId: string;
  memberStatus: CampaignMemberStatus;
  addedAt: string;
}

export interface MarketingTypeRow {
  type: CampaignType;
  count: number;
  budget: number;
  actualCost: number;
  memberCount: number;
  convertedCount: number;
}

export interface MarketingTopCampaign {
  id: string;
  name: string;
  type: CampaignType;
  status: CampaignStatus;
  budget?: number;
  actualCost?: number;
  memberCount: number;
  responseRate: number;
  convertedCount: number;
}

/** `GET /reports/marketing/summary`. Amounts are summed regardless of currency. */
export interface MarketingSummary {
  from: string;
  to: string;
  campaignCount: number;
  byStatus: { status: CampaignStatus; count: number }[];
  byType: MarketingTypeRow[];
  totals: {
    budget: number;
    expectedRevenue: number;
    actualCost: number;
    memberCount: number;
    leadCount: number;
    contactedCount: number;
    responseCount: number;
    responseRate: number;
    convertedCount: number;
    conversionRate: number;
    costPerLead?: number;
  };
  topCampaigns: MarketingTopCampaign[];
}
