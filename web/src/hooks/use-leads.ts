import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  convertLead,
  createLead,
  deleteLead,
  getLead,
  leadKeys,
  listLeads,
  updateLead,
  type LeadListQuery,
} from "@/services/leads.service";
import type { ConvertLeadInput, LeadInput } from "@/types";

export function useLeads(query: LeadListQuery, enabled = true) {
  return useQuery({
    queryKey: leadKeys.list(query),
    queryFn: () => listLeads(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useLead(id: string | undefined) {
  return useQuery({
    queryKey: leadKeys.detail(id ?? ""),
    queryFn: () => getLead(id as string),
    enabled: !!id,
  });
}

export function useSaveLead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...input }: LeadInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateLead(id, input);
        return id;
      }
      return (await createLead(input)).id;
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: leadKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

export function useDeleteLead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteLead(id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: leadKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

/** Converts a lead into an account + contact (+ deal); all four resources change. */
export function useConvertLead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, ...input }: ConvertLeadInput & { id: string }) => convertLead(id, input),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: leadKeys.all }),
        queryClient.invalidateQueries({ queryKey: ["accounts"] }),
        queryClient.invalidateQueries({ queryKey: ["contacts"] }),
        queryClient.invalidateQueries({ queryKey: ["deals"] }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}
