import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Card, Center, Group, Stack, Text, Title } from "@mantine/core";
import { LanguageMenu } from "@/components/shell/language-menu";

interface AuthLayoutProps {
  title: string;
  subtitle: string;
  children: ReactNode;
  footer?: ReactNode;
}

/** Centered card shared by the login and sign-up screens. */
export default function AuthLayout({ title, subtitle, children, footer }: AuthLayoutProps) {
  const { t } = useTranslation(["common"]);
  return (
    <div className="min-h-screen" style={{ background: "var(--mantine-color-body)" }}>
      <Group justify="flex-end" p="md">
        <LanguageMenu />
      </Group>
      <Center px="md" pb="xl">
        <Stack w="100%" maw={440} gap="lg">
          <Stack gap={4} align="center">
            <Text fw={800} size="xl" c="brand.7">
              {t("common:app.name")}
            </Text>
            <Text size="sm" c="dimmed" ta="center">
              {t("common:app.tagline")}
            </Text>
          </Stack>
          <Card withBorder shadow="sm" padding="xl" radius="lg">
            <Stack gap={4} mb="lg">
              <Title order={3}>{title}</Title>
              <Text size="sm" c="dimmed">
                {subtitle}
              </Text>
            </Stack>
            {children}
          </Card>
          {footer && (
            <Text size="sm" ta="center" c="dimmed">
              {footer}
            </Text>
          )}
        </Stack>
      </Center>
    </div>
  );
}
