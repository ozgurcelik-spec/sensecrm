/** Sales pipelines and their stages - `/pipelines`. */
import { apiClient } from "@/lib/api-client";
import type { Pipeline, StageInput } from "@/types";
import { getArray, getOne, seg } from "./crm-http";

export const pipelineKeys = {
  all: ["pipelines"] as const,
};

export const listPipelines = (): Promise<Pipeline[]> => getArray<Pipeline>("/pipelines");

export const getPipeline = (id: string): Promise<Pipeline> =>
  getOne<Pipeline>(`/pipelines/${seg(id)}`);

export async function createPipeline(name: string): Promise<Pipeline> {
  const { data } = await apiClient.post<Pipeline>("/pipelines", { name });
  return data;
}

export async function updatePipeline(
  id: string,
  request: { name: string; isDefault: boolean }
): Promise<void> {
  await apiClient.put(`/pipelines/${seg(id)}`, request);
}

export async function updatePipelineStages(id: string, stages: StageInput[]): Promise<void> {
  await apiClient.put(`/pipelines/${seg(id)}/stages`, { stages });
}
