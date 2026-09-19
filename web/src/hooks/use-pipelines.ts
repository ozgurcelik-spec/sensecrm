import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createPipeline,
  listPipelines,
  pipelineKeys,
  updatePipeline,
  updatePipelineStages,
} from "@/services/pipelines.service";
import type { StageInput } from "@/types";

export function usePipelines(enabled = true) {
  return useQuery({
    queryKey: pipelineKeys.all,
    queryFn: listPipelines,
    staleTime: 60_000,
    enabled,
  });
}

function useInvalidatePipelines() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: pipelineKeys.all }),
      // Stage names/probabilities are denormalised into deals and the board.
      queryClient.invalidateQueries({ queryKey: ["deals"] }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
    ]);
}

export function useCreatePipeline() {
  const invalidate = useInvalidatePipelines();
  return useMutation({
    mutationFn: (name: string) => createPipeline(name),
    onSuccess: invalidate,
  });
}

export function useUpdatePipeline() {
  const invalidate = useInvalidatePipelines();
  return useMutation({
    mutationFn: ({ id, ...request }: { id: string; name: string; isDefault: boolean }) =>
      updatePipeline(id, request),
    onSuccess: invalidate,
  });
}

export function useUpdatePipelineStages() {
  const invalidate = useInvalidatePipelines();
  return useMutation({
    mutationFn: ({ id, stages }: { id: string; stages: StageInput[] }) =>
      updatePipelineStages(id, stages),
    onSuccess: invalidate,
  });
}
