import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createRole,
  deleteRole,
  listPermissions,
  listRoles,
  roleKeys,
  updateRole,
  type RoleRequest,
} from "@/services/roles.service";
import { useAuthStore } from "@/store/auth.store";

export function useRoles(enabled = true) {
  return useQuery({ queryKey: roleKeys.all, queryFn: listRoles, enabled });
}

export function usePermissionCatalog() {
  // The permission catalog is static per deployment.
  return useQuery({
    queryKey: roleKeys.permissions,
    queryFn: listPermissions,
    staleTime: Infinity,
  });
}

/** Create or update a role; editing the caller's own role changes their permissions, so /me is re-read. */
export function useSaveRole() {
  const queryClient = useQueryClient();
  const refreshMe = useAuthStore((state) => state.refreshMe);
  return useMutation({
    mutationFn: async ({ id, ...request }: RoleRequest & { id?: string }): Promise<void> => {
      if (id) await updateRole(id, request);
      else await createRole(request);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: roleKeys.all });
      await refreshMe();
    },
  });
}

export function useDeleteRole() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteRole(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: roleKeys.all }),
  });
}
