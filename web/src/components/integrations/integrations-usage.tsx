import { useTranslation } from "react-i18next";
import { UsageBar } from "@/components/subscription/usage-bars";
import type { SubscriptionInfo } from "@/types";

/**
 * "Webhook" and "API anahtarı" bars of the plan page (`usage.webhooks` / `usage.apiKeys` against
 * `limits.maxWebhooks` / `limits.maxApiKeys`). Nothing is drawn while the plan switches the
 * integrations module off or the server does not report the counts.
 */
export function IntegrationsUsageBars({ info }: { info: SubscriptionInfo }) {
  const { t } = useTranslation(["integrations"]);
  if (info.modules.integrations === false) return null;
  const { webhooks, apiKeys } = info.usage;
  return (
    <>
      {webhooks !== undefined && (
        <UsageBar
          testId="usage-webhooks"
          label={t("integrations:plan.webhooks")}
          used={webhooks}
          max={info.limits.maxWebhooks}
        />
      )}
      {apiKeys !== undefined && (
        <UsageBar
          testId="usage-apiKeys"
          label={t("integrations:plan.apiKeys")}
          used={apiKeys}
          max={info.limits.maxApiKeys}
          detail={t("integrations:plan.apiKeysDetail")}
        />
      )}
    </>
  );
}
