import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Anchor } from "@mantine/core";
import { Info, TriangleAlert } from "lucide-react";
import { useMeSubscription } from "@/hooks/use-module-enabled";
import { usePermission } from "@/hooks/use-permission";
import { PERMISSIONS } from "@/types";

/** Trial banners start this many days before the end; the last two days use the warning color. */
const TRIAL_BANNER_DAYS = 7;
const TRIAL_WARNING_DAYS = 2;

/**
 * Global banner above every page, from `GET /me` `subscription`: trial ending soon, trial over
 * (read-only), suspended (read-only). A blocked tenant (`accessLevel: none`) gets the full-page
 * blocked screen instead, so nothing is rendered here for it.
 */
export function SubscriptionBanner() {
  const { t } = useTranslation(["subscription"]);
  const subscription = useMeSubscription();
  const canSeePlan = usePermission(PERMISSIONS.orgSettingsManage);
  if (!subscription || subscription.accessLevel === "none") return null;

  let message: string | null = null;
  let color = "yellow";
  let icon = <Info size={16} />;

  if (subscription.status === "trial") {
    const days = subscription.trialDaysLeft;
    if (days === undefined || days > TRIAL_BANNER_DAYS) return null;
    message =
      days <= 0 ? t("subscription:banner.trialLastDay") : t("subscription:banner.trialEnding", { count: days });
    color = days <= TRIAL_WARNING_DAYS ? "orange" : "blue";
    if (days <= TRIAL_WARNING_DAYS) icon = <TriangleAlert size={16} />;
  } else if (subscription.status === "trial_expired") {
    message = t("subscription:banner.trialExpired");
    color = "red";
    icon = <TriangleAlert size={16} />;
  } else if (subscription.status === "suspended") {
    message = t("subscription:banner.suspended");
    color = "red";
    icon = <TriangleAlert size={16} />;
  }
  if (!message) return null;

  return (
    <Alert
      color={color}
      variant="light"
      icon={icon}
      mb="md"
      role="status"
      data-testid="subscription-banner"
      data-status={subscription.status}
    >
      {message}{" "}
      {canSeePlan && (
        <Anchor component={Link} to="/app/settings/plan" size="sm">
          {t("subscription:banner.viewPlan")}
        </Anchor>
      )}
    </Alert>
  );
}
