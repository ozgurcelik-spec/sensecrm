/** Sales and activity reports - `/reports/...` (`crm.reports.read`). */
import { apiClient } from "@/lib/api-client";
import type {
  ActivityUserRow,
  LeadSourceRow,
  OwnerReportRow,
  SalesFunnelReport,
  WonLostGroupBy,
  WonLostRow,
} from "@/types";
import { cleanParams } from "./crm-http";

/** Inclusive `YYYY-MM-DD` bounds; both omitted = the server default (last 12 months). */
export interface DateRangeQuery {
  from?: string;
  to?: string;
}

export interface WonLostQuery extends DateRangeQuery {
  groupBy: WonLostGroupBy;
}

export interface FunnelQuery {
  /** Omitted = the default pipeline. */
  pipelineId?: string;
}

export const reportKeys = {
  all: ["reports"] as const,
  funnel: (query: FunnelQuery) => ["reports", "funnel", query] as const,
  wonLost: (query: WonLostQuery) => ["reports", "won-lost", query] as const,
  leadSources: (query: DateRangeQuery) => ["reports", "lead-sources", query] as const,
  byOwner: (query: DateRangeQuery) => ["reports", "by-owner", query] as const,
  activitiesByUser: (query: DateRangeQuery) => ["reports", "activities-by-user", query] as const,
};

async function fetchReport<T>(
  path: string,
  params: Record<string, string | undefined>
): Promise<T> {
  const { data } = await apiClient.get<T>(path, { params: cleanParams(params) });
  return data;
}

export const getSalesFunnel = (query: FunnelQuery) =>
  fetchReport<SalesFunnelReport>("/reports/sales/funnel", { pipelineId: query.pipelineId });

export const getWonLost = (query: WonLostQuery) =>
  fetchReport<WonLostRow[]>("/reports/sales/won-lost", { ...query });

export const getLeadsBySource = (query: DateRangeQuery) =>
  fetchReport<LeadSourceRow[]>("/reports/sales/leads-by-source", { ...query });

export const getSalesByOwner = (query: DateRangeQuery) =>
  fetchReport<OwnerReportRow[]>("/reports/sales/by-owner", { ...query });

export const getActivitiesByUser = (query: DateRangeQuery) =>
  fetchReport<ActivityUserRow[]>("/reports/activities/by-user", { ...query });
