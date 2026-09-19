import { keepPreviousData, useQuery } from "@tanstack/react-query";
import {
  getActivitiesByUser,
  getLeadsBySource,
  getSalesByOwner,
  getSalesFunnel,
  getWonLost,
  reportKeys,
  type DateRangeQuery,
  type FunnelQuery,
  type WonLostQuery,
} from "@/services/reports.service";

const REPORT_STALE_MS = 30_000;

export function useSalesFunnel(query: FunnelQuery, enabled = true) {
  return useQuery({
    queryKey: reportKeys.funnel(query),
    queryFn: () => getSalesFunnel(query),
    placeholderData: keepPreviousData,
    staleTime: REPORT_STALE_MS,
    enabled,
  });
}

export function useWonLost(query: WonLostQuery, enabled = true) {
  return useQuery({
    queryKey: reportKeys.wonLost(query),
    queryFn: () => getWonLost(query),
    placeholderData: keepPreviousData,
    staleTime: REPORT_STALE_MS,
    enabled,
  });
}

export function useLeadsBySource(query: DateRangeQuery, enabled = true) {
  return useQuery({
    queryKey: reportKeys.leadSources(query),
    queryFn: () => getLeadsBySource(query),
    placeholderData: keepPreviousData,
    staleTime: REPORT_STALE_MS,
    enabled,
  });
}

export function useSalesByOwner(query: DateRangeQuery, enabled = true) {
  return useQuery({
    queryKey: reportKeys.byOwner(query),
    queryFn: () => getSalesByOwner(query),
    placeholderData: keepPreviousData,
    staleTime: REPORT_STALE_MS,
    enabled,
  });
}

export function useActivitiesByUser(query: DateRangeQuery, enabled = true) {
  return useQuery({
    queryKey: reportKeys.activitiesByUser(query),
    queryFn: () => getActivitiesByUser(query),
    placeholderData: keepPreviousData,
    staleTime: REPORT_STALE_MS,
    enabled,
  });
}
