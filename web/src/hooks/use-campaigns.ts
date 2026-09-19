import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  addCampaignMembers,
  campaignKeys,
  createCampaign,
  deleteCampaign,
  getCampaign,
  getCampaignMetrics,
  getMarketingSummary,
  listCampaignMembers,
  listCampaigns,
  listCampaignsByMember,
  marketingReportKeys,
  removeCampaignMembers,
  setCampaignStatus,
  setMembersStatus,
  updateCampaign,
  type CampaignListQuery,
  type MarketingRangeQuery,
  type MemberListQuery,
} from "@/services/campaigns.service";
import type {
  CampaignInput,
  CampaignStatus,
  MemberManualStatus,
  MemberType,
} from "@/types";

const REPORT_STALE_MS = 30_000;

export function useCampaigns(query: CampaignListQuery, enabled = true) {
  return useQuery({
    queryKey: campaignKeys.list(query),
    queryFn: () => listCampaigns(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useCampaign(id: string | undefined) {
  return useQuery({
    queryKey: campaignKeys.detail(id ?? ""),
    queryFn: () => getCampaign(id as string),
    enabled: !!id,
  });
}

export function useCampaignMetrics(id: string | undefined) {
  return useQuery({
    queryKey: campaignKeys.metrics(id ?? ""),
    queryFn: () => getCampaignMetrics(id as string),
    enabled: !!id,
  });
}

export function useCampaignMembers(id: string | undefined, query: MemberListQuery) {
  return useQuery({
    queryKey: campaignKeys.members(id ?? "", query),
    queryFn: () => listCampaignMembers(id as string, query),
    placeholderData: keepPreviousData,
    enabled: !!id,
  });
}

export function useCampaignsByMember(memberType: MemberType, memberId: string) {
  return useQuery({
    queryKey: campaignKeys.byMember(memberType, memberId),
    queryFn: () => listCampaignsByMember(memberType, memberId),
    enabled: !!memberId,
  });
}

export function useMarketingSummary(query: MarketingRangeQuery, enabled = true) {
  return useQuery({
    queryKey: marketingReportKeys.summary(query),
    queryFn: () => getMarketingSummary(query),
    placeholderData: keepPreviousData,
    staleTime: REPORT_STALE_MS,
    enabled,
  });
}

/** Everything a campaign or membership change can affect: lists, details, members, metrics, audit, reports. */
function useInvalidateCampaigns() {
  const queryClient = useQueryClient();
  return async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: campaignKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      queryClient.invalidateQueries({ queryKey: ["reports", "marketing"] }),
    ]);
  };
}

export function useSaveCampaign() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: async ({ id, ...input }: CampaignInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateCampaign(id, input);
        return id;
      }
      return (await createCampaign(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteCampaign() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: (id: string) => deleteCampaign(id),
    onSuccess: invalidate,
  });
}

export function useSetCampaignStatus() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: ({ id, status }: { id: string; status: CampaignStatus }) =>
      setCampaignStatus(id, status),
    onSuccess: invalidate,
  });
}

export function useAddCampaignMembers() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: ({
      campaignId,
      memberType,
      memberIds,
    }: {
      campaignId: string;
      memberType: MemberType;
      memberIds: string[];
    }) => addCampaignMembers(campaignId, memberType, memberIds),
    onSuccess: invalidate,
  });
}

export function useSetMembersStatus() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: ({
      campaignId,
      memberIds,
      status,
    }: {
      campaignId: string;
      memberIds: string[];
      status: MemberManualStatus;
    }) => setMembersStatus(campaignId, memberIds, status),
    onSuccess: invalidate,
  });
}

export function useRemoveCampaignMembers() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: ({ campaignId, memberIds }: { campaignId: string; memberIds: string[] }) =>
      removeCampaignMembers(campaignId, memberIds),
    onSuccess: invalidate,
  });
}
