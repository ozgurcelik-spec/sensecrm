/** Commerce report - `GET /reports/commerce/summary` (`crm.reports.read`). */
import { apiClient } from "@/lib/api-client";
import type { CommerceSummaryReport } from "@/types";
import { cleanParams } from "./crm-http";
import type { DateRangeQuery } from "./reports.service";

export const commerceReportKeys = {
  summary: (query: DateRangeQuery) => ["reports", "commerce-summary", query] as const,
};

export async function getCommerceSummary(query: DateRangeQuery): Promise<CommerceSummaryReport> {
  const { data } = await apiClient.get<CommerceSummaryReport>("/reports/commerce/summary", {
    params: cleanParams({ ...query }),
  });
  return data;
}
