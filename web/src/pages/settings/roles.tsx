import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  ActionIcon,
  Badge,
  Button,
  Card,
  Group,
  Skeleton,
  Table,
  Text,
  Tooltip,
} from "@mantine/core";
import { Eye, Pencil, Plus, Trash2 } from "lucide-react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { usePermission } from "@/hooks/use-permission";
import { RoleFormDialog } from "@/components/roles/role-form-dialog";
import { useDeleteRole, usePermissionCatalog, useRoles } from "@/hooks/use-role-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { PERMISSIONS, type Role } from "@/types";

/** Dialog state: closed, creating, or editing/viewing a role. */
type Editor = { mode: "closed" } | { mode: "create" } | { mode: "edit"; role: Role };

export default function RolesPage() {
  const { t } = useTranslation(["users", "common"]);
  const canManage = usePermission(PERMISSIONS.orgRolesManage);
  const roles = useRoles();
  const catalog = usePermissionCatalog();
  const remove = useDeleteRole();
  const [editor, setEditor] = useState<Editor>({ mode: "closed" });
  const [toDelete, setToDelete] = useState<Role | null>(null);

  async function confirmDelete() {
    if (!toDelete) return;
    try {
      await remove.mutateAsync(toDelete.id);
      toast({ variant: "success", description: t("users:roles.deleted") });
      setToDelete(null);
    } catch (error) {
      toastApiError(error);
    }
  }

  const loadError = roles.error ?? catalog.error;

  return (
    <>
      <PageHeader
        title={t("users:roles.title")}
        description={t("users:roles.description")}
        actions={
          canManage && (
            <Button
              leftSection={<Plus size={16} />}
              onClick={() => setEditor({ mode: "create" })}
              disabled={!catalog.data}
            >
              {t("users:roles.create")}
            </Button>
          )
        }
      />
      {loadError && (
        <LoadError
          error={loadError}
          onRetry={() => {
            void roles.refetch();
            void catalog.refetch();
          }}
        />
      )}
      <Card withBorder padding={0} mt={loadError ? "md" : 0}>
        <Table.ScrollContainer minWidth={640}>
          <Table verticalSpacing="sm" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t("users:roles.name")}</Table.Th>
                <Table.Th>{t("users:roles.permissions")}</Table.Th>
                <Table.Th>{t("users:roles.members")}</Table.Th>
                <Table.Th w={120} ta="right">
                  {t("common:actions")}
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {roles.isLoading &&
                Array.from({ length: 3 }, (_, i) => (
                  <Table.Tr key={i}>
                    <Table.Td colSpan={4}>
                      <Skeleton h={24} />
                    </Table.Td>
                  </Table.Tr>
                ))}
              {roles.data?.map((role) => {
                const editable = canManage && !role.isSystem;
                return (
                  <Table.Tr key={role.id}>
                    <Table.Td>
                      <Group gap="xs">
                        <Text size="sm" fw={500}>
                          {role.name}
                        </Text>
                        {role.isSystem && (
                          <Badge size="xs" variant="light" color="gray">
                            {t("users:roles.system")}
                          </Badge>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">
                        {t("users:roles.permissionCount", { count: role.permissions.length })}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{role.memberCount}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} justify="flex-end" wrap="nowrap">
                        <Tooltip label={editable ? t("common:edit") : t("common:view")}>
                          <ActionIcon
                            variant="subtle"
                            color="gray"
                            aria-label={editable ? t("common:edit") : t("common:view")}
                            onClick={() => setEditor({ mode: "edit", role })}
                            disabled={!catalog.data}
                          >
                            {editable ? <Pencil size={16} /> : <Eye size={16} />}
                          </ActionIcon>
                        </Tooltip>
                        {editable && (
                          <Tooltip label={t("common:delete")}>
                            <ActionIcon
                              variant="subtle"
                              color="red"
                              aria-label={t("common:delete")}
                              onClick={() => setToDelete(role)}
                            >
                              <Trash2 size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Card>

      {catalog.data && (
        <RoleFormDialog
          opened={editor.mode !== "closed"}
          onClose={() => setEditor({ mode: "closed" })}
          role={editor.mode === "edit" ? editor.role : undefined}
          catalog={catalog.data}
          editable={canManage}
        />
      )}
      <ConfirmDialog
        opened={toDelete !== null}
        title={t("users:roles.deleteTitle")}
        message={t("users:roles.deleteConfirm", { name: toDelete?.name ?? "" })}
        confirmLabel={t("common:delete")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void confirmDelete()}
        onClose={() => setToDelete(null)}
      />
    </>
  );
}
