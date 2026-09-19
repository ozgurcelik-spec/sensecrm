import type { DealBoard } from "@/types";

/**
 * Optimistic kanban update: moves a deal card to the target stage (top of the column) and keeps the
 * column counts and totals consistent. Returns the same board object when nothing changes.
 * Only the cards loaded on the board (max 100 per column) can be moved; counts/totals of columns
 * that hold more cards than were loaded stay correct because they are adjusted, not recomputed.
 */
export function applyStageMove(board: DealBoard, dealId: string, targetStageId: string): DealBoard {
  const source = board.stages.find((stage) => stage.deals.some((deal) => deal.id === dealId));
  const target = board.stages.find((stage) => stage.id === targetStageId);
  const deal = source?.deals.find((d) => d.id === dealId);
  if (!source || !target || !deal || source.id === target.id) return board;

  const amount = deal.amount ?? 0;
  return {
    ...board,
    stages: board.stages.map((stage) => {
      if (stage.id === source.id) {
        return {
          ...stage,
          count: Math.max(0, stage.count - 1),
          totalAmount: stage.totalAmount - amount,
          deals: stage.deals.filter((d) => d.id !== dealId),
        };
      }
      if (stage.id === target.id) {
        return {
          ...stage,
          count: stage.count + 1,
          totalAmount: stage.totalAmount + amount,
          deals: [deal, ...stage.deals],
        };
      }
      return stage;
    }),
  };
}

/** Sums a numeric field of every board stage (e.g. the open pipeline value). */
export function boardTotals(board: DealBoard | undefined): { count: number; totalAmount: number } {
  return (board?.stages ?? []).reduce(
    (acc, stage) => ({
      count: acc.count + stage.count,
      totalAmount: acc.totalAmount + stage.totalAmount,
    }),
    { count: 0, totalAmount: 0 }
  );
}
