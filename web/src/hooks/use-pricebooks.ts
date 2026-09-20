import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  clearAccountDefaultPriceBook,
  createPriceBook,
  deletePriceBook,
  deletePriceBookEntry,
  getAccountDefaultPriceBook,
  getPriceBook,
  listPriceBookEntries,
  listPriceBooks,
  priceBookKeys,
  setAccountDefaultPriceBook,
  updatePriceBook,
  upsertPriceBookEntry,
  type PriceBookEntryQuery,
  type PriceBookListQuery,
} from "@/services/pricebooks.service";
import type { PriceBookInput } from "@/types";

export function usePriceBooks(query: PriceBookListQuery, enabled = true) {
  return useQuery({
    queryKey: priceBookKeys.list(query),
    queryFn: () => listPriceBooks(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function usePriceBook(id: string | undefined) {
  return useQuery({
    queryKey: priceBookKeys.detail(id ?? ""),
    queryFn: () => getPriceBook(id as string),
    enabled: !!id,
  });
}

export function usePriceBookEntries(id: string | undefined, query: PriceBookEntryQuery, enabled = true) {
  return useQuery({
    queryKey: priceBookKeys.entries(id ?? "", query),
    queryFn: () => listPriceBookEntries(id as string, query),
    placeholderData: keepPreviousData,
    enabled: !!id && enabled,
  });
}

function useInvalidatePriceBooks() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: priceBookKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
    ]);
}

export function useSavePriceBook() {
  const invalidate = useInvalidatePriceBooks();
  return useMutation({
    mutationFn: async ({ id, ...input }: PriceBookInput & { id?: string }): Promise<string> => {
      if (id) {
        await updatePriceBook(id, input);
        return id;
      }
      return (await createPriceBook(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeletePriceBook() {
  const invalidate = useInvalidatePriceBooks();
  return useMutation({ mutationFn: (id: string) => deletePriceBook(id), onSuccess: invalidate });
}

export function useUpsertPriceBookEntry() {
  const invalidate = useInvalidatePriceBooks();
  return useMutation({
    mutationFn: ({ id, productId, unitPrice }: { id: string; productId: string; unitPrice: number }) =>
      upsertPriceBookEntry(id, productId, unitPrice),
    onSuccess: invalidate,
  });
}

export function useDeletePriceBookEntry() {
  const invalidate = useInvalidatePriceBooks();
  return useMutation({
    mutationFn: ({ id, productId }: { id: string; productId: string }) =>
      deletePriceBookEntry(id, productId),
    onSuccess: invalidate,
  });
}

/** `GET /pricebooks/accounts/{id}/default` (200 or 204 -> `null`); needs `crm.pricebooks.read`. */
export function useAccountDefaultPriceBook(accountId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: priceBookKeys.accountDefault(accountId ?? ""),
    queryFn: () => getAccountDefaultPriceBook(accountId as string),
    enabled: !!accountId && enabled,
  });
}

export function useSetAccountDefaultPriceBook() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ accountId, priceBookId }: { accountId: string; priceBookId: string | null }) =>
      priceBookId
        ? setAccountDefaultPriceBook(accountId, priceBookId)
        : clearAccountDefaultPriceBook(accountId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: priceBookKeys.all }),
  });
}
