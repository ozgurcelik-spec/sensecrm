import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Button, Card, Group, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { PackageX } from "lucide-react";
import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS, type GatedModule } from "@/types";

/** Shown in place of a page whose module the plan does not include ("Bu modül planınıza dahil değil"). */
export default function ModuleDisabled({ module }: { module?: GatedModule }) {
  const { t } = useTranslation(["subscription", "common"]);
  const canSeePlan = usePermission(PERMISSIONS.orgSettingsManage);
  return (
    <div className="mx-auto max-w-xl p-6 md:p-10">
      <Card withBorder padding="xl" data-testid="module-disabled">
        <Stack align="center" gap="md">
          <ThemeIcon size={48} radius="xl" variant="light" color="orange">
            <PackageX size={24} />
          </ThemeIcon>
          <Title order={3}>{t("subscription:moduleDisabled.title")}</Title>
          {module && (
            <Text size="sm" fw={500}>
              {t(`subscription:modules.${module}`)}
            </Text>
          )}
          <Text size="sm" c="dimmed" ta="center">
            {t("subscription:moduleDisabled.description")}
          </Text>
          <Group>
            {canSeePlan && (
              <Button component={Link} to="/app/settings/plan" variant="light">
                {t("subscription:moduleDisabled.viewPlan")}
              </Button>
            )}
            <Button component={Link} to="/app" variant="default">
              {t("common:backHome")}
            </Button>
          </Group>
        </Stack>
      </Card>
    </div>
  );
}
