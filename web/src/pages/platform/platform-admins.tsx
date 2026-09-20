import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Badge, Button, Card, Checkbox, Skeleton, Table, Text } from "@mantine/core";
import { UserMinus } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { StepUpDialog } from "@/components/platform/step-up-dialog";
import { usePlatformAdmins, useRevokePlatformAdmin } from "@/hooks/use-platform";
import { toast } from "@/hooks/use-toast";
import { formatDateTime } from "@/lib/dates";
import type { PlatformAdmin } from "@/types";

function RevokeDialog({ admin, onClose }: { admin: PlatformAdmin; onClose: () => void }) {
  const { t } = useTranslation(["platform"]);
  const revoke = useRevokePlatformAdmin();
  const [deactivate, setDeactivate] = useState(false);
  return (
    <StepUpDialog
      title={t("platform:admins.revokeTitle")}
      message={t("platform:admins.revokeMessage", { name: admin.displayName || admin.email })}
      confirmLabel={t("platform:admins.revoke")}
      isPending={revoke.isPending}
      onConfirm={async (currentPassword) => {
        await revoke.mutateAsync({
          userId: admin.userId,
          currentPassword,
          ...(deactivate ? { deactivate: true } : {}),
        });
        toast({ variant: "success", description: t("platform:admins.revoked", { email: admin.email }) });
        onClose();
      }}
      onClose={onClose}
    >
      <Checkbox
        label={t("platform:admins.deactivate")}
        description={t("platform:admins.deactivateHint")}
        checked={deactivate}
        onChange={(event) => setDeactivate(event.currentTarget.checked)}
      />
    </StepUpDialog>
  );
}

/** Minimal list of the platform administrators; revoking one needs the caller's own password. */
export default function PlatformAdminsPage() {
  const { t } = useTranslation(["platform"]);
  const { data, isLoading, error, refetch } = usePlatformAdmins();
  const [target, setTarget] = useState<PlatformAdmin | null>(null);

  return (
    <>
      <PageHeader title={t("platform:admins.title")} description={t("platform:admins.description")} />
      {error ? (
        <LoadError error={error} onRetry={() => void refetch()} />
      ) : isLoading ? (
        <Skeleton h={160} />
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={640}>
            <Table verticalSpacing="sm" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t("platform:admins.columns.name")}</Table.Th>
                  <Table.Th>{t("platform:admins.columns.email")}</Table.Th>
                  <Table.Th>{t("platform:admins.columns.status")}</Table.Th>
                  <Table.Th>{t("platform:admins.columns.lastLogin")}</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {(data ?? []).map((admin) => (
                  <Table.Tr key={admin.userId} data-testid="admin-row">
                    <Table.Td>{admin.displayName}</Table.Td>
                    <Table.Td>{admin.email}</Table.Td>
                    <Table.Td>
                      <Badge variant="light" color={admin.isActive ? "green" : "gray"}>
                        {admin.isActive ? t("platform:admins.active") : t("platform:admins.inactive")}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      {admin.lastLoginAt ? formatDateTime(admin.lastLoginAt) : t("platform:admins.never")}
                    </Table.Td>
                    <Table.Td ta="right">
                      <Button
                        color="red"
                        variant="light"
                        size="xs"
                        leftSection={<UserMinus size={14} />}
                        aria-label={t("platform:admins.revokeOf", { email: admin.email })}
                        onClick={() => setTarget(admin)}
                      >
                        {t("platform:admins.revoke")}
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                ))}
                {(data ?? []).length === 0 && (
                  <Table.Tr>
                    <Table.Td colSpan={5}>
                      <Text c="dimmed" ta="center" size="sm">
                        {t("platform:admins.empty")}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                )}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}
      {target && <RevokeDialog admin={target} onClose={() => setTarget(null)} />}
    </>
  );
}
