/** Marketing: campaigns, campaign members and the marketing report - `/campaigns`, `/reports/marketing`. */
import { apiClient } from "@/lib/api-client";
import type {
  AddMembersResult,
  Campaign,
  CampaignInput,
  CampaignMember,
  CampaignMetrics,
  CampaignStatus,
  ListResult,
  MarketingSummary,
  MemberManualStatus,
  MemberType,
  RecordCampaignMembership,
  RemoveMembersResult,
  SetMembersStatusResult,
} from "@/types";
import { cleanParams, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface CampaignListQuery extends ListQuery {
  /** Comma separated CampaignType values (comes straight from the URL). */
  type?: string;
  /** Comma separated CampaignStatus values. */
  status?: string;
  ownerUserId?: string;
  /** `YYYY-MM-DD`, inclusive, on `startDate`. */
  startFrom?: string;
  startTo?: string;
}

export interface MemberListQuery extends ListQuery {
  memberType?: string;
  /** Comma separated CampaignMemberStatus values. */
  status?: string;
}

export interface MarketingRangeQuery {
  from?: string;
  to?: string;
}

export const campaignKeys = {
  all: ["campaigns"] as const,
  list: (query: CampaignListQuery) => ["campaigns", "list", query] as const,
  detail: (id: string) => ["campaigns", "detail", id] as const,
  metrics: (id: string) => ["campaigns", "metrics", id] as const,
  members: (id: string, query: MemberListQuery) => ["campaigns", "members", id, query] as const,
  byMember: (memberType: MemberType, memberId: string) =>
    ["campaigns", "by-member", memberType, memberId] as const,
};

/** Under the shared `reports` prefix so `reportKeys.all` invalidation covers it. */
export const marketingReportKeys = {
  summary: (query: MarketingRangeQuery) => ["reports", "marketing", query] as const,
};

export const listCampaigns = (query: CampaignListQuery): Promise<ListResult<Campaign>> =>
  getList<Campaign>("/campaigns", query);

export const getCampaign = (id: string): Promise<Campaign> =>
  getOne<Campaign>(`/campaigns/${seg(id)}`);

export async function createCampaign(input: CampaignInput): Promise<Campaign> {
  const { data } = await apiClient.post<Campaign>("/campaigns", input);
  return data;
}

/** Full replacement; `status` is not part of an update. */
export async function updateCampaign(id: string, input: CampaignInput): Promise<void> {
  await apiClient.put(`/campaigns/${seg(id)}`, input);
}

export async function deleteCampaign(id: string): Promise<void> {
  await apiClient.delete(`/campaigns/${seg(id)}`);
}

export async function setCampaignStatus(id: string, status: CampaignStatus): Promise<void> {
  await apiClient.post(`/campaigns/${seg(id)}/status`, { status });
}

export const getCampaignMetrics = (id: string): Promise<CampaignMetrics> =>
  getOne<CampaignMetrics>(`/campaigns/${seg(id)}/metrics`);

export const listCampaignMembers = (
  id: string,
  query: MemberListQuery
): Promise<ListResult<CampaignMember>> =>
  getList<CampaignMember>(`/campaigns/${seg(id)}/members`, query);

export async function addCampaignMembers(
  id: string,
  memberType: MemberType,
  memberIds: string[]
): Promise<AddMembersResult> {
  const { data } = await apiClient.post<AddMembersResult>(`/campaigns/${seg(id)}/members`, {
    memberType,
    memberIds,
  });
  return data;
}

/** `memberIds` are membership row ids. */
export async function setMembersStatus(
  id: string,
  memberIds: string[],
  status: MemberManualStatus
): Promise<SetMembersStatusResult> {
  const { data } = await apiClient.post<SetMembersStatusResult>(
    `/campaigns/${seg(id)}/members/status`,
    { memberIds, status }
  );
  return data;
}

/** `memberIds` are membership row ids. */
export async function removeCampaignMembers(
  id: string,
  memberIds: string[]
): Promise<RemoveMembersResult> {
  const { data } = await apiClient.post<RemoveMembersResult>(
    `/campaigns/${seg(id)}/members/remove`,
    { memberIds }
  );
  return data;
}

export async function listCampaignsByMember(
  memberType: MemberType,
  memberId: string
): Promise<RecordCampaignMembership[]> {
  const { data } = await apiClient.get<RecordCampaignMembership[]>("/campaigns/by-member", {
    params: { memberType, memberId },
  });
  return data;
}

export async function getMarketingSummary(query: MarketingRangeQuery): Promise<MarketingSummary> {
  const { data } = await apiClient.get<MarketingSummary>("/reports/marketing/summary", {
    params: cleanParams({ ...query }),
  });
  return data;
}
