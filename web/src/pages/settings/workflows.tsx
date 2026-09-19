import { useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Tabs } from "@mantine/core";
import { PageHeader } from "@/components/page-header";
import { ExecutionsTab } from "@/components/workflows/executions-tab";
import { RulesTab } from "@/components/workflows/rules-tab";

const TABS = ["rules", "executions"] as const;
type WorkflowTab = (typeof TABS)[number];

/** Settings > Workflows (`org.workflows.manage`): rules and executions; the tab is kept in `?tab=`. */
export default function WorkflowsPage() {
  const { t } = useTranslation(["workflows"]);
  const [searchParams, setSearchParams] = useSearchParams();
  const requested = searchParams.get("tab");
  const tab: WorkflowTab = TABS.find((value) => value === requested) ?? "rules";

  return (
    <>
      <PageHeader title={t("workflows:title")} description={t("workflows:description")} />
      <Tabs
        value={tab}
        // Each tab has its own filters: switching starts from a clean query string.
        onChange={(value) =>
          setSearchParams(value && value !== "rules" ? { tab: value } : {}, { replace: true })
        }
        keepMounted={false}
      >
        <Tabs.List mb="md" aria-label={t("workflows:tabs.label")}>
          <Tabs.Tab value="rules">{t("workflows:tabs.rules")}</Tabs.Tab>
          <Tabs.Tab value="executions">{t("workflows:tabs.executions")}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="rules">
          <RulesTab />
        </Tabs.Panel>
        <Tabs.Panel value="executions">
          <ExecutionsTab />
        </Tabs.Panel>
      </Tabs>
    </>
  );
}
