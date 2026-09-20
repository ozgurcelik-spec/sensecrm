import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router";
import { Avatar, Menu, Text, UnstyledButton } from "@mantine/core";
import { CircleUser, LogOut, Moon, Sun } from "lucide-react";
import { useAuthStore } from "@/store/auth.store";
import { useUIStore } from "@/store/ui.store";
import { ThemeSwitcher } from "./theme-switcher";

function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? "")
    .join("");
}

/** Avatar menu: profile, theme, sign out. */
export function UserMenu() {
  const { t } = useTranslation(["common"]);
  const navigate = useNavigate();
  const me = useAuthStore((state) => state.me);
  const logout = useAuthStore((state) => state.logout);
  const theme = useUIStore((state) => state.theme);
  const setTheme = useUIStore((state) => state.setTheme);

  if (!me) return null;
  const dark = theme === "dark";

  async function handleLogout() {
    await logout();
    navigate("/login", { replace: true });
  }

  return (
    <Menu position="bottom-end" width={240}>
      <Menu.Target>
        <UnstyledButton aria-label={me.user.displayName}>
          <Avatar color="brand" radius="xl" size={34}>
            {initials(me.user.displayName)}
          </Avatar>
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>
          <Text size="sm" fw={600} c="var(--mantine-color-text)" truncate="end">
            {me.user.displayName}
          </Text>
          <Text size="xs" c="dimmed" truncate="end">
            {me.user.email}
          </Text>
          <Text size="xs" c="dimmed" truncate="end">
            {me.role.name}
          </Text>
        </Menu.Label>
        <Menu.Divider />
        <Menu.Item component={Link} to="/app/account" leftSection={<CircleUser size={16} />}>
          {t("common:shell.profile")}
        </Menu.Item>
        <Menu.Item
          leftSection={dark ? <Sun size={16} /> : <Moon size={16} />}
          onClick={() => setTheme(dark ? "light" : "dark")}
        >
          {dark ? t("common:themeLight") : t("common:themeDark")}
        </Menu.Item>
        <Menu.Divider />
        <ThemeSwitcher />
        <Menu.Divider />
        <Menu.Item
          color="red"
          leftSection={<LogOut size={16} />}
          onClick={() => void handleLogout()}
        >
          {t("common:shell.logout")}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
