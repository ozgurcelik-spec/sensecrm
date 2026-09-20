import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Badge, Group, Stack, Text } from "@mantine/core";
import { DataTable, type Column } from "@/components/crm/data-table";
import type { ListParams } from "@/hooks/use-list-params";
import { formatDate } from "@/lib/dates";
import { formatCalendarDate, formatNumber } from "@/lib/format";
import type { ListResult, PlatformOrganization } from "@/types";
import { TenantStatusBadge } from "./status-badge";

interface OrganizationTableProps {
  params: ListParams<"status" | "planCode" | "source">;
  data: ListResult<PlatformOrganization> | undefined;
  isLoading: boolean;
  isFetching: boolean;
  error: unknown;
  onRetry: () => void;
}

/** Compact per-module record counts of the latest usage snapshot ("Satış 340 · Aktivite 120"). */
function RecordCounts({ org }: { org: PlatformOrganization }) {
  const { t } = useTranslation(["subscription"]);
  const entries = Object.entries(org.usage?.records ?? {});
  if (entries.length === 0) return <>-</>;
  return (
    <Text size="xs" c="dimmed">
      {entries
        .map(
          ([module, count]) =>
            `${t(`subscription:modules.${module}`, { defaultValue: module })} ${formatNumber(count)}`
        )
        .join(" · ")}
    </Text>
  );
}

/** Organizations of the platform console: server side paging / sorting, state lives in the URL. */
export function OrganizationTable({
  params,
  data,
  isLoading,
  isFetching,
  error,
  onRetry,
}: OrganizationTableProps) {
  const { t } = useTranslation(["platform"]);

  const columns: Column<PlatformOrganization>[] = [
    {
      key: "name",
      header: t("platform:columns.name"),
      sortField: "name",
      render: (org) => (
        <Stack gap={0}>
          <Group gap="xs" wrap="nowrap">
            <Anchor
              component={Link}
              to={`/app/platform/organizations/${org.tenantId}`}
              size="sm"
              fw={500}
            >
              {org.name}
            </Anchor>
            {org.isSystem && (
              <Badge size="xs" variant="outline" color="gray">
                {t("platform:system.badge")}
              </Badge>
            )}
          </Group>
          <Text size="xs" c="dimmed">
            {org.slug}
          </Text>
        </Stack>
      ),
    },
    {
      key: "plan",
      header: t("platform:columns.plan"),
      sortField: "plan",
      render: (org) => org.planName,
    },
    {
      key: "status",
      header: t("platform:columns.status"),
      sortField: "status",
      render: (org) => <TenantStatusBadge status={org.status} />,
    },
    {
      key: "trial",
      header: t("platform:columns.trialEnds"),
      render: (org) => formatCalendarDate(org.trialEndsOn),
    },
    {
      key: "users",
      header: t("platform:columns.users"),
      sortField: "users",
      render: (org) =>
        org.usage
          ? org.usage.usersPending > 0
            ? t("platform:usersWithPending", {
                active: formatNumber(org.usage.usersActive),
                pending: formatNumber(org.usage.usersPending),
              })
            : formatNumber(org.usage.usersActive)
          : "-",
    },
    { key: "records", header: t("platform:columns.records"), render: (org) => <RecordCounts org={org} /> },
    {
      key: "source",
      header: t("platform:columns.source"),
      render: (org) => t(`platform:sources.${org.source}`, { defaultValue: org.source }),
    },
    {
      key: "createdAt",
      header: t("platform:columns.createdAt"),
      sortField: "createdAt",
      render: (org) => formatDate(org.createdAt),
    },
  ];

  return (
    <DataTable
      columns={columns}
      rows={data?.items}
      rowKey={(org) => org.tenantId}
      isLoading={isLoading}
      isFetching={isFetching}
      error={error}
      onRetry={onRetry}
      sort={params.sort}
      onSort={params.toggleSort}
      page={params.page}
      pageSize={params.pageSize}
      totalCount={data?.totalCount}
      onPageChange={params.setPage}
      onPageSizeChange={params.setPageSize}
      minWidth={1000}
    />
  );
}
