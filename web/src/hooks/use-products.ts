import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createProduct,
  deleteProduct,
  listProducts,
  productKeys,
  updateProduct,
  type ProductListQuery,
} from "@/services/products.service";
import type { ProductInput } from "@/types";

export function useProducts(query: ProductListQuery, enabled = true) {
  return useQuery({
    queryKey: productKeys.list(query),
    queryFn: () => listProducts(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useSaveProduct() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...input }: ProductInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateProduct(id, input);
        return id;
      }
      return (await createProduct(input)).id;
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: productKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

export function useDeleteProduct() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteProduct(id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: productKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

/** Active / inactive switch of a list row: a full PUT with the row's own values and the new flag. */
export function useSetProductActive() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, ...input }: ProductInput & { id: string }) => updateProduct(id, input),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: productKeys.all });
      void queryClient.invalidateQueries({ queryKey: ["audit"] });
    },
  });
}
