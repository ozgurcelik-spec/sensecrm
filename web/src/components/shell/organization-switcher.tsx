import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { Button, Menu, Text } from "@mantine/core";
import { Building, Check, ChevronDown } from "lucide-react";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useAuthStore } from "@/store/auth.store";

/** Zoho-style organization switcher: lists every organization the user belongs to. */
export function OrganizationSwitcher() {
  const { t } = useTranslation(["common"]);
  const navigate = useNavigate();
  const me = useAuthStore((state) => state.me);
  const switchOrganization = useAuthStore((state) => state.switchOrganization);
  const [switchingTo, setSwitchingTo] = useState<string | null>(null);

  if (!me) return null;
  const current = me.organization;

  async function handleSwitch(id: string, name: string) {
    if (id === current.id) return;
    setSwitchingTo(id);
    try {
      await switchOrganization(id);
      navigate("/app");
      toast({ variant: "success", description: t("common:shell.switched", { name }) });
    } catch (error) {
      toastApiError(error);
    } finally {
      setSwitchingTo(null);
    }
  }

  return (
    <Menu position="bottom-end" width={260}>
      <Menu.Target>
        <Button
          variant="default"
          size="compact-md"
          leftSection={<Building size={16} />}
          rightSection={<ChevronDown size={14} />}
          loading={switchingTo !== null}
          aria-label={t("common:shell.switchOrganization")}
          maw={260}
        >
          <Text size="sm" truncate="end">
            {current.name}
          </Text>
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t("common:shell.organizations")}</Menu.Label>
        {me.organizations.map((org) => (
          <Menu.Item
            key={org.id}
            onClick={() => void handleSwitch(org.id, org.name)}
            rightSection={org.id === current.id ? <Check size={14} /> : null}
          >
            <Text size="sm" fw={org.id === current.id ? 600 : 400} truncate="end">
              {org.name}
            </Text>
            <Text size="xs" c="dimmed">
              {org.slug}
            </Text>
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}
