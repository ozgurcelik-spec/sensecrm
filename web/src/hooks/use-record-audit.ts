import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { listRecordAudit, recordAuditKeys } from "@/services/audit.service";
import type { AuditEntityType } from "@/types";

export function useRecordAudit(
  entityType: AuditEntityType,
  entityId: string | undefined,
  page: number,
  pageSize: number,
  enabled = true
) {
  return useQuery({
    queryKey: recordAuditKeys.record(entityType, entityId ?? "", page),
    queryFn: () => listRecordAudit(entityType, entityId as string, page, pageSize),
    placeholderData: keepPreviousData,
    enabled: enabled && !!entityId,
  });
}
