import { useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Group, Menu, MultiSelect, Select, Text, Tooltip } from "@mantine/core";
import { ChevronDown, Plus, UserMinus } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { DataTable, type Column } from "@/components/crm/data-table";
import { RowActions } from "@/components/crm/list-page-frame";
import {
  useCampaignMembers,
  useRemoveCampaignMembers,
  useSetMembersStatus,
} from "@/hooks/use-campaigns";
import { useCrmPermissions } from "@/hooks/use-crm-permissions";
import { useListParams } from "@/hooks/use-list-params";
import { usePermission } from "@/hooks/use-permission";
import { useRowSelection } from "@/hooks/use-row-selection";
import { toast, toastApiError } from "@/hooks/use-toast";
import { summarizeStatusResult, splitList } from "@/lib/campaign";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import {
  MEMBER_MANUAL_STATUSES,
  MEMBER_STATUSES,
  MEMBER_TYPES,
  PERMISSIONS,
  isCampaignClosed,
  type Campaign,
  type CampaignMember,
  type MemberManualStatus,
} from "@/types";
import { AddMembersDialog } from "./add-members-dialog";
import { MemberStatusBadge } from "./campaign-badges";

const FILTERS = ["memberType", "status"] as const;

/** "Members" tab: paged member table with filters, per-row status, bulk status / removal and "Add members". */
export function CampaignMembersTab({ campaign }: { campaign: Campaign }) {
  const { t } = useTranslation(["campaigns", "common"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const { canWriteCampaigns } = useCrmPermissions();
  const canReadLeads = usePermission(PERMISSIONS.crmLeadsRead);
  const canReadContacts = usePermission(PERMISSIONS.crmContactsRead);
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = useCampaignMembers(
    campaign.id,
    params.query
  );
  const selection = useRowSelection(JSON.stringify(params.query));
  const setStatus = useSetMembersStatus();
  const remove = useRemoveCampaignMembers();
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<CampaignMember[] | null>(null);
  const closed = isCampaignClosed(campaign.status);

  async function changeStatus(ids: string[], status: MemberManualStatus) {
    try {
      const result = await setStatus.mutateAsync({ campaignId: campaign.id, memberIds: ids, status });
      toast({
        variant: result.updatedCount > 0 ? "success" : "default",
        description: summarizeStatusResult(result, t),
      });
      selection.clear();
    } catch (err) {
      toastApiError(err);
    }
  }

  async function confirmRemove() {
    if (!removing) return;
    try {
      const result = await remove.mutateAsync({
        campaignId: campaign.id,
        memberIds: removing.map((m) => m.id),
      });
      toast({
        variant: "success",
        description: t("campaigns:members.removed", { count: result.removedCount }),
      });
      selection.clear();
    } catch (err) {
      toastApiError(err);
    }
    setRemoving(null);
  }

  function memberName(member: CampaignMember) {
    if (member.memberMissing || !member.memberName) {
      return (
        <Text size="sm" c="dimmed" fs="italic">
          {t("campaigns:members.missing")}
        </Text>
      );
    }
    const canOpen = member.memberType === "lead" ? canReadLeads : canReadContacts;
    if (!canOpen) return <Text size="sm">{member.memberName}</Text>;
    const path = member.memberType === "lead" ? "leads" : "contacts";
    return (
      <Anchor component={Link} to={`/app/${path}/${member.memberId}`} size="sm" fw={500}>
        {member.memberName}
      </Anchor>
    );
  }

  const displayName = (member: CampaignMember) =>
    member.memberName ?? t("campaigns:members.missing");

  const columns: Column<CampaignMember>[] = [
    {
      key: "type",
      header: t("campaigns:members.type"),
      sortField: "memberType",
      width: 120,
      render: (m) => t(`campaigns:memberTypes.${m.memberType}`),
    },
    { key: "name", header: t("campaigns:members.name"), render: memberName },
    {
      key: "status",
      header: t("campaigns:members.status"),
      sortField: "status",
      width: 190,
      render: (m) => {
        if (!canWriteCampaigns) return <MemberStatusBadge status={m.status} />;
        const locked = m.status === "converted";
        return (
          <Tooltip label={t("campaigns:members.locked")} disabled={!locked}>
            <Select
              size="xs"
              aria-label={t("campaigns:members.statusOf", { name: displayName(m) })}
              // A converted member keeps its status: the option exists only to show it.
              data={[
                ...MEMBER_MANUAL_STATUSES.map((s) => ({
                  value: s,
                  label: t(`campaigns:memberStatuses.${s}`),
                })),
                ...(locked
                  ? [
                      {
                        value: "converted",
                        label: t("campaigns:memberStatuses.converted"),
                        disabled: true,
                      },
                    ]
                  : []),
              ]}
              value={m.status}
              disabled={locked || setStatus.isPending}
              allowDeselect={false}
              onChange={(value) => {
                if (value && value !== m.status) {
                  void changeStatus([m.id], value as MemberManualStatus);
                }
              }}
            />
          </Tooltip>
        );
      },
    },
    {
      key: "addedAt",
      header: t("campaigns:members.addedAt"),
      sortField: "addedAt",
      render: (m) => formatDateTime(m.addedAt, timeZone),
    },
    {
      key: "addedBy",
      header: t("campaigns:members.addedBy"),
      render: (m) => m.addedByName ?? "-",
    },
    {
      key: "actions",
      header: "",
      width: 60,
      render: (m) => (
        <RowActions
          editLabel=""
          deleteLabel={t("campaigns:members.removeOne", { name: displayName(m) })}
          onDelete={canWriteCampaigns ? () => setRemoving([m]) : undefined}
        />
      ),
    },
  ];

  const selectedRows = (data?.items ?? []).filter((m) => selection.selected.has(m.id));

  return (
    <>
      <Group gap="sm" align="flex-end" wrap="wrap" mb="md" justify="space-between">
        <Group gap="sm" align="flex-end" wrap="wrap">
          <Select
            aria-label={t("campaigns:members.filterType")}
            placeholder={t("campaigns:members.filterType")}
            w={160}
            clearable
            data={MEMBER_TYPES.map((v) => ({ value: v, label: t(`campaigns:memberTypes.${v}`) }))}
            value={params.filters.memberType || null}
            onChange={(value) => params.setFilter("memberType", value)}
          />
          <MultiSelect
            aria-label={t("campaigns:members.filterStatus")}
            placeholder={t("campaigns:members.filterStatus")}
            w={260}
            clearable
            data={MEMBER_STATUSES.map((v) => ({
              value: v,
              label: t(`campaigns:memberStatuses.${v}`),
            }))}
            value={splitList(params.filters.status)}
            onChange={(values) => params.setFilter("status", values.join(",") || null)}
          />
          {params.hasActiveFilters && (
            <Button variant="subtle" size="sm" onClick={params.clearFilters}>
              {t("common:clearFilters")}
            </Button>
          )}
        </Group>
        {canWriteCampaigns && (
          <Group gap="sm">
            {closed && (
              <Text size="xs" c="dimmed" role="note">
                {t("campaigns:members.closedHint")}
              </Text>
            )}
            <Button
              leftSection={<Plus size={16} />}
              disabled={closed}
              onClick={() => setAdding(true)}
            >
              {t("campaigns:members.add")}
            </Button>
          </Group>
        )}
      </Group>

      {canWriteCampaigns && selectedRows.length > 0 && (
        <Group gap="sm" mb="md" data-testid="member-bulk-bar">
          <Text size="sm" fw={500}>
            {t("campaigns:members.selected", { count: selectedRows.length })}
          </Text>
          <Menu withinPortal>
            <Menu.Target>
              <Button
                variant="default"
                size="xs"
                rightSection={<ChevronDown size={14} />}
                loading={setStatus.isPending}
              >
                {t("campaigns:members.changeStatus")}
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              {/* `converted` cannot be set by hand. */}
              {MEMBER_MANUAL_STATUSES.map((status) => (
                <Menu.Item
                  key={status}
                  onClick={() => void changeStatus(selectedRows.map((m) => m.id), status)}
                >
                  {t(`campaigns:memberStatuses.${status}`)}
                </Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
          <Button
            variant="default"
            color="red"
            size="xs"
            leftSection={<UserMinus size={14} />}
            onClick={() => setRemoving(selectedRows)}
          >
            {t("campaigns:members.remove")}
          </Button>
        </Group>
      )}

      <DataTable
        columns={columns}
        rows={data?.items}
        rowKey={(m) => m.id}
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
        emptyMessage={t("campaigns:members.empty")}
        minWidth={820}
        selection={
          canWriteCampaigns
            ? {
                selected: selection.selected,
                onChange: selection.onChange,
                rowLabel: (m) => t("campaigns:members.selectRow", { name: displayName(m) }),
              }
            : undefined
        }
      />

      {adding && <AddMembersDialog campaign={campaign} onClose={() => setAdding(false)} />}
      <ConfirmDialog
        opened={!!removing}
        title={t("campaigns:members.removeTitle")}
        message={t("campaigns:members.removeMessage", { count: removing?.length ?? 0 })}
        confirmLabel={t("campaigns:members.remove")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmRemove()}
        onClose={() => setRemoving(null)}
      />
    </>
  );
}
