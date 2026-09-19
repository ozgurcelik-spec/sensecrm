import type { StageKind } from "@/types";

const STAGE_COLOR: Record<StageKind, string> = { open: "blue", won: "green", lost: "red" };

/** Mantine color of a pipeline stage kind (open / won / lost). */
export function stageColor(kind: StageKind): string {
  return STAGE_COLOR[kind] ?? "gray";
}
