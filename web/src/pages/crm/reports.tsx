import { useMemo } from "react";
import { useSearchParams } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Group, SegmentedControl, Stack, Tabs, TextInput } from "@mantine/core";
import {
  ActivitiesReport,
  ByOwnerReport,
  FunnelReport,
  LeadSourcesReport,
  WonLostReport,
} from "@/components/reports/report-tabs";
import { PageHeader } from "@/components/page-header";
import {
  DEFAULT_RANGE_PRESET,
  RANGE_PRESETS,
  isRangePreset,
  resolveRange,
  type RangePreset,
} from "@/lib/report-range";
import { useAuthStore } from "@/store/auth.store";
import type { WonLostGroupBy } from "@/types";

const REPORT_TABS = ["funnel", "wonLost", "leadSources", "byOwner", "activities"] as const;
type ReportTab = (typeof REPORT_TABS)[number];

const DEFAULT_TAB: ReportTab = "funnel";
const DEFAULT_GROUP_BY: WonLostGroupBy = "month";

/**
 * Reports page (`crm.reports.read`). The date range, active tab, pipeline and grouping live in the
 * URL (`?range=&from=&to=&tab=&pipelineId=&groupBy=`), so a report view can be shared or reloaded.
 * Only the active tab is mounted, so only its report is requested.
 */
export default function ReportsPage() {
  const { t } = useTranslation(["reports"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const [searchParams, setSearchParams] = useSearchParams();

  const presetParam = searchParams.get("range");
  const preset: RangePreset = isRangePreset(presetParam) ? presetParam : DEFAULT_RANGE_PRESET;
  const customFrom = searchParams.get("from") ?? "";
  const customTo = searchParams.get("to") ?? "";
  const tabParam = searchParams.get("tab");
  const tab: ReportTab = REPORT_TABS.find((value) => value === tabParam) ?? DEFAULT_TAB;
  const groupBy: WonLostGroupBy =
    searchParams.get("groupBy") === "week" ? "week" : DEFAULT_GROUP_BY;
  const pipelineId = searchParams.get("pipelineId") ?? "";

  const range = useMemo(
    () => resolveRange(preset, timeZone, { from: customFrom, to: customTo }),
    [preset, timeZone, customFrom, customTo]
  );

  function update(mutate: (params: URLSearchParams) => void) {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        mutate(next);
        return next;
      },
      { replace: true }
    );
  }

  function setPreset(value: string) {
    update((p) => {
      if (value === DEFAULT_RANGE_PRESET) p.delete("range");
      else p.set("range", value);
      if (value !== "custom") {
        p.delete("from");
        p.delete("to");
      }
    });
  }

  function setCustom(key: "from" | "to", value: string) {
    update((p) => (value ? p.set(key, value) : p.delete(key)));
  }

  const needsRange = tab !== "funnel";

  return (
    <>
      <PageHeader title={t("reports:title")} description={t("reports:description")} />
      <Stack gap="lg">
        <Group gap="md" align="flex-end" wrap="wrap">
          <SegmentedControl
            aria-label={t("reports:range.label")}
            value={preset}
            onChange={setPreset}
            data={RANGE_PRESETS.map((value) => ({
              value,
              label: t(`reports:range.${value}`),
            }))}
          />
          {preset === "custom" && (
            <>
              <TextInput
                type="date"
                label={t("reports:range.from")}
                value={customFrom}
                max={customTo || undefined}
                onChange={(event) => setCustom("from", event.currentTarget.value)}
              />
              <TextInput
                type="date"
                label={t("reports:range.to")}
                value={customTo}
                min={customFrom || undefined}
                onChange={(event) => setCustom("to", event.currentTarget.value)}
              />
            </>
          )}
        </Group>

        <Tabs
          value={tab}
          onChange={(value) =>
            update((p) => (!value || value === DEFAULT_TAB ? p.delete("tab") : p.set("tab", value)))
          }
          keepMounted={false}
        >
          <Tabs.List mb="md">
            {REPORT_TABS.map((value) => (
              <Tabs.Tab key={value} value={value}>
                {t(`reports:tabs.${value}`)}
              </Tabs.Tab>
            ))}
          </Tabs.List>

          {needsRange && !range && (
            <Alert color="yellow" variant="light" role="status">
              {t("reports:range.invalid")}
            </Alert>
          )}

          <Tabs.Panel value="funnel">
            <FunnelReport
              pipelineId={pipelineId}
              onPipelineChange={(id) =>
                update((p) => (id ? p.set("pipelineId", id) : p.delete("pipelineId")))
              }
            />
          </Tabs.Panel>
          {range && (
            <>
              <Tabs.Panel value="wonLost">
                <WonLostReport
                  range={range}
                  groupBy={groupBy}
                  onGroupByChange={(value) =>
                    update((p) =>
                      value === DEFAULT_GROUP_BY ? p.delete("groupBy") : p.set("groupBy", value)
                    )
                  }
                />
              </Tabs.Panel>
              <Tabs.Panel value="leadSources">
                <LeadSourcesReport range={range} />
              </Tabs.Panel>
              <Tabs.Panel value="byOwner">
                <ByOwnerReport range={range} />
              </Tabs.Panel>
              <Tabs.Panel value="activities">
                <ActivitiesReport range={range} />
              </Tabs.Panel>
            </>
          )}
        </Tabs>
      </Stack>
    </>
  );
}
