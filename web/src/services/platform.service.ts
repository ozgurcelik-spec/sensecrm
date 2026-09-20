/** Platform console - `/platform/**` (platform admins only). */
import { apiClient } from "@/lib/api-client";
import type {
  CreatedPlatformOrganization,
  CreatePlatformOrganizationInput,
  ListResult,
  PlatformAuditEntry,
  PlatformDeletionInput,
  PlatformDeletionResult,
  PlatformOrganization,
  PlatformOrganizationDetail,
  PlatformPlan,
  PlatformSubscriptionInput,
  PlatformSubscriptionResult,
  PlatformSuspendInput,
  PlatformUsageDay,
} from "@/types";
import { cleanParams, getArray, getList, getOne, seg, type ListQuery } from "./crm-http";

export interface PlatformOrganizationQuery extends ListQuery {
  /** Effective status (`trial|active|trial_expired|suspended|pending_deletion|deleted`). */
  status?: string;
  planCode?: string;
  source?: string;
}

export interface PlatformAuditQuery extends ListQuery {
  tenantId?: string;
  action?: string;
  actorUserId?: string;
  /** `YYYY-MM-DD` (UTC day), inclusive. */
  from?: string;
  to?: string;
}

export interface UsageRangeQuery {
  from?: string;
  to?: string;
}

export const platformKeys = {
  all: ["platform"] as const,
  organizations: ["platform", "organizations"] as const,
  list: (query: PlatformOrganizationQuery) => ["platform", "organizations", "list", query] as const,
  detail: (id: string) => ["platform", "organizations", "detail", id] as const,
  usage: (id: string, query: UsageRangeQuery) => ["platform", "usage", id, query] as const,
  plans: ["platform", "plans"] as const,
  audit: (query: PlatformAuditQuery) => ["platform", "audit", query] as const,
};

const BASE = "/platform/organizations";

export const listPlatformOrganizations = (
  query: PlatformOrganizationQuery
): Promise<ListResult<PlatformOrganization>> => getList<PlatformOrganization>(BASE, query);

export const getPlatformOrganization = (id: string): Promise<PlatformOrganizationDetail> =>
  getOne<PlatformOrganizationDetail>(`${BASE}/${seg(id)}`);

/** Existing M5 endpoint (Identity), extended with `planCode` / `trialEndsOn`. The response may carry a one-time password. */
export async function createPlatformOrganization(
  input: CreatePlatformOrganizationInput
): Promise<CreatedPlatformOrganization> {
  const { data } = await apiClient.post<CreatedPlatformOrganization>(BASE, input);
  return data;
}

/** Full, permanent replacement of plan / trial / overrides. */
export async function updatePlatformSubscription(
  id: string,
  input: PlatformSubscriptionInput
): Promise<PlatformSubscriptionResult> {
  const { data } = await apiClient.put<PlatformSubscriptionResult>(
    `${BASE}/${seg(id)}/subscription`,
    input
  );
  return { overLimit: data?.overLimit ?? [] };
}

export async function suspendPlatformOrganization(
  id: string,
  input: PlatformSuspendInput
): Promise<void> {
  await apiClient.post(`${BASE}/${seg(id)}/suspend`, input);
}

export async function reactivatePlatformOrganization(id: string): Promise<void> {
  await apiClient.post(`${BASE}/${seg(id)}/reactivate`);
}

export async function requestPlatformDeletion(
  id: string,
  input: PlatformDeletionInput
): Promise<PlatformDeletionResult> {
  const { data } = await apiClient.post<PlatformDeletionResult>(
    `${BASE}/${seg(id)}/deletion-request`,
    input
  );
  return data;
}

export async function cancelPlatformDeletion(id: string): Promise<void> {
  await apiClient.post(`${BASE}/${seg(id)}/deletion-request/cancel`);
}

export async function getPlatformUsage(
  id: string,
  query: UsageRangeQuery
): Promise<PlatformUsageDay[]> {
  const { data } = await apiClient.get<{ items: PlatformUsageDay[] }>(
    `${BASE}/${seg(id)}/usage`,
    { params: cleanParams({ ...query }) }
  );
  return data.items;
}

/** Live count of one tenant; writes today's snapshot. */
export async function refreshPlatformUsage(id: string): Promise<PlatformUsageDay[]> {
  const { data } = await apiClient.post<{ items: PlatformUsageDay[] }>(
    `${BASE}/${seg(id)}/usage/refresh`
  );
  return data.items;
}

export const listPlatformPlans = (): Promise<PlatformPlan[]> =>
  getArray<PlatformPlan>("/platform/plans");

export const listPlatformAudit = (
  query: PlatformAuditQuery
): Promise<ListResult<PlatformAuditEntry>> => getList<PlatformAuditEntry>("/platform/audit", query);

/** `GET /platform/usage/export` (CSV, UTF-8 BOM). Without a range the server exports the previous calendar month. */
export async function exportPlatformUsage(range: UsageRangeQuery): Promise<Blob> {
  const { data } = await apiClient.get<Blob>("/platform/usage/export", {
    params: cleanParams({ ...range }),
    responseType: "blob",
  });
  return data;
}
