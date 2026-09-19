import {
  keepPreviousData,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import {
  addCaseComment,
  assignCase,
  caseKeys,
  changeCasePriority,
  changeCaseStatus,
  createCase,
  deleteCase,
  getCase,
  getCaseTimeline,
  getCasesSummary,
  listCases,
  updateCase,
  type CaseListQuery,
} from "@/services/cases.service";
import type {
  CaseCommentInput,
  CaseCreateInput,
  CasePriority,
  CaseStatusInput,
  CaseUpdateInput,
} from "@/types";

export const TIMELINE_PAGE_SIZE = 50;

export function useCases(query: CaseListQuery, enabled = true) {
  return useQuery({
    queryKey: caseKeys.list(query),
    queryFn: () => listCases(query),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export interface CaseRecordRef {
  type: "account" | "contact";
  id: string;
}

const RECORD_TAB_PAGE_SIZE = 50;

/**
 * The cases of an account or contact (`GET /cases?accountId=|contactId=`, 50 rows). The detail page
 * uses it for the tab label's total and the tab for its rows, so one request serves both.
 */
export function useRecordCases(record: CaseRecordRef, enabled = true) {
  return useCases(
    {
      [record.type === "account" ? "accountId" : "contactId"]: record.id,
      page: 1,
      pageSize: RECORD_TAB_PAGE_SIZE,
    },
    enabled && !!record.id
  );
}

export function useCase(id: string | undefined) {
  return useQuery({
    queryKey: caseKeys.detail(id ?? ""),
    queryFn: () => getCase(id as string),
    enabled: !!id,
  });
}

/** Merged comments + events, newest first, fetched page by page ("show more"). */
export function useCaseTimeline(id: string | undefined) {
  return useInfiniteQuery({
    queryKey: caseKeys.timeline(id ?? ""),
    queryFn: ({ pageParam }) => getCaseTimeline(id as string, pageParam, TIMELINE_PAGE_SIZE),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined,
    enabled: !!id,
  });
}

/** Counters of the home widget (`GET /cases/summary`). */
export function useCasesSummary(enabled = true) {
  return useQuery({ queryKey: caseKeys.summary, queryFn: getCasesSummary, enabled });
}

function useInvalidateCases() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: caseKeys.all }),
      queryClient.invalidateQueries({ queryKey: ["audit"] }),
      // The service report changes with every case.
      queryClient.invalidateQueries({ queryKey: ["reports", "service"] }),
    ]);
}

export function useSaveCase() {
  const invalidate = useInvalidateCases();
  return useMutation({
    mutationFn: async ({
      id,
      ...input
    }: (CaseCreateInput | CaseUpdateInput) & { id?: string }): Promise<string> => {
      if (id) {
        await updateCase(id, input as CaseUpdateInput);
        return id;
      }
      return (await createCase(input as CaseCreateInput)).id;
    },
    onSuccess: invalidate,
  });
}

export function useDeleteCase() {
  const invalidate = useInvalidateCases();
  return useMutation({ mutationFn: (id: string) => deleteCase(id), onSuccess: invalidate });
}

export function useChangeCaseStatus() {
  const invalidate = useInvalidateCases();
  return useMutation({
    mutationFn: ({ id, ...input }: CaseStatusInput & { id: string }) =>
      changeCaseStatus(id, input),
    onSuccess: invalidate,
  });
}

export function useChangeCasePriority() {
  const invalidate = useInvalidateCases();
  return useMutation({
    mutationFn: ({ id, priority }: { id: string; priority: CasePriority }) =>
      changeCasePriority(id, priority),
    onSuccess: invalidate,
  });
}

export function useAssignCase() {
  const invalidate = useInvalidateCases();
  return useMutation({
    mutationFn: ({ id, assignedUserId }: { id: string; assignedUserId: string | null }) =>
      assignCase(id, assignedUserId),
    onSuccess: invalidate,
  });
}

export function useAddCaseComment() {
  const invalidate = useInvalidateCases();
  return useMutation({
    mutationFn: ({ id, ...input }: CaseCommentInput & { id: string }) => addCaseComment(id, input),
    onSuccess: invalidate,
  });
}
