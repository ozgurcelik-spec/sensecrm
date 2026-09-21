import { useTranslation } from "react-i18next";
import { Avatar, Card, Group, Stack, Text, Title } from "@mantine/core";
import { useAuthStore } from "@/store/auth.store";

function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const letters = parts.length > 1 ? [parts[0], parts[parts.length - 1]] : [parts[0] ?? ""];
  return letters
    .map((p) => p.charAt(0))
    .join("")
    .toLocaleUpperCase();
}

function formatToday(language: string, timeZone: string | undefined): string {
  try {
    return new Intl.DateTimeFormat(language, { dateStyle: "full", timeZone }).format(new Date());
  } catch {
    return new Intl.DateTimeFormat(language, { dateStyle: "full" }).format(new Date());
  }
}

/** Dashboard hero: avatar, greeting, organization/role line and today's date on a brand gradient. */
export function WelcomeCard() {
  const { t, i18n } = useTranslation(["home"]);
  const me = useAuthStore((state) => state.me);
  if (!me) return null;

  return (
    <Card
      padding="xl"
      radius="lg"
      data-testid="welcome-card"
      style={{
        background: "linear-gradient(120deg, #8a6bb8 0%, #4a48b0 50%, #28399e 100%)",
        color: "white",
      }}
    >
      <Group gap="lg" wrap="nowrap" align="center">
        <Avatar size={64} radius="xl" color="brand" variant="white" name={me.user.displayName}>
          {initialsOf(me.user.displayName)}
        </Avatar>
        <Stack gap={4} style={{ minWidth: 0 }}>
          <Title order={2} c="white" lh={1.2}>
            {t("home:welcome", { name: me.user.displayName })}
          </Title>
          <Text c="white" opacity={0.85}>
            {t("home:subtitle", { org: me.organization.name, role: me.role.name })}
          </Text>
          <Text size="sm" c="white" opacity={0.7}>
            {formatToday(i18n.language, me.organization.timeZone)}
          </Text>
        </Stack>
      </Group>
    </Card>
  );
}
