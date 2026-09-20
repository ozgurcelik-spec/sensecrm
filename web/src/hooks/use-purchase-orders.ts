import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createPurchaseOrder,
  deletePurchaseOrder,
  getPurchaseOrder,
  listPurchaseOrders,
  purchaseOrderKeys,
  runPurchaseOrderAction,
  updatePurchaseOrder,
  type PurchaseOrderAction,
  type PurchaseOrderListQuery,
} from "@/services/purchase-orders.service";
import type { PurchaseOrderInput } from "@/types";

export function usePurchaseOrders(query: PurchaseOrderListQuery, enabled = true) {
  return useQuery({
    queryKey: purchaseOrderKeys.list(query),
    queryFn: () => listPurchaseOrders(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function usePurchaseOrder(id: string | undefined) {
  return useQuery({
    queryKey: purchaseOrderKeys.detail(id ?? ""),
    queryFn: () => getPurchaseOrder(id as string),
    enabled: !!id,
  });
}

function useInvalidatePurchaseOrders() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: purchaseOrderKeys.all }),
      // The vendor's purchase order count.
      queryClient.invalidateQueries({ queryKey: ["vendors"] }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      queryClient.invalidateQueries({ queryKey: ["reports"] }),
    ]);
}

export function useSavePurchaseOrder() {
  const invalidate = useInvalidatePurchaseOrders();
  return useMutation({
    mutationFn: async ({ id, ...input }: PurchaseOrderInput & { id?: string }): Promise<string> => {
      if (id) {
        await updatePurchaseOrder(id, input);
        return id;
      }
      return (await createPurchaseOrder(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeletePurchaseOrder() {
  const invalidate = useInvalidatePurchaseOrders();
  return useMutation({ mutationFn: (id: string) => deletePurchaseOrder(id), onSuccess: invalidate });
}

interface PurchaseOrderActionVariables {
  id: string;
  action: PurchaseOrderAction;
  reason?: string;
}

/** confirm / receive / cancel. Refetches on failure as well (a 409 means the order changed). */
export function usePurchaseOrderAction() {
  const invalidate = useInvalidatePurchaseOrders();
  return useMutation({
    mutationFn: ({ id, action, reason }: PurchaseOrderActionVariables) =>
      runPurchaseOrderAction(id, action, action === "cancel" ? { reason } : undefined),
    onSettled: () => invalidate(),
  });
}
