import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Button, Card, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { Hourglass } from "lucide-react";

interface ComingSoonPageProps {
  /** i18n key in the "navigation" namespace for the module name. */
  titleKey: string;
}

/** Placeholder for CRM modules that ship in a later milestone. */
export default function ComingSoonPage({ titleKey }: ComingSoonPageProps) {
  const { t } = useTranslation(["common", "navigation"]);
  return (
    <div className="mx-auto max-w-3xl p-6 md:p-10">
      <Card withBorder padding="xl">
        <Stack align="center" gap="md">
          <ThemeIcon size={48} radius="xl" variant="light" color="gray">
            <Hourglass size={24} />
          </ThemeIcon>
          <Title order={3}>
            {t("common:comingSoon.title", { module: t(`navigation:${titleKey}`) })}
          </Title>
          <Text size="sm" c="dimmed" ta="center">
            {t("common:comingSoon.description")}
          </Text>
          <Button component={Link} to="/app" variant="default">
            {t("common:backHome")}
          </Button>
        </Stack>
      </Card>
    </div>
  );
}
