import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Anchor, Button, Card, Group, Progress, Stack, Text, ThemeIcon } from "@mantine/core";
import { Check, Circle } from "lucide-react";
import { useAccessLevel, useModuleEnabled } from "@/hooks/use-module-enabled";
import { useDismissOnboarding, useOnboarding } from "@/hooks/use-onboarding";
import { usePermission } from "@/hooks/use-permission";
import { toastApiError } from "@/hooks/use-toast";
import { PERMISSIONS } from "@/types";

/** Step key -> where the checklist item leads. Unknown keys from a newer server are ignored. */
const STEP_TARGETS: Record<string, string> = {
  profile: "/app/settings/organization",
  invite_user: "/app/settings/users",
  create_lead: "/app/leads",
  create_workflow_rule: "/app/settings/workflows",
};

/**
 * First-run checklist on the home page: progress bar and the four setup steps. Shown to
 * `org.settings.manage` while the checklist is neither dismissed nor complete; the workflow step
 * disappears when the plan has no workflows. Loading and errors render nothing, so the home page
 * never breaks because of it.
 */
export function OnboardingCard() {
  const { t } = useTranslation(["subscription"]);
  const canManage = usePermission(PERMISSIONS.orgSettingsManage);
  const fullAccess = useAccessLevel() === "full";
  const workflowsOn = useModuleEnabled("workflows");
  const { data } = useOnboarding(canManage && fullAccess);
  const dismiss = useDismissOnboarding();

  if (!canManage || !fullAccess || !data || data.dismissed) return null;

  const items = data.items.filter(
    (item) => item.key in STEP_TARGETS && (item.key !== "create_workflow_rule" || workflowsOn)
  );
  const done = items.filter((item) => item.done).length;
  if (items.length === 0 || done >= items.length) return null;

  async function close() {
    try {
      await dismiss.mutateAsync();
    } catch (error) {
      toastApiError(error);
    }
  }

  return (
    <Card withBorder padding="lg" data-testid="onboarding-card">
      <Group justify="space-between" align="flex-start" mb="xs" wrap="nowrap">
        <Stack gap={2}>
          <Text fw={600}>{t("subscription:onboarding.title")}</Text>
          <Text size="sm" c="dimmed">
            {t("subscription:onboarding.progress", { done, total: items.length })}
          </Text>
        </Stack>
        <Button
          variant="subtle"
          color="gray"
          size="xs"
          onClick={() => void close()}
          loading={dismiss.isPending}
        >
          {t("subscription:onboarding.dismiss")}
        </Button>
      </Group>
      <Progress
        value={(done / items.length) * 100}
        size="sm"
        radius="xl"
        mb="md"
        aria-label={t("subscription:onboarding.title")}
      />
      <Stack gap="xs">
        {items.map((item) => (
          <Group gap="sm" wrap="nowrap" key={item.key} data-testid={`onboarding-${item.key}`} data-done={item.done}>
            <ThemeIcon
              size={22}
              radius="xl"
              variant={item.done ? "filled" : "light"}
              color={item.done ? "green" : "gray"}
            >
              {item.done ? <Check size={14} /> : <Circle size={10} />}
            </ThemeIcon>
            {item.done ? (
              <Text size="sm" c="dimmed" td="line-through">
                {t(`subscription:onboarding.steps.${item.key}`)}
              </Text>
            ) : (
              <Anchor component={Link} to={STEP_TARGETS[item.key] as string} size="sm">
                {t(`subscription:onboarding.steps.${item.key}`)}
              </Anchor>
            )}
          </Group>
        ))}
      </Stack>
    </Card>
  );
}
