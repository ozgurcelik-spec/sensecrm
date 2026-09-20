import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createVendor,
  deleteVendor,
  getVendor,
  listVendors,
  updateVendor,
  vendorKeys,
  type VendorListQuery,
} from "@/services/vendors.service";
import type { VendorInput } from "@/types";

export function useVendors(query: VendorListQuery, enabled = true) {
  return useQuery({
    queryKey: vendorKeys.list(query),
    queryFn: () => listVendors(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useVendor(id: string | undefined) {
  return useQuery({
    queryKey: vendorKeys.detail(id ?? ""),
    queryFn: () => getVendor(id as string),
    enabled: !!id,
  });
}

function useInvalidateVendors() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: vendorKeys.all }),
      // A vendor delete clears the primary vendor of its products.
      queryClient.invalidateQueries({ queryKey: ["products"] }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
    ]);
}

export function useSaveVendor() {
  const invalidate = useInvalidateVendors();
  return useMutation({
    mutationFn: async ({ id, ...input }: VendorInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateVendor(id, input);
        return id;
      }
      return (await createVendor(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteVendor() {
  const invalidate = useInvalidateVendors();
  return useMutation({ mutationFn: (id: string) => deleteVendor(id), onSuccess: invalidate });
}
