import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  cancelPlatformDeletion,
  createPlatformOrganization,
  exportPlatformUsage,
  getPlatformOrganization,
  getPlatformUsage,
  listPlatformAudit,
  listPlatformOrganizations,
  listPlatformPlans,
  platformKeys,
  reactivatePlatformOrganization,
  refreshPlatformUsage,
  requestPlatformDeletion,
  suspendPlatformOrganization,
  updatePlatformSubscription,
  type PlatformAuditQuery,
  type PlatformOrganizationQuery,
  type UsageRangeQuery,
} from "@/services/platform.service";
import type {
  CreatePlatformOrganizationInput,
  PlatformDeletionInput,
  PlatformSubscriptionInput,
  PlatformSuspendInput,
} from "@/types";

export function usePlatformOrganizations(query: PlatformOrganizationQuery, enabled = true) {
  return useQuery({
    queryKey: platformKeys.list(query),
    queryFn: () => listPlatformOrganizations(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function usePlatformOrganization(id: string | undefined) {
  return useQuery({
    queryKey: platformKeys.detail(id ?? ""),
    queryFn: () => getPlatformOrganization(id as string),
    enabled: !!id,
  });
}

export function usePlatformPlans() {
  return useQuery({
    queryKey: platformKeys.plans,
    queryFn: listPlatformPlans,
    staleTime: 5 * 60_000,
  });
}

export function usePlatformUsage(id: string | undefined, query: UsageRangeQuery) {
  return useQuery({
    queryKey: platformKeys.usage(id ?? "", query),
    queryFn: () => getPlatformUsage(id as string, query),
    enabled: !!id,
  });
}

export function usePlatformAudit(query: PlatformAuditQuery) {
  return useQuery({
    queryKey: platformKeys.audit(query),
    queryFn: () => listPlatformAudit(query),
    placeholderData: keepPreviousData,
  });
}

/** Lists, details, usage and the platform audit all change with any console command. */
function useInvalidatePlatform() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: platformKeys.all });
}

export function useCreatePlatformOrganization() {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: (input: CreatePlatformOrganizationInput) => createPlatformOrganization(input),
    onSuccess: invalidate,
  });
}

export function useUpdatePlatformSubscription(id: string) {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: (input: PlatformSubscriptionInput) => updatePlatformSubscription(id, input),
    onSuccess: invalidate,
  });
}

export function useSuspendPlatformOrganization(id: string) {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: (input: PlatformSuspendInput) => suspendPlatformOrganization(id, input),
    onSuccess: invalidate,
  });
}

export function useReactivatePlatformOrganization(id: string) {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: () => reactivatePlatformOrganization(id),
    onSuccess: invalidate,
  });
}

export function useRequestPlatformDeletion(id: string) {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: (input: PlatformDeletionInput) => requestPlatformDeletion(id, input),
    onSuccess: invalidate,
  });
}

export function useCancelPlatformDeletion(id: string) {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: () => cancelPlatformDeletion(id),
    onSuccess: invalidate,
  });
}

export function useRefreshPlatformUsage(id: string) {
  const invalidate = useInvalidatePlatform();
  return useMutation({
    mutationFn: () => refreshPlatformUsage(id),
    onSuccess: invalidate,
  });
}

export function useExportPlatformUsage() {
  return useMutation({ mutationFn: (range: UsageRangeQuery) => exportPlatformUsage(range) });
}
