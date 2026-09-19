import { describe, expect, it } from "vitest";
import type { DealBoard } from "@/types";
import { applyStageMove, boardTotals } from "./board";

const board: DealBoard = {
  pipelineId: "p1",
  stages: [
    {
      id: "a",
      name: "A",
      kind: "open",
      probability: 10,
      count: 5,
      totalAmount: 900,
      deals: [
        { id: "d1", name: "One", accountName: "X", amount: 100, currency: "TRY" },
        { id: "d2", name: "Two", accountName: "Y", currency: "TRY" },
      ],
    },
    { id: "b", name: "B", kind: "open", probability: 50, count: 1, totalAmount: 50, deals: [] },
  ],
};

describe("applyStageMove", () => {
  it("moves the card to the top of the target column and adjusts count and total", () => {
    const next = applyStageMove(board, "d1", "b");

    expect(next.stages[0]?.deals.map((d) => d.id)).toEqual(["d2"]);
    expect(next.stages[0]).toMatchObject({ count: 4, totalAmount: 800 });
    expect(next.stages[1]?.deals.map((d) => d.id)).toEqual(["d1"]);
    expect(next.stages[1]).toMatchObject({ count: 2, totalAmount: 150 });
  });

  it("does not mutate the previous board (needed for rollback)", () => {
    const snapshot = JSON.stringify(board);
    applyStageMove(board, "d1", "b");
    expect(JSON.stringify(board)).toBe(snapshot);
  });

  it("treats a missing amount as zero", () => {
    const next = applyStageMove(board, "d2", "b");
    expect(next.stages[0]?.totalAmount).toBe(900);
    expect(next.stages[1]?.totalAmount).toBe(50);
  });

  it("returns the same board for unknown ids or a move within the same stage", () => {
    expect(applyStageMove(board, "nope", "b")).toBe(board);
    expect(applyStageMove(board, "d1", "zzz")).toBe(board);
    expect(applyStageMove(board, "d1", "a")).toBe(board);
  });
});

describe("boardTotals", () => {
  it("sums counts and amounts over all stages", () => {
    expect(boardTotals(board)).toEqual({ count: 6, totalAmount: 950 });
    expect(boardTotals(undefined)).toEqual({ count: 0, totalAmount: 0 });
  });
});
