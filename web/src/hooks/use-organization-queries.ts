import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createMember,
  getOrganization,
  listAuditEntries,
  listMembers,
  organizationKeys,
  updateMember,
  updateOrganization,
  type CreateMemberRequest,
  type UpdateMemberRequest,
  type UpdateOrganizationRequest,
} from "@/services/organization.service";
import { roleKeys } from "@/services/roles.service";
import { useAuthStore } from "@/store/auth.store";

export function useOrganization() {
  return useQuery({ queryKey: organizationKeys.detail, queryFn: getOrganization });
}

export function useUpdateOrganization() {
  const queryClient = useQueryClient();
  const refreshMe = useAuthStore((state) => state.refreshMe);
  return useMutation({
    mutationFn: (request: UpdateOrganizationRequest) => updateOrganization(request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: organizationKeys.detail });
      // The organization name/locale also appear in /me (header, org switcher).
      await refreshMe();
    },
  });
}

export function useMembers(enabled = true) {
  return useQuery({ queryKey: organizationKeys.members, queryFn: listMembers, enabled });
}

export function useCreateMember() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateMemberRequest) => createMember(request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: organizationKeys.members });
      await queryClient.invalidateQueries({ queryKey: roleKeys.all });
    },
  });
}

export function useUpdateMember() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, ...request }: UpdateMemberRequest & { userId: string }) =>
      updateMember(userId, request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: organizationKeys.members });
      await queryClient.invalidateQueries({ queryKey: roleKeys.all });
    },
  });
}

export function useAuditEntries(page: number, pageSize: number) {
  return useQuery({
    queryKey: organizationKeys.audit(page, pageSize),
    queryFn: () => listAuditEntries(page, pageSize),
    placeholderData: keepPreviousData,
  });
}
