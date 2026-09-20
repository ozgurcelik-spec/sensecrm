import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Button, Card, Group, Stack, Text, Title } from "@mantine/core";
import { Bell } from "lucide-react";

/** Profile page card: the way to "Bildirim tercihleri" (no permission needed, every member has them). */
export function PreferencesLinkCard() {
  const { t } = useTranslation(["notifications"]);
  return (
    <Card withBorder padding="lg" maw={560} component="section" aria-labelledby="notification-prefs-title">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Stack gap={2}>
          <Title order={4} id="notification-prefs-title">
            {t("notifications:preferences.title")}
          </Title>
          <Text size="sm" c="dimmed">
            {t("notifications:preferences.profileHint")}
          </Text>
        </Stack>
        <Button component={Link} to="/app/notifications/preferences" variant="default" leftSection={<Bell size={16} />}>
          {t("notifications:preferences.open")}
        </Button>
      </Group>
    </Card>
  );
}
