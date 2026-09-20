import { useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Skeleton, Tabs } from "@mantine/core";
import { Info } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { DeliveryLogTable } from "@/components/notifications/delivery-log-table";
import { RecipientIssuesTable } from "@/components/notifications/recipient-issues-table";
import { TenantSettingsForm } from "@/components/notifications/tenant-settings-form";
import { PageHeader } from "@/components/page-header";
import { useCanChangeNotificationSettings } from "@/hooks/use-notification-access";
import { useNotificationSettings } from "@/hooks/use-notifications";
import { useAccessLevel } from "@/hooks/use-module-enabled";

const TABS = ["settings", "deliveries", "issues"] as const;
type SettingsTab = (typeof TABS)[number];

function SettingsTabPanel({ canWrite }: { canWrite: boolean }) {
  const { data, isLoading, error, refetch } = useNotificationSettings();
  if (error) return <LoadError error={error} onRetry={() => void refetch()} />;
  if (isLoading || !data) return <Skeleton h={260} />;
  return <TenantSettingsForm settings={data} canWrite={canWrite} />;
}

/**
 * Settings > Bildirimler (`org.notifications.manage`): channel switches and sender, the delivery
 * log and the recipient issues. The tab lives in `?tab=`; every tab keeps its own filters in the URL.
 */
export default function NotificationSettingsPage() {
  const { t } = useTranslation(["notifications"]);
  const [searchParams, setSearchParams] = useSearchParams();
  const requested = searchParams.get("tab");
  const tab: SettingsTab = (TABS as readonly string[]).includes(requested ?? "") ? (requested as SettingsTab) : "settings";
  const canWrite = useCanChangeNotificationSettings();
  const readOnly = useAccessLevel() === "readOnly";

  return (
    <>
      <PageHeader
        title={t("notifications:admin.title")}
        description={t("notifications:admin.description")}
      />
      {readOnly && (
        <Alert color="blue" variant="light" icon={<Info size={16} />} mb="md" data-testid="admin-readonly">
          {t("notifications:admin.readOnly")}
        </Alert>
      )}
      <Tabs
        value={tab}
        // Tabs have different filters: switching starts from a clean query string.
        onChange={(value) => setSearchParams(value && value !== "settings" ? { tab: value } : {}, { replace: true })}
        keepMounted={false}
      >
        <Tabs.List mb="md" aria-label={t("notifications:admin.tabs.label")}>
          <Tabs.Tab value="settings">{t("notifications:admin.tabs.settings")}</Tabs.Tab>
          <Tabs.Tab value="deliveries">{t("notifications:admin.tabs.deliveries")}</Tabs.Tab>
          <Tabs.Tab value="issues">{t("notifications:admin.tabs.issues")}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="settings">
          <SettingsTabPanel canWrite={canWrite} />
        </Tabs.Panel>
        <Tabs.Panel value="deliveries">
          <DeliveryLogTable />
        </Tabs.Panel>
        <Tabs.Panel value="issues">
          <RecipientIssuesTable />
        </Tabs.Panel>
      </Tabs>
    </>
  );
}
