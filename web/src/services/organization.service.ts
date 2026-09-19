/**
 * Organization (tenant) settings, members and audit log - `/api/organization/*`.
 */
import { apiClient } from "@/lib/api-client";
import type { AuditEntry, Locale, Member, MemberStatus, Organization, PagedResult } from "@/types";

const ORG_BASE = "/organization";

export const organizationKeys = {
  all: ["organization"] as const,
  detail: ["organization", "detail"] as const,
  members: ["organization", "members"] as const,
  audit: (page: number, pageSize: number) => ["organization", "audit", page, pageSize] as const,
};

export interface UpdateOrganizationRequest {
  name: string;
  defaultLocale: Locale;
  timeZone: string;
}

export async function getOrganization(): Promise<Organization> {
  const { data } = await apiClient.get<Organization>(ORG_BASE);
  return data;
}

export async function updateOrganization(request: UpdateOrganizationRequest): Promise<void> {
  await apiClient.put(ORG_BASE, request);
}

export interface CreateMemberRequest {
  email: string;
  displayName: string;
  roleId: string;
}

/**
 * `POST /organization/members` (201). `temporaryPassword` exists only for a newly created account
 * and is returned exactly once; `status: "pending"` means an existing account was invited.
 */
export interface CreateMemberResponse {
  email: string;
  roleId: string;
  roleName: string;
  status: MemberStatus;
  userId?: string;
  temporaryPassword?: string;
}

export interface UpdateMemberRequest {
  roleId?: string;
  isActive?: boolean;
}

export async function listMembers(): Promise<Member[]> {
  const { data } = await apiClient.get<Member[]>(`${ORG_BASE}/members`);
  return data;
}

export async function createMember(request: CreateMemberRequest): Promise<CreateMemberResponse> {
  const { data } = await apiClient.post<CreateMemberResponse>(`${ORG_BASE}/members`, request);
  return data;
}

export async function updateMember(userId: string, request: UpdateMemberRequest): Promise<void> {
  await apiClient.patch(`${ORG_BASE}/members/${encodeURIComponent(userId)}`, request);
}

export async function listAuditEntries(
  page: number,
  pageSize: number
): Promise<PagedResult<AuditEntry>> {
  const { data } = await apiClient.get<PagedResult<AuditEntry>>(`${ORG_BASE}/audit`, {
    params: { page, pageSize },
  });
  return data;
}
