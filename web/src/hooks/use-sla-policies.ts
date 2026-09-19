import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { caseKeys, getSlaPolicies, updateSlaPolicies } from "@/services/cases.service";
import type { SlaPolicy } from "@/types";

/** The four SLA policies (low, normal, high, urgent), `org.settings.manage`. */
export function useSlaPolicies(enabled = true) {
  return useQuery({ queryKey: caseKeys.slaPolicies, queryFn: getSlaPolicies, enabled });
}

export function useUpdateSlaPolicies() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (policies: SlaPolicy[]) => updateSlaPolicies(policies),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: caseKeys.slaPolicies });
      await queryClient.invalidateQueries({ queryKey: ["audit"] });
    },
  });
}
