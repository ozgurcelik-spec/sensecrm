import { useTranslation } from "react-i18next";
import { Anchor, Text } from "@mantine/core";
import { navigateApp } from "@/lib/app-navigator";

const PLAN_USAGE_PATH = "/app/settings/plan";

/** Body of the "plan limit reached" toast: the message plus a link to the "Plan ve kullanım" page. */
export function PlanLimitMessage({ message }: { message: string }) {
  const { t } = useTranslation(["subscription"]);
  return (
    <>
      <Text size="sm">{message}</Text>
      <Anchor
        component="button"
        type="button"
        size="sm"
        mt={4}
        onClick={() => navigateApp(PLAN_USAGE_PATH)}
      >
        {t("subscription:errors.planLink")}
      </Anchor>
    </>
  );
}
