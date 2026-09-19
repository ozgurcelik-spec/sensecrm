import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Card, Group, Skeleton, Switch, Table, Text } from "@mantine/core";
import { Plus } from "lucide-react";
import { RowActions } from "@/components/crm/list-page-frame";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { useRoles } from "@/hooks/use-role-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useDeleteWorkflowRule, useSetRuleEnabled, useWorkflowRules } from "@/hooks/use-workflows";
import { formatNumber } from "@/lib/format";
import type { DealApprovalParams, LeadAssignmentParams, Role, WorkflowRule } from "@/types";
import { KindBadge } from "./badges";
import { RuleFormDialog } from "./rule-form-dialog";

function useParamsSummary(roles: Role[] | undefined) {
  const { t } = useTranslation(["workflows", "crm"]);
  const roleName = (id: string) =>
    roles?.find((role) => role.id === id)?.name ?? t("workflows:rules.summary.unknownRole");
  return (rule: WorkflowRule): string => {
    if (rule.kind === "leadAssignment") {
      const params = rule.params as LeadAssignmentParams;
      const sources = params.sources?.length
        ? params.sources.map((s) => t(`workflows:sources.${s}`, { defaultValue: s })).join(", ")
        : t("workflows:rules.summary.allSources");
      return t("workflows:rules.summary.leadAssignment", {
        sources,
        role: roleName(params.assigneeRoleId),
        hours: params.followUpHours,
      });
    }
    const params = rule.params as DealApprovalParams;
    return t("workflows:rules.summary.dealApproval", {
      amount: formatNumber(params.minAmount),
      role: roleName(params.approverRoleId),
    });
  };
}

/** The "Kurallar" tab: rule table with the enabled switch, create / edit dialog and delete confirmation. */
export function RulesTab() {
  const { t } = useTranslation(["workflows", "common"]);
  const rules = useWorkflowRules();
  const roles = useRoles();
  const setEnabled = useSetRuleEnabled();
  const remove = useDeleteWorkflowRule();
  const summarize = useParamsSummary(roles.data);
  const [editing, setEditing] = useState<WorkflowRule | "new" | null>(null);
  const [deleting, setDeleting] = useState<WorkflowRule | null>(null);

  async function confirmDelete() {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast({ variant: "success", description: t("workflows:rules.deleted") });
    } catch (error) {
      toastApiError(error);
    }
    setDeleting(null);
  }

  return (
    <>
      <Group justify="flex-end" mb="md">
        <Button leftSection={<Plus size={16} />} onClick={() => setEditing("new")}>
          {t("workflows:rules.new")}
        </Button>
      </Group>

      {rules.error && <LoadError error={rules.error} onRetry={() => void rules.refetch()} />}
      {rules.isLoading && <Skeleton h={160} />}
      {rules.data && (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={760}>
            <Table verticalSpacing="sm" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("workflows:rules.column.name")}</Table.Th>
                  <Table.Th>{t("workflows:rules.column.kind")}</Table.Th>
                  <Table.Th>{t("workflows:rules.column.params")}</Table.Th>
                  <Table.Th w={110}>{t("workflows:rules.column.enabled")}</Table.Th>
                  <Table.Th w={100} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rules.data.map((rule) => (
                  <Table.Tr key={rule.id}>
                    <Table.Td>
                      <Text size="sm" fw={500}>
                        {rule.name}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <KindBadge kind={rule.kind} />
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">
                        {summarize(rule)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Switch
                        aria-label={t("workflows:rules.enableNamed", { name: rule.name })}
                        checked={rule.isEnabled}
                        disabled={setEnabled.isPending && setEnabled.variables?.id === rule.id}
                        onChange={(event) =>
                          setEnabled.mutate({ id: rule.id, enabled: event.currentTarget.checked })
                        }
                      />
                    </Table.Td>
                    <Table.Td>
                      <RowActions
                        editLabel={t("common:edit")}
                        deleteLabel={t("common:delete")}
                        onEdit={() => setEditing(rule)}
                        onDelete={() => setDeleting(rule)}
                      />
                    </Table.Td>
                  </Table.Tr>
                ))}
                {rules.data.length === 0 && (
                  <Table.Tr>
                    <Table.Td colSpan={5}>
                      <Text size="sm" c="dimmed" ta="center" py="md">
                        {t("workflows:rules.empty")}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                )}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}

      {editing && (
        <RuleFormDialog
          rule={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}
      <ConfirmDialog
        opened={!!deleting}
        title={t("workflows:rules.deleteTitle")}
        message={t("workflows:rules.deleteMessage", { name: deleting?.name })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setDeleting(null)}
      />
    </>
  );
}
