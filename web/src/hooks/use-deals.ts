import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { applyStageMove } from "@/lib/board";
import {
  createDeal,
  dealKeys,
  deleteDeal,
  getDeal,
  getDealBoard,
  listDeals,
  moveDealStage,
  updateDeal,
  type DealBoardQuery,
  type DealListQuery,
} from "@/services/deals.service";
import type { DealBoard, DealInput } from "@/types";

export function useDeals(query: DealListQuery, enabled = true) {
  return useQuery({
    queryKey: dealKeys.list(query),
    queryFn: () => listDeals(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useDeal(id: string | undefined) {
  return useQuery({
    queryKey: dealKeys.detail(id ?? ""),
    queryFn: () => getDeal(id as string),
    enabled: !!id,
  });
}

export function useDealBoard(query: DealBoardQuery, enabled = true) {
  return useQuery({
    queryKey: dealKeys.board(query),
    queryFn: () => getDealBoard(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useSaveDeal() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...input }: DealInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateDeal(id, input);
        return id;
      }
      return (await createDeal(input)).id;
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: dealKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["accounts"] }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

export function useDeleteDeal() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteDeal(id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: dealKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["accounts"] }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

interface MoveStageVariables {
  dealId: string;
  stageId: string;
  lostReason?: string;
}

/**
 * Moves a deal to another stage. When a `board` query is given the kanban cache is updated
 * optimistically and rolled back if the server rejects the move; either way the board and lists
 * are refetched afterwards so the totals come from the server.
 */
export function useMoveDealStage(board?: DealBoardQuery) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ dealId, stageId, lostReason }: MoveStageVariables) =>
      moveDealStage(dealId, { stageId, lostReason }),
    onMutate: async ({ dealId, stageId }) => {
      if (!board) return { previous: undefined };
      const key = dealKeys.board(board);
      await queryClient.cancelQueries({ queryKey: key });
      const previous = queryClient.getQueryData<DealBoard>(key);
      if (previous)
        queryClient.setQueryData<DealBoard>(key, applyStageMove(previous, dealId, stageId));
      return { previous };
    },
    onError: (_error, _variables, context) => {
      if (board && context?.previous) {
        queryClient.setQueryData(dealKeys.board(board), context.previous);
      }
    },
    onSettled: () => {
      // Not awaited: the toast / dialog close must not wait for the refetch round trip.
      void queryClient.invalidateQueries({ queryKey: dealKeys.all });
      void queryClient.invalidateQueries({ queryKey: ["audit"] });
    },
  });
}
