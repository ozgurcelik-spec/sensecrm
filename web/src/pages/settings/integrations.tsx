import { useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Tabs } from "@mantine/core";
import { Info } from "lucide-react";
import { ApiKeysTab } from "@/components/integrations/api-keys-tab";
import { DeliveryLog } from "@/components/integrations/delivery-log";
import { DeveloperGuide } from "@/components/integrations/developer-guide";
import { WebhooksTab } from "@/components/integrations/webhooks-tab";
import { PageHeader } from "@/components/page-header";
import { useAccessLevel } from "@/hooks/use-module-enabled";

const TABS = ["webhooks", "deliveries", "apiKeys", "developer"] as const;
type IntegrationsTab = (typeof TABS)[number];

/**
 * Settings > Entegrasyonlar (`org.integrations.manage`, plan module `integrations`): webhook
 * subscriptions, the delivery log, API keys and the developer guide. The tab lives in `?tab=`; every
 * tab keeps its own filters in the URL. In read-only mode (`accessLevel: readOnly`) the lists stay
 * readable and the server answers writes with `tenant.suspended` (shown as a toast).
 */
export default function IntegrationsPage() {
  const { t } = useTranslation(["integrations"]);
  const [searchParams, setSearchParams] = useSearchParams();
  const requested = searchParams.get("tab");
  const tab: IntegrationsTab = (TABS as readonly string[]).includes(requested ?? "")
    ? (requested as IntegrationsTab)
    : "webhooks";
  const readOnly = useAccessLevel() === "readOnly";

  return (
    <>
      <PageHeader title={t("integrations:title")} description={t("integrations:description")} />
      {readOnly && (
        <Alert color="blue" variant="light" icon={<Info size={16} />} mb="md" data-testid="integrations-readonly">
          {t("integrations:readOnly")}
        </Alert>
      )}
      <Tabs
        value={tab}
        // Tabs have different filters: switching starts from a clean query string.
        onChange={(value) => setSearchParams(value && value !== "webhooks" ? { tab: value } : {}, { replace: true })}
        keepMounted={false}
      >
        <Tabs.List mb="md" aria-label={t("integrations:tabs.label")}>
          <Tabs.Tab value="webhooks">{t("integrations:tabs.webhooks")}</Tabs.Tab>
          <Tabs.Tab value="deliveries">{t("integrations:tabs.deliveries")}</Tabs.Tab>
          <Tabs.Tab value="apiKeys">{t("integrations:tabs.apiKeys")}</Tabs.Tab>
          <Tabs.Tab value="developer">{t("integrations:tabs.developer")}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="webhooks">
          <WebhooksTab />
        </Tabs.Panel>
        <Tabs.Panel value="deliveries">
          <DeliveryLog />
        </Tabs.Panel>
        <Tabs.Panel value="apiKeys">
          <ApiKeysTab />
        </Tabs.Panel>
        <Tabs.Panel value="developer">
          <DeveloperGuide />
        </Tabs.Panel>
      </Tabs>
    </>
  );
}
