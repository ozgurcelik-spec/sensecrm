import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toastApiError } from "@/hooks/use-toast";
import { approvalKeys } from "@/services/approvals.service";
import {
  createRule,
  deleteRule,
  getExecution,
  listExecutions,
  listRules,
  retryExecution,
  setRuleEnabled,
  terminateExecution,
  updateRule,
  workflowKeys,
  type ExecutionListQuery,
} from "@/services/workflows.service";
import type { WorkflowRule, WorkflowRuleInput, WorkflowSubjectType } from "@/types";

export function useWorkflowRules(enabled = true) {
  return useQuery({ queryKey: workflowKeys.rules, queryFn: listRules, enabled });
}

export function useSaveWorkflowRule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...input }: WorkflowRuleInput & { id?: string }): Promise<string> => {
      if (id) {
        await updateRule(id, input);
        return id;
      }
      return (await createRule(input)).id;
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: workflowKeys.rules }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

export function useDeleteWorkflowRule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deleteRule(id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: workflowKeys.rules }),
        queryClient.invalidateQueries({ queryKey: ["audit"] }),
      ]);
    },
  });
}

/**
 * Enable / disable a rule with an optimistic update of the cached rule list: the switch flips at
 * once and is restored (with an error toast) if the request fails. The list is refetched afterwards.
 */
export function useSetRuleEnabled() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => setRuleEnabled(id, enabled),
    onMutate: async ({ id, enabled }) => {
      await queryClient.cancelQueries({ queryKey: workflowKeys.rules });
      const previous = queryClient.getQueryData<WorkflowRule[]>(workflowKeys.rules);
      queryClient.setQueryData<WorkflowRule[]>(workflowKeys.rules, (old) =>
        old?.map((rule) => (rule.id === id ? { ...rule, isEnabled: enabled } : rule))
      );
      return { previous };
    },
    onError: (error, _variables, context) => {
      queryClient.setQueryData(workflowKeys.rules, context?.previous);
      toastApiError(error);
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: workflowKeys.rules }),
  });
}

export function useExecutions(query: ExecutionListQuery, enabled = true) {
  return useQuery({
    queryKey: workflowKeys.executionList(query),
    queryFn: () => listExecutions(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

/** The newest execution of one lead or deal (undefined when there is none); `enabled` gates the request. */
export function useSubjectExecution(
  subjectType: WorkflowSubjectType,
  subjectId: string | undefined,
  enabled = true
) {
  const query: ExecutionListQuery = { subjectType, subjectId, page: 1, pageSize: 1 };
  return useQuery({
    queryKey: workflowKeys.executionList(query),
    queryFn: () => listExecutions(query),
    select: (result) => result.items[0],
    enabled: enabled && !!subjectId,
    // The strip is decoration: a failure must stay silent and not be retried noisily.
    retry: false,
  });
}

export function useExecution(id: string | undefined) {
  return useQuery({
    queryKey: workflowKeys.execution(id ?? ""),
    queryFn: () => getExecution(id as string),
    enabled: !!id,
  });
}

function useInvalidateExecutions() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: workflowKeys.executions }),
      // Terminating cancels the pending approvals; a retry may open new ones.
      queryClient.invalidateQueries({ queryKey: approvalKeys.all }),
    ]);
}

export function useTerminateExecution() {
  const invalidate = useInvalidateExecutions();
  return useMutation({ mutationFn: (id: string) => terminateExecution(id), onSettled: invalidate });
}

export function useRetryExecution() {
  const invalidate = useInvalidateExecutions();
  return useMutation({ mutationFn: (id: string) => retryExecution(id), onSettled: invalidate });
}
