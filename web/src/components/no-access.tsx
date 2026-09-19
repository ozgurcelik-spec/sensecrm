import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Button, Card, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { Lock } from "lucide-react";

/** Shown in place of a page the user lacks the permission for (deep link / stale bookmark). */
export default function NoAccess() {
  const { t } = useTranslation(["common"]);
  return (
    <div className="mx-auto max-w-xl p-6 md:p-10">
      <Card withBorder padding="xl">
        <Stack align="center" gap="md">
          <ThemeIcon size={48} radius="xl" variant="light" color="red">
            <Lock size={24} />
          </ThemeIcon>
          <Title order={3}>{t("common:forbidden.title")}</Title>
          <Text size="sm" c="dimmed" ta="center">
            {t("common:forbidden.description")}
          </Text>
          <Button component={Link} to="/app" variant="default">
            {t("common:backHome")}
          </Button>
        </Stack>
      </Card>
    </div>
  );
}
