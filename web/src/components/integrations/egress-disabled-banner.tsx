import { useTranslation } from "react-i18next";
import { Alert, Stack } from "@mantine/core";
import { Info, ShieldAlert } from "lucide-react";
import type { IntegrationsStatus } from "@/types";

/**
 * Deployment notes from `GET /integrations/status`: a persistent banner while this installation
 * sends no webhooks (subscriptions can still be prepared) and a note when only allow-listed hosts
 * are accepted. Renders nothing while the status is unknown.
 */
export function EgressDisabledBanner({ status }: { status: IntegrationsStatus | undefined }) {
  const { t } = useTranslation(["integrations"]);
  if (!status) return null;
  return (
    <Stack gap="xs" mb="md">
      {!status.webhooksEnabled && (
        <Alert
          color="orange"
          variant="light"
          icon={<ShieldAlert size={16} />}
          title={t("integrations:egress.disabledTitle")}
          data-testid="egress-disabled"
        >
          {t("integrations:egress.disabledBody")}
        </Alert>
      )}
      {status.restrictedHosts && (
        <Alert color="blue" variant="light" icon={<Info size={16} />} data-testid="restricted-hosts">
          {t("integrations:egress.restrictedHosts")}
        </Alert>
      )}
    </Stack>
  );
}
