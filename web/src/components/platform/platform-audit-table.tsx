import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Code, Text } from "@mantine/core";
import { DataTable, type Column } from "@/components/crm/data-table";
import { usePlatformAudit } from "@/hooks/use-platform";
import { formatDateTime } from "@/lib/dates";
import type { PlatformAuditQuery } from "@/services/platform.service";
import type { PlatformAuditEntry } from "@/types";

/** `{ field: { old, new } }` plus `reason`; expandable so long rows stay compact. */
function AuditDetails({ entry }: { entry: PlatformAuditEntry }) {
  const { t } = useTranslation(["platform"]);
  const [open, setOpen] = useState(false);
  const hasDetails = Object.keys(entry.details ?? {}).length > 0;
  if (!hasDetails) return <>-</>;
  return (
    <>
      <Button
        variant="subtle"
        size="compact-xs"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? t("platform:audit.hideDetails") : t("platform:audit.showDetails")}
      </Button>
      {open && (
        <Code block mt={4} data-testid="audit-details">
          {JSON.stringify(entry.details, null, 2)}
        </Code>
      )}
    </>
  );
}

interface PlatformAuditTableProps {
  query: PlatformAuditQuery;
  page: number;
  pageSize: number;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  /** Hide the "organization" column when the list is already scoped to one tenant. */
  hideTenant?: boolean;
}

/** Paged platform audit list (newest first). Shared by the audit page and the organization's Audit tab. */
export function PlatformAuditTable({
  query,
  page,
  pageSize,
  onPageChange,
  onPageSizeChange,
  hideTenant = false,
}: PlatformAuditTableProps) {
  const { t } = useTranslation(["platform"]);
  const { data, isLoading, isFetching, error, refetch } = usePlatformAudit({
    ...query,
    page,
    pageSize,
  });

  const columns: Column<PlatformAuditEntry>[] = [
    {
      key: "occurredAt",
      header: t("platform:audit.columns.occurredAt"),
      render: (e) => formatDateTime(e.occurredAt),
    },
    {
      key: "action",
      header: t("platform:audit.columns.action"),
      render: (e) => t(`platform:audit.actions.${e.action}`, { defaultValue: e.action }),
    },
    {
      key: "actor",
      header: t("platform:audit.columns.actor"),
      render: (e) => e.actorEmail ?? t("platform:audit.system"),
    },
    ...(hideTenant
      ? []
      : [
          {
            key: "tenant",
            header: t("platform:audit.columns.tenant"),
            render: (e: PlatformAuditEntry) =>
              e.targetTenantId ? (
                <Anchor
                  component={Link}
                  to={`/app/platform/organizations/${e.targetTenantId}`}
                  size="sm"
                >
                  {e.targetTenantName ?? e.targetTenantId}
                </Anchor>
              ) : (
                <Text size="sm">-</Text>
              ),
          },
        ]),
    {
      key: "details",
      header: t("platform:audit.columns.details"),
      render: (e) => <AuditDetails entry={e} />,
    },
  ];

  return (
    <DataTable
      columns={columns}
      rows={data?.items}
      rowKey={(e) => e.id}
      isLoading={isLoading}
      isFetching={isFetching}
      error={error}
      onRetry={() => void refetch()}
      sort={null}
      onSort={() => undefined}
      page={page}
      pageSize={pageSize}
      totalCount={data?.totalCount}
      onPageChange={onPageChange}
      onPageSizeChange={onPageSizeChange}
      minWidth={760}
    />
  );
}
