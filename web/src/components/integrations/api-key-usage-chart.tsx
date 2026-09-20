import { useTranslation } from "react-i18next";
import { BarChart } from "@mantine/charts";
import type { ApiKeyUsageDay } from "@/types";

/** Daily requests / errors / throttled requests of a key (lazy chunk: recharts stays out of the page bundle). */
export default function ApiKeyUsageChart({ items }: { items: ApiKeyUsageDay[] }) {
  const { t } = useTranslation(["integrations"]);
  return (
    <BarChart
      h={220}
      data={items}
      dataKey="day"
      series={[
        { name: "requests", label: t("integrations:apiKeys.usage.requests"), color: "blue.6" },
        { name: "errors", label: t("integrations:apiKeys.usage.errors"), color: "red.6" },
        { name: "throttled", label: t("integrations:apiKeys.usage.throttled"), color: "orange.6" },
      ]}
      tickLine="y"
      withLegend
    />
  );
}
