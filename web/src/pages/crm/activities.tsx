import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Select, Tabs, Text } from "@mantine/core";
import { ActivityDate, RelatedRecordLink } from "@/components/activities/activity-cells";
import { ActivityFormDialog } from "@/components/activities/activity-form-dialog";
import { ActivityStatusButton } from "@/components/activities/activity-status-control";
import {
  ActivityPriorityBadge,
  ActivityStatusBadge,
  ActivityTypeBadge,
} from "@/components/crm/badges";
import { DataTable, type Column } from "@/components/crm/data-table";
import { ListPageFrame, RowActions } from "@/components/crm/list-page-frame";
import { OwnerSelect } from "@/components/crm/owner-select";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { useActivities, useDeleteActivity } from "@/hooks/use-activities";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { toast, toastApiError } from "@/hooks/use-toast";
import { todayBoundsIso } from "@/lib/zoned-time";
import { useAuthStore } from "@/store/auth.store";
import type { ActivityListQuery } from "@/services/activities.service";
import { ACTIVITY_STATUSES, ACTIVITY_TYPES, type Activity, type ActivityType } from "@/types";

// `due=today` and `overdue=true` are the quick filters; they are turned into API params below.
const FILTERS = ["type", "status", "assignedUserId", "due", "overdue"] as const;

function isActivityType(value: string): value is ActivityType {
  return (ACTIVITY_TYPES as readonly string[]).includes(value);
}

