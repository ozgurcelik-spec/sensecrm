import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Card, Group, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { ShieldAlert } from "lucide-react";
import { OrganizationSwitcher } from "@/components/shell/organization-switcher";
import { UserMenu } from "@/components/shell/user-menu";
import { useMeSubscription } from "@/hooks/use-module-enabled";
import { useAuthStore } from "@/store/auth.store";

/**
 * Full-page screen for a tenant whose access level is `none` (blocked suspension, pending deletion,
 * deleted). The app shell renders it instead of any page, so nothing but `/me` is requested; the user can
 * switch organization, sign out or re-check the status.
 */
export function BlockedScreen() {
  const { t } = useTranslation(["subscription", "common"]);
  const subscription = useMeSubscription();
  const organizationName = useAuthStore((state) => state.me?.organization.name);
  const refreshMe = useAuthStore((state) => state.refreshMe);
  const [checking, setChecking] = useState(false);

  const reason = subscription?.status ?? "suspended";

  async function recheck() {
    setChecking(true);
    try {
      await refreshMe();
    } finally {
      setChecking(false);
    }
  }

  return (
    <div className="min-h-screen p-4 md:p-8" data-testid="blocked-screen">
      <Group justify="flex-end" gap="xs" mb="xl">
        <OrganizationSwitcher />
        <UserMenu />
      </Group>
      <div className="mx-auto max-w-xl">
        <Card withBorder padding="xl">
          <Stack align="center" gap="md">
            <ThemeIcon size={56} radius="xl" variant="light" color="red">
              <ShieldAlert size={28} />
            </ThemeIcon>
            <Title order={3} ta="center">
              {t("subscription:blocked.title", { name: organizationName })}
            </Title>
            <Text size="sm" ta="center">
              {t(`subscription:blocked.reason.${reason}`, {
                defaultValue: t("subscription:blocked.reason.suspended"),
              })}
            </Text>
            <Text size="sm" c="dimmed" ta="center">
              {t("subscription:blocked.contact")}
            </Text>
            <Button variant="default" onClick={() => void recheck()} loading={checking}>
              {t("subscription:blocked.recheck")}
            </Button>
          </Stack>
        </Card>
      </div>
    </div>
  );
}
