/**
 * Roles & permissions - `/api/organization/roles`, `/api/permissions`.
 */
import { apiClient } from "@/lib/api-client";
import type { Permission, Role } from "@/types";

const ROLES_BASE = "/organization/roles";

export const roleKeys = {
  all: ["organization", "roles"] as const,
  permissions: ["permissions"] as const,
};

export interface RoleRequest {
  name: string;
  permissions: string[];
}

export async function listPermissions(): Promise<Permission[]> {
  const { data } = await apiClient.get<Permission[]>("/permissions");
  return data;
}

export async function listRoles(): Promise<Role[]> {
  const { data } = await apiClient.get<Role[]>(ROLES_BASE);
  return data;
}

export async function createRole(request: RoleRequest): Promise<Role> {
  const { data } = await apiClient.post<Role>(ROLES_BASE, request);
  return data;
}

export async function updateRole(id: string, request: RoleRequest): Promise<void> {
  await apiClient.put(`${ROLES_BASE}/${encodeURIComponent(id)}`, request);
}

export async function deleteRole(id: string): Promise<void> {
  await apiClient.delete(`${ROLES_BASE}/${encodeURIComponent(id)}`);
}