export default function ActivitiesPage() {
  const { t } = useTranslation(["activities", "common"]);
  const { canWriteActivities } = useCrmPermissions();
  const me = useAuthStore((state) => state.me);
  const timeZone = me?.organization.timeZone;
  const params = useListParams(FILTERS);

  const apiQuery = useMemo<ActivityListQuery>(() => {
    const { due, overdue, ...rest } = params.query;
    const query: ActivityListQuery = { ...rest };
    if (overdue === "true") query.overdue = true;
    if (due === "today") {
      const bounds = todayBoundsIso(timeZone);
      query.dueFrom = bounds.from;
      query.dueTo = bounds.to;
    }
    return query;
  }, [params.query, timeZone]);

  const { data, isLoading, isFetching, error, refetch } = useActivities(apiQuery);
  const remove = useDeleteActivity();
  const [editing, setEditing] = useState<Activity | "new" | null>(null);
  const [deleting, setDeleting] = useState<Activity | null>(null);

  const typeFilter = params.filters.type;
  const isToday = params.filters.due === "today";
  const isOverdue = params.filters.overdue === "true";
  const isMine = !!me && params.filters.assignedUserId === me.user.id;
  const noQuickFilter = !isToday && !isOverdue && !isMine;

  function setQuick(key: "today" | "overdue" | "mine" | "all") {
    if (key === "all") params.setFilters({ due: null, overdue: null, assignedUserId: null });
    else if (key === "today") params.setFilters({ overdue: null, due: isToday ? null : "today" });
    else if (key === "overdue")
      params.setFilters({ due: null, overdue: isOverdue ? null : "true" });
    else params.setFilter("assignedUserId", isMine ? null : (me?.user.id ?? null));
  }

  const columns: Column<Activity>[] = [
    {
      key: "subject",
      header: t("activities:fields.subject"),
      sortField: "subject",
      render: (a) => (
        <Text
          size="sm"
          fw={500}
          td={a.status === "completed" && a.type !== "note" ? "line-through" : undefined}
        >
          {a.subject}
        </Text>
      ),
    },
    {
      key: "type",
      header: t("activities:fields.type"),
      render: (a) => <ActivityTypeBadge type={a.type} />,
    },
    {
      key: "related",
      header: t("activities:fields.related"),
      render: (a) => <RelatedRecordLink activity={a} />,
    },
    {
      key: "when",
      header: t("activities:fields.when"),
      sortField: "dueAt",
      render: (a) => <ActivityDate activity={a} />,
    },
    {
      key: "assignee",
      header: t("activities:fields.assignee"),
      render: (a) => a.assignedUserName ?? "-",
    },
    {
      key: "priority",
      header: t("activities:fields.priority"),
      sortField: "priority",
      render: (a) => (a.type === "note" ? "-" : <ActivityPriorityBadge priority={a.priority} />),
    },
    {
      key: "status",
      header: t("activities:fields.status"),
      render: (a) => <ActivityStatusBadge status={a.status} />,
    },
    {
      key: "actions",
      header: "",
      width: 130,
      render: (a) => (
        <Group gap={4} wrap="nowrap" justify="flex-end">
          {canWriteActivities && <ActivityStatusButton activity={a} />}
          <RowActions
            editLabel={t("common:edit")}
            deleteLabel={t("common:delete")}
            onEdit={canWriteActivities ? () => setEditing(a) : undefined}
            onDelete={canWriteActivities ? () => setDeleting(a) : undefined}
          />
        </Group>
      ),
    },
  ];

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("activities:deleted") });
    } catch (err) {
      toastApiError(err);
    }
    setDeleting(null);
  }

  return (
    <>
      <ListPageFrame
        title={t("activities:title")}
        description={t("activities:description")}
        createLabel={t("activities:new")}
        onCreate={canWriteActivities ? () => setEditing("new") : undefined}
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("activities:search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("activities:fields.status")}
              placeholder={t("activities:fields.status")}
              w={160}
              clearable
              data={ACTIVITY_STATUSES.map((s) => ({
                value: s,
                label: t(`activities:statuses.${s}`),
              }))}
              value={params.filters.status || null}
              onChange={(value) => params.setFilter("status", value)}
            />
            <OwnerSelect
              label={undefined}
              aria-label={t("activities:fields.assignee")}
              placeholder={t("activities:fields.assignee")}
              w={200}
              clearable
              value={params.filters.assignedUserId || null}
              onChange={(value) => params.setFilter("assignedUserId", value)}
            />
          </>
        }
      >
        <Tabs
          value={isActivityType(typeFilter) ? typeFilter : "all"}
          onChange={(value) => params.setFilter("type", value === "all" ? null : value)}
        >
          <Tabs.List aria-label={t("activities:typeTabs")}>
            <Tabs.Tab value="all">{t("activities:quick.all")}</Tabs.Tab>
            {ACTIVITY_TYPES.map((type) => (
              <Tabs.Tab key={type} value={type}>
                {t(`activities:types.${type}`)}
              </Tabs.Tab>
            ))}
          </Tabs.List>
        </Tabs>

        <Group gap="xs" role="group" aria-label={t("activities:quick.label")}>
          {(
            [
              ["all", noQuickFilter],
              ["today", isToday],
              ["overdue", isOverdue],
              ["mine", isMine],
            ] as const
          ).map(([key, active]) => (
            <Button
              key={key}
              size="xs"
              radius="xl"
              variant={active ? "filled" : "default"}
              aria-pressed={active}
              onClick={() => setQuick(key)}
            >
              {t(`activities:quick.${key}`)}
            </Button>
          ))}
        </Group>

        <DataTable
          columns={columns}
          rows={data?.items}
          rowKey={(a) => a.id}
          isLoading={isLoading}
          isFetching={isFetching}
          error={error}
          onRetry={() => void refetch()}
          sort={params.sort}
          onSort={params.toggleSort}
          page={params.page}
          pageSize={params.pageSize}
          totalCount={data?.totalCount}
          onPageChange={params.setPage}
          onPageSizeChange={params.setPageSize}
          minWidth={1000}
        />
      </ListPageFrame>

      {editing && (
        <ActivityFormDialog
          activity={editing === "new" ? undefined : editing}
          defaultType={isActivityType(typeFilter) ? typeFilter : "task"}
          onClose={() => setEditing(null)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("activities:deleteTitle")}
        message={t("activities:deleteMessage", { name: deleting?.subject })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
