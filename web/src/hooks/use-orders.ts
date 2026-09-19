import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createOrder,
  deleteOrder,
  getOrder,
  listOrders,
  orderKeys,
  runOrderAction,
  updateOrder,
  type OrderAction,
  type OrderListQuery,
} from "@/services/orders.service";
import type { OrderInput } from "@/types";

export function useOrders(query: OrderListQuery, enabled = true) {
  return useQuery({
    queryKey: orderKeys.list(query),
    queryFn: () => listOrders(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useOrder(id: string | undefined) {
  return useQuery({
    queryKey: orderKeys.detail(id ?? ""),
    queryFn: () => getOrder(id as string),
    enabled: !!id,
  });
}

/** Everything an order change can touch: order lists, the source quote's "converted" state, report, audit. */
function useInvalidateOrders() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: orderKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["quotes"] }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      queryClient.invalidateQueries({ queryKey: ["reports"] }),
    ]);
}

export function useSaveOrder() {
  const invalidate = useInvalidateOrders();
  return useMutation({
    mutationFn: async ({ id, ...input }: OrderInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateOrder(id, input);
        return id;
      }
      return (await createOrder(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteOrder() {
  const invalidate = useInvalidateOrders();
  return useMutation({ mutationFn: (id: string) => deleteOrder(id), onSuccess: invalidate });
}

interface OrderActionVariables {
  id: string;
  action: OrderAction;
  reason?: string;
}

export function useOrderAction() {
  const invalidate = useInvalidateOrders();
  return useMutation({
    mutationFn: ({ id, action, reason }: OrderActionVariables) =>
      runOrderAction(id, action, action === "cancel" ? { reason } : undefined),
    onSettled: () => invalidate(),
  });
}
