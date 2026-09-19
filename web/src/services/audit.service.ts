/** Per-record audit trail - `GET /audit?entityType&entityId` (same shape as `/organization/audit`). */
import { apiClient } from "@/lib/api-client";
import type { AuditEntityType, RecordAuditEntry } from "@/types";

export const recordAuditKeys = {
  record: (entityType: AuditEntityType, entityId: string, page: number) =>
    ["audit", entityType, entityId, page] as const,
  entity: (entityType: AuditEntityType, entityId: string) =>
    ["audit", entityType, entityId] as const,
};

export interface RecordAuditPage {
  items: RecordAuditEntry[];
  total: number;
}

export async function listRecordAudit(
  entityType: AuditEntityType,
  entityId: string,
  page: number,
  pageSize: number
): Promise<RecordAuditPage> {
  const { data } = await apiClient.get<Partial<RecordAuditPage> & { totalCount?: number }>(
    "/audit",
    { params: { entityType, entityId, page, pageSize } }
  );
  // The contract says "same shape as /organization/audit" ({ items, total }); tolerate totalCount.
  return { items: data.items ?? [], total: data.total ?? data.totalCount ?? 0 };
}
