import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Button, Center, Stack, Text, Title } from "@mantine/core";

export default function NotFoundPage() {
  const { t } = useTranslation(["common"]);
  return (
    <Center mih="60vh" p="xl">
      <Stack align="center" gap="sm">
        <Title order={1} c="dimmed">
          404
        </Title>
        <Title order={3}>{t("common:notFound.title")}</Title>
        <Text size="sm" c="dimmed" ta="center">
          {t("common:notFound.description")}
        </Text>
        <Button component={Link} to="/app" variant="default" mt="sm">
          {t("common:notFound.back")}
        </Button>
      </Stack>
    </Center>
  );
}
