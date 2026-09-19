import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  convertQuote,
  createQuote,
  deleteQuote,
  getQuote,
  listQuotes,
  quoteKeys,
  runQuoteAction,
  updateQuote,
  type QuoteAction,
  type QuoteListQuery,
} from "@/services/quotes.service";
import type { QuoteInput } from "@/types";

export function useQuotes(query: QuoteListQuery, enabled = true) {
  return useQuery({
    queryKey: quoteKeys.list(query),
    queryFn: () => listQuotes(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useQuote(id: string | undefined) {
  return useQuery({
    queryKey: quoteKeys.detail(id ?? ""),
    queryFn: () => getQuote(id as string),
    enabled: !!id,
  });
}

/** Everything a quote change can touch: quote lists, the commerce report, audit and (converting) orders. */
function useInvalidateQuotes() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: quoteKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      queryClient.invalidateQueries({ queryKey: ["reports"] }),
    ]);
}

export function useSaveQuote() {
  const invalidate = useInvalidateQuotes();
  return useMutation({
    mutationFn: async ({ id, ...input }: QuoteInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateQuote(id, input);
        return id;
      }
      return (await createQuote(input)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteQuote() {
  const invalidate = useInvalidateQuotes();
  return useMutation({ mutationFn: (id: string) => deleteQuote(id), onSuccess: invalidate });
}

interface QuoteActionVariables {
  id: string;
  action: QuoteAction;
  reason?: string;
  validUntil?: string;
}

/**
 * send / accept / revert / reject / extend. Refetches on failure as well: a 409 usually means the
 * quote changed under the user (`quote.invalid_transition`, `commerce.concurrent_update`).
 */
export function useQuoteAction() {
  const invalidate = useInvalidateQuotes();
  return useMutation({
    mutationFn: ({ id, action, reason, validUntil }: QuoteActionVariables) =>
      runQuoteAction(
        id,
        action,
        action === "reject" ? { reason } : action === "extend" ? { validUntil } : undefined
      ),
    onSettled: () => invalidate(),
  });
}

export function useConvertQuote() {
  const queryClient = useQueryClient();
  const invalidate = useInvalidateQuotes();
  return useMutation({
    mutationFn: (id: string) => convertQuote(id),
    onSettled: async () => {
      await Promise.all([invalidate(), queryClient.invalidateQueries({ queryKey: ["orders"] })]);
    },
  });
}
