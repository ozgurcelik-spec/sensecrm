import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import {
  Badge,
  Card,
  Group,
  SimpleGrid,
  Stack,
  Table,
  Text,
  ThemeIcon,
  Title,
} from "@mantine/core";
import { Building2 } from "lucide-react";
import { CRM_MODULE_ITEMS, SETTINGS_ITEMS } from "@/config/navigation";
import { useVisibleItems } from "@/hooks/use-nav-visibility";
import { useAuthStore } from "@/store/auth.store";

export default function HomePage() {
  const { t } = useTranslation(["home", "common", "navigation"]);
  const me = useAuthStore((state) => state.me);
  const modules = useVisibleItems(CRM_MODULE_ITEMS);
  const settings = useVisibleItems(SETTINGS_ITEMS);

  if (!me) return null;
  const org = me.organization;
  const quickLinks = [...modules, ...settings];

  return (
    <Stack gap="lg" maw={1100}>
      <Stack gap={4}>
        <Title order={2}>{t("home:welcome", { name: me.user.displayName })}</Title>
        <Text c="dimmed">{t("home:subtitle", { org: org.name, role: me.role.name })}</Text>
      </Stack>

      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
        <Card withBorder padding="lg">
          <Group gap="sm" mb="md">
            <ThemeIcon variant="light" size="lg">
              <Building2 size={18} />
            </ThemeIcon>
            <Text fw={600}>{t("home:organizationCard")}</Text>
          </Group>
          <Table variant="vertical" withTableBorder={false} layout="fixed">
            <Table.Tbody>
              <Table.Tr>
                <Table.Th w={160}>{t("home:name")}</Table.Th>
                <Table.Td>{org.name}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t("home:slug")}</Table.Th>
                <Table.Td>{org.slug}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t("home:defaultLocale")}</Table.Th>
                <Table.Td>{t(`common:languages.${org.defaultLocale}`)}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t("home:timeZone")}</Table.Th>
                <Table.Td>{org.timeZone}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t("home:role")}</Table.Th>
                <Table.Td>
                  <Group gap="xs">
                    <Text size="sm">{me.role.name}</Text>
                    <Badge variant="light" size="sm">
                      {t("home:permissionCount", { count: me.permissions.length })}
                    </Badge>
                  </Group>
                </Table.Td>
              </Table.Tr>
            </Table.Tbody>
          </Table>
        </Card>

        <Card withBorder padding="lg">
          <Text fw={600} mb="md">
            {t("home:quickLinks")}
          </Text>
          <SimpleGrid cols={2} spacing="sm">
            {quickLinks.map((item) => {
              const Icon = item.icon;
              return (
                <Card
                  key={item.key}
                  component={Link}
                  to={item.path}
                  withBorder
                  padding="sm"
                  style={{ textDecoration: "none" }}
                >
                  <Group gap="sm" wrap="nowrap">
                    <Icon size={18} color="var(--mantine-color-brand-6)" />
                    <Text size="sm" fw={500}>
                      {t(`navigation:${item.labelKey}`)}
                    </Text>
                  </Group>
                </Card>
              );
            })}
          </SimpleGrid>
          <Text size="xs" c="dimmed" mt="md">
            {t("home:modulesSoon")}
          </Text>
        </Card>
      </SimpleGrid>
    </Stack>
  );
}
