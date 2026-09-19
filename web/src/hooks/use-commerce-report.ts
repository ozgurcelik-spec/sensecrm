import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { commerceReportKeys, getCommerceSummary } from "@/services/commerce-reports.service";
import type { DateRangeQuery } from "@/services/reports.service";

export function useCommerceSummary(query: DateRangeQuery, enabled = true) {
  return useQuery({
    queryKey: commerceReportKeys.summary(query),
    queryFn: () => getCommerceSummary(query),
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    enabled,
  });
}
