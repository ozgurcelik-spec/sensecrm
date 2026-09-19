import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useDisclosure } from "@mantine/hooks";
import {
  Badge,
  Button,
  Card,
  Group,
  Select,
  Skeleton,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
} from "@mantine/core";
import { Search, UserPlus } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { usePermission } from "@/hooks/use-permission";
import { AddMemberDialog } from "@/components/users/add-member-dialog";
import { useMembers, useUpdateMember } from "@/hooks/use-organization-queries";
import { useRoles } from "@/hooks/use-role-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDate } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS, type ActiveMember, type PendingMember, type Role } from "@/types";

function PendingMemberRow({ member, timeZone }: { member: PendingMember; timeZone?: string }) {
  const { t } = useTranslation(["users", "security"]);
  return (
    <Table.Tr data-testid="pending-member-row">
      <Table.Td>
        <Text size="sm" fw={500} c={member.displayName ? undefined : "dimmed"}>
          {member.displayName ?? "-"}
        </Text>
      </Table.Td>
      <Table.Td>
        <Text size="sm">{member.email}</Text>
      </Table.Td>
      <Table.Td>
        <Text size="sm">{member.roleName}</Text>
      </Table.Td>
      <Table.Td>
        <Badge color="yellow" variant="light">
          {t("security:members.pendingBadge")}
        </Badge>
      </Table.Td>
      <Table.Td>
        <Text size="sm" c="dimmed">
          {member.invitedAt ? formatDate(member.invitedAt, timeZone) : "-"}
        </Text>
      </Table.Td>
    </Table.Tr>
  );
}

interface MemberRowProps {
  member: ActiveMember;
  roles: Role[];
  canManage: boolean;
  isSelf: boolean;
  timeZone: string | undefined;
}

function MemberRow({ member, roles, canManage, isSelf, timeZone }: MemberRowProps) {
  const { t } = useTranslation(["users"]);
  const update = useUpdateMember();

  async function change(request: { roleId?: string; isActive?: boolean }) {
    try {
      await update.mutateAsync({ userId: member.userId, ...request });
      toast({ variant: "success", description: t("users:users.updated") });
    } catch (error) {
      toastApiError(error);
    }
  }

  return (
    <Table.Tr>
      <Table.Td>
        <Group gap="xs" wrap="nowrap">
          <Text size="sm" fw={500}>
            {member.displayName}
          </Text>
          {isSelf && (
            <Badge size="xs" variant="light">
              {t("users:users.you")}
            </Badge>
          )}
        </Group>
      </Table.Td>
      <Table.Td>
        <Text size="sm">{member.email}</Text>
      </Table.Td>
      <Table.Td miw={200}>
        {canManage ? (
          <Select
            size="xs"
            aria-label={t("users:users.role")}
            data={roles.map((role) => ({ value: role.id, label: role.name }))}
            value={member.roleId}
            onChange={(value) => value && value !== member.roleId && void change({ roleId: value })}
            allowDeselect={false}
            disabled={update.isPending}
          />
        ) : (
          <Text size="sm">{member.roleName}</Text>
        )}
      </Table.Td>
      <Table.Td>
        {canManage ? (
          <Switch
            checked={member.isActive}
            onChange={(event) => void change({ isActive: event.currentTarget.checked })}
            disabled={update.isPending || isSelf}
            label={member.isActive ? t("users:users.active") : t("users:users.inactive")}
            aria-label={member.isActive ? t("users:users.deactivate") : t("users:users.activate")}
          />
        ) : (
          <Badge color={member.isActive ? "green" : "gray"} variant="light">
            {member.isActive ? t("users:users.active") : t("users:users.inactive")}
          </Badge>
        )}
      </Table.Td>
      <Table.Td>
        <Text size="sm" c="dimmed">
          {formatDate(member.joinedAt, timeZone)}
        </Text>
      </Table.Td>
    </Table.Tr>
  );
}

export default function UsersPage() {
  const { t } = useTranslation(["users", "common"]);
  const canManage = usePermission(PERMISSIONS.orgUsersManage);
  const me = useAuthStore((state) => state.me);
  const members = useMembers();
  const roles = useRoles();
  const [opened, { open, close }] = useDisclosure(false);
  const [query, setQuery] = useState("");

  const filtered = useMemo(() => {
    const q = query.trim().toLocaleLowerCase();
    const list = members.data ?? [];
    if (!q) return list;
    return list.filter(
      (m) =>
        (m.displayName ?? "").toLocaleLowerCase().includes(q) ||
        m.email.toLocaleLowerCase().includes(q)
    );
  }, [members.data, query]);

  return (
    <>
      <PageHeader
        title={t("users:users.title")}
        description={t("users:users.description")}
        actions={
          canManage && (
            <Button leftSection={<UserPlus size={16} />} onClick={open} disabled={!roles.data}>
              {t("users:users.add")}
            </Button>
          )
        }
      />
      <Stack gap="md">
        <TextInput
          leftSection={<Search size={16} />}
          placeholder={t("users:users.search")}
          value={query}
          onChange={(event) => setQuery(event.currentTarget.value)}
          maw={360}
        />
        {members.error && (
          <LoadError error={members.error} onRetry={() => void members.refetch()} />
        )}
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={760}>
            <Table verticalSpacing="sm" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("users:users.name")}</Table.Th>
                  <Table.Th>{t("users:users.email")}</Table.Th>
                  <Table.Th>{t("users:users.role")}</Table.Th>
                  <Table.Th>{t("users:users.status")}</Table.Th>
                  <Table.Th>{t("users:users.joinedAt")}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {members.isLoading &&
                  Array.from({ length: 3 }, (_, i) => (
                    <Table.Tr key={i}>
                      <Table.Td colSpan={5}>
                        <Skeleton h={24} />
                      </Table.Td>
                    </Table.Tr>
                  ))}
                {filtered.map((member) =>
                  member.status === "pending" ? (
                    <PendingMemberRow
                      key={`pending:${member.email}`}
                      member={member}
                      timeZone={me?.organization.timeZone}
                    />
                  ) : (
                    <MemberRow
                      key={member.userId}
                      member={member}
                      roles={roles.data ?? []}
                      canManage={canManage && !!roles.data}
                      isSelf={member.userId === me?.user.id}
                      timeZone={me?.organization.timeZone}
                    />
                  )
                )}
                {members.data && filtered.length === 0 && (
                  <Table.Tr>
                    <Table.Td colSpan={5}>
                      <Text size="sm" c="dimmed" ta="center" py="md">
                        {t("common:noData")}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                )}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      </Stack>
      {canManage && roles.data && (
        <AddMemberDialog opened={opened} onClose={close} roles={roles.data} />
      )}
    </>
  );
}
