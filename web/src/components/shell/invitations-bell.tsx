import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useDisclosure } from "@mantine/hooks";
import { ActionIcon, Button, Group, Indicator, Modal, Paper, Stack, Text } from "@mantine/core";
import { MailPlus } from "lucide-react";
import { useAcceptInvitation, useDeclineInvitation, useInvitations } from "@/hooks/use-invitations";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatDate } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { Invitation } from "@/types";

interface AcceptedOrg {
  id: string;
  name: string;
}

/**
 * Top bar bell with the number of pending organization invitations (polled like the approvals
 * bell) and a dialog to accept or decline them. After accepting, the user can switch straight to
 * the new organization. The dialog stays mounted even when the last invitation is gone.
 */
export function InvitationsBell() {
  const { t } = useTranslation(["security", "common"]);
  const navigate = useNavigate();
  const me = useAuthStore((state) => state.me);
  const switchOrganization = useAuthStore((state) => state.switchOrganization);
  const invitations = useInvitations(!!me);
  const accept = useAcceptInvitation();
  const decline = useDeclineInvitation();
  const [opened, { open, close }] = useDisclosure(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [accepted, setAccepted] = useState<AcceptedOrg[]>([]);
  const [switching, setSwitching] = useState(false);

  const items = invitations.data ?? [];
  const count = items.length;
  const timeZone = me?.organization.timeZone;

  async function handleAccept(invitation: Invitation) {
    setBusyId(invitation.id);
    try {
      await accept.mutateAsync(invitation.id);
      setAccepted((list) => [
        ...list,
        { id: invitation.organizationId, name: invitation.organizationName },
      ]);
      toast({
        variant: "success",
        description: t("security:invitations.accepted", { name: invitation.organizationName }),
      });
    } catch (error) {
      toastApiError(error);
    } finally {
      setBusyId(null);
    }
  }

  async function handleDecline(invitation: Invitation) {
    setBusyId(invitation.id);
    try {
      await decline.mutateAsync(invitation.id);
      toast({ description: t("security:invitations.declined") });
    } catch (error) {
      toastApiError(error);
    } finally {
      setBusyId(null);
    }
  }

  async function handleSwitch(org: AcceptedOrg) {
    setSwitching(true);
    try {
      await switchOrganization(org.id);
      setAccepted([]);
      close();
      navigate("/app");
      toast({ variant: "success", description: t("common:shell.switched", { name: org.name }) });
    } catch (error) {
      toastApiError(error);
    } finally {
      setSwitching(false);
    }
  }

  function handleClose() {
    setAccepted([]);
    close();
  }

  if (!me) return null;

  return (
    <>
      {count > 0 && (
        <Indicator label={count > 99 ? "99+" : count} size={16} color="red">
          <ActionIcon
            variant="subtle"
            color="gray"
            size="lg"
            onClick={open}
            aria-label={t("security:invitations.bellCount", { count })}
          >
            <MailPlus size={18} />
          </ActionIcon>
        </Indicator>
      )}
      <Modal opened={opened} onClose={handleClose} title={t("security:invitations.title")} centered>
        <Stack gap="md">
          {accepted.map((org) => (
            <Paper key={org.id} withBorder p="sm" role="status">
              <Group justify="space-between" wrap="nowrap">
                <Text size="sm">{t("security:invitations.joined", { name: org.name })}</Text>
                <Button size="xs" loading={switching} onClick={() => void handleSwitch(org)}>
                  {t("security:invitations.switchNow")}
                </Button>
              </Group>
            </Paper>
          ))}
          {items.length === 0 && accepted.length === 0 && (
            <Text size="sm" c="dimmed" ta="center">
              {t("security:invitations.empty")}
            </Text>
          )}
          {items.map((invitation) => (
            <Paper key={invitation.id} withBorder p="sm" data-testid="invitation">
              <Stack gap="xs">
                <div>
                  <Text size="sm" fw={600}>
                    {invitation.organizationName}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {t("security:invitations.detail", {
                      role: invitation.roleName,
                      date: formatDate(invitation.invitedAt, timeZone),
                    })}
                  </Text>
                </div>
                <Group gap="xs" justify="flex-end">
                  <Button
                    size="xs"
                    variant="default"
                    disabled={busyId !== null}
                    onClick={() => void handleDecline(invitation)}
                    aria-label={t("security:invitations.declineFor", {
                      name: invitation.organizationName,
                    })}
                  >
                    {t("security:invitations.decline")}
                  </Button>
                  <Button
                    size="xs"
                    loading={busyId === invitation.id}
                    disabled={busyId !== null && busyId !== invitation.id}
                    onClick={() => void handleAccept(invitation)}
                    aria-label={t("security:invitations.acceptFor", {
                      name: invitation.organizationName,
                    })}
                  >
                    {t("security:invitations.accept")}
                  </Button>
                </Group>
              </Stack>
            </Paper>
          ))}
        </Stack>
      </Modal>
    </>
  );
}
