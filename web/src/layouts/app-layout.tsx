import { useEffect } from "react";
import { Link, NavLink as RouterNavLink, Outlet, useMatch, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { ArrowLeft } from "lucide-react";
import { useDisclosure } from "@mantine/hooks";
import { AppShell, Burger, Divider, Group, NavLink, ScrollArea, Text } from "@mantine/core";
import { NAV_ITEMS, PLATFORM_ITEMS, SETTINGS_ITEMS, type NavItem } from "@/config/navigation";
import { usePendingApprovalCount } from "@/hooks/use-approvals";
import { useAccessLevel, useModuleEnabled } from "@/hooks/use-module-enabled";
import { useVisibleItems } from "@/hooks/use-nav-visibility";
import { useSubscriptionSync } from "@/hooks/use-subscription";
import { setAppNavigator } from "@/lib/app-navigator";
import { BlockedScreen } from "@/components/subscription/blocked-screen";
import { SubscriptionBanner } from "@/components/subscription/subscription-banner";
import RouteBoundary from "@/components/route-boundary";
import { BackButton } from "@/components/shell/back-button";
import { GlobalSearch } from "@/components/shell/global-search";
import { ApprovalsBell } from "@/components/shell/approvals-bell";
import { NotificationsBell } from "@/components/notifications/notifications-bell";
import { InvitationsBell } from "@/components/shell/invitations-bell";
import { LanguageMenu } from "@/components/shell/language-menu";
import { SettingsLink } from "@/components/shell/settings-link";
import { OrganizationSwitcher } from "@/components/shell/organization-switcher";
import { UserMenu } from "@/components/shell/user-menu";
import classes from "./app-layout.module.css";

function BrandMark() {
  return (
    <svg width="28" height="28" viewBox="0 0 40 40" fill="none" aria-hidden="true">
      <rect x="12" y="4" width="24" height="24" rx="6" fill="var(--mantine-color-navy-7)" />
      <rect x="4" y="12" width="24" height="24" rx="6" fill="var(--mantine-color-brand-6)" />
    </svg>
  );
}

function NavSection({ items, onNavigate }: { items: NavItem[]; onNavigate: () => void }) {
  const { t } = useTranslation(["navigation"]);
  return (
    <>
      {items.map((item) => {
        const Icon = item.icon;
        // React Router sets aria-current="page" on the matching link, which Mantine styles as active.
        return (
          <NavLink
            key={item.key}
            component={RouterNavLink}
            to={item.path}
            end={item.end}
            label={t(`navigation:${item.labelKey}`)}
            leftSection={<Icon size={18} />}
            className={classes.navLink}
            onClick={onNavigate}
          />
        );
      })}
    </>
  );
}

/** Authenticated shell: Zoho-style top bar + module navigation on the left. */
export default function AppLayout() {
  const { t } = useTranslation(["common", "navigation"]);
  const [mobileOpened, { toggle, close }] = useDisclosure(false);
  const navigate = useNavigate();
  const blocked = useAccessLevel() === "none";
  const workflowsOn = useModuleEnabled("workflows");
  // Approvals belong to the workflows module: no request while the plan lacks it or the tenant is blocked.
  const { data: pendingApprovals = 0 } = usePendingApprovalCount(workflowsOn && !blocked);
  const modules = useVisibleItems(NAV_ITEMS, { pendingApprovals: pendingApprovals > 0 });
  const platform = useVisibleItems(PLATFORM_ITEMS);
  const settings = useVisibleItems(SETTINGS_ITEMS);
  // Inside the settings area the left rail lists the settings pages instead of the CRM modules.
  const inSettings = useMatch("/app/settings/*") !== null;
  useSubscriptionSync();
  // Toasts (rendered above the router) navigate through this.
  useEffect(() => {
    setAppNavigator((to) => void navigate(to));
    return () => setAppNavigator(null);
  }, [navigate]);

  // `accessLevel: none` (blocked / deletion pending): nothing but /me is requested until it changes.
  if (blocked) return <BlockedScreen />;

  return (
    <AppShell
      header={{ height: 56 }}
      navbar={{ width: 240, breakpoint: "sm", collapsed: { mobile: !mobileOpened } }}
      padding="lg"
    >
      <AppShell.Header className={classes.header}>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger
              opened={mobileOpened}
              onClick={toggle}
              hiddenFrom="sm"
              size="sm"
              aria-label={t("common:shell.toggleNavigation")}
            />
            <BackButton />
            <Group
              renderRoot={(props) => <Link to="/app" {...props} />}
              gap={8}
              wrap="nowrap"
              style={{ textDecoration: "none" }}
            >
              <BrandMark />
              <Text fw={700} size="lg" c="var(--mantine-color-text)" visibleFrom="xs">
                {t("common:app.name")}
              </Text>
            </Group>
          </Group>
          <div className={classes.search}>
            <GlobalSearch />
          </div>
          <Group gap="xs" wrap="nowrap">
            <NotificationsBell />
            <ApprovalsBell />
            <InvitationsBell />
            <OrganizationSwitcher />
            <LanguageMenu />
            <SettingsLink />
            <UserMenu />
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar className={classes.sidebar}>
        <ScrollArea className="flex-1" p="sm">
          {inSettings ? (
            <>
              <NavLink
                component={Link}
                to="/app"
                label={t("navigation:backToApp")}
                leftSection={<ArrowLeft size={18} />}
                className={classes.navLink}
                onClick={close}
              />
              <Divider my="sm" />
              <nav aria-label={t("navigation:settings")}>
                <NavSection items={settings} onNavigate={close} />
              </nav>
            </>
          ) : (
            <>
              <nav aria-label={t("navigation:modules")}>
                <NavSection items={modules} onNavigate={close} />
              </nav>
              {platform.length > 0 && (
                <nav aria-label={t("navigation:platform")}>
                  <Divider
                    my="sm"
                    labelPosition="left"
                    label={
                      <Text size="xs" fw={600} tt="uppercase" c="dimmed">
                        {t("navigation:platform")}
                      </Text>
                    }
                  />
                  <NavSection items={platform} onNavigate={close} />
                </nav>
              )}
            </>
          )}
        </ScrollArea>
      </AppShell.Navbar>

      <AppShell.Main className={classes.main}>
        <SubscriptionBanner />
        <RouteBoundary>
          <Outlet />
        </RouteBoundary>
      </AppShell.Main>
    </AppShell>
  );
}
