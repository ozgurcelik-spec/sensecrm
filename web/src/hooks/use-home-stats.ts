import { useQueries } from "@tanstack/react-query";
import { useDealBoard } from "@/hooks/use-deals";
import { leadKeys, listLeads } from "@/services/leads.service";
import type { LeadStatus } from "@/types";

const OPEN_LEAD_STATUSES: readonly LeadStatus[] = ["new", "contacted", "qualified"];

/**
 * Open leads = new + contacted + qualified. The list endpoint filters by a single status, so this is
 * three cheap `pageSize=1` requests whose `totalCount`s are added up.
 */
export function useOpenLeadsCount(enabled: boolean) {
  const results = useQueries({
    queries: OPEN_LEAD_STATUSES.map((status) => {
      const query = { status, page: 1, pageSize: 1 };
      return { queryKey: leadKeys.list(query), queryFn: () => listLeads(query), enabled };
    }),
  });
  const loading = results.some((r) => r.isLoading);
  const failed = results.some((r) => r.isError);
  const count = results.reduce((sum, r) => sum + (r.data?.totalCount ?? 0), 0);
  return {
    count,
    isLoading: loading,
    isError: failed,
    refetch: () => results.forEach((r) => void r.refetch()),
  };
}

/** Open deals and their total amount, from the default pipeline's board (`GET /deals/board`). */
export function useOpenDealsSummary(enabled: boolean) {
  const board = useDealBoard({}, enabled);
  const open = (board.data?.stages ?? []).filter((s) => s.kind === "open");
  return {
    count: open.reduce((sum, s) => sum + s.count, 0),
    totalAmount: open.reduce((sum, s) => sum + s.totalAmount, 0),
    isLoading: board.isLoading,
    isError: board.isError,
    refetch: () => void board.refetch(),
  };
}
