import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon } from "@mantine/core";
import { Settings } from "lucide-react";
import { SETTINGS_ITEMS } from "@/config/navigation";
import { useVisibleItems } from "@/hooks/use-nav-visibility";

/** Header gear: opens the settings area; hidden when the user may open no settings page. */
export function SettingsLink() {
  const { t } = useTranslation(["navigation"]);
  const items = useVisibleItems(SETTINGS_ITEMS);
  if (items.length === 0) return null;

  return (
    <ActionIcon
      component={Link}
      to="/app/settings"
      variant="subtle"
      color="gray"
      size="lg"
      aria-label={t("navigation:settings")}
    >
      <Settings size={20} />
    </ActionIcon>
  );
}
