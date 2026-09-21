import { useTranslation } from "react-i18next";
import { Trophy } from "lucide-react";
import { useWonLost } from "@/hooks/use-reports";
import { formatMoney } from "@/lib/format";
import { lastSixMonthsRange } from "@/lib/report-range";
import { useAuthStore } from "@/store/auth.store";
import type { WonLostRow } from "@/types";
import { KpiCard } from "./kpi-card";

/** "2026-01" -> "2025-12". */
function previousPeriod(period: string): string {
  const [year, month] = period.split("-").map(Number) as [number, number];
  return month === 1
    ? `${year - 1}-12`
    : `${year}-${String(month - 1).padStart(2, "0")}`;
}

function trendPercent(rows: WonLostRow[], currentPeriod: string): number | null {
  const current = rows.find((r) => r.period === currentPeriod)?.wonAmount ?? 0;
  const previous = rows.find((r) => r.period === previousPeriod(currentPeriod))?.wonAmount ?? 0;
  return previous > 0 ? ((current - previous) / previous) * 100 : null;
}

/** Won revenue of the current month, with the change against the previous month. Needs reports.read. */
export function WonThisMonthKpi() {
  const { t } = useTranslation(["home"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const range = lastSixMonthsRange(timeZone);
  const currentPeriod = range.to.slice(0, 7);
  const { data, isLoading, isError } = useWonLost({ ...range, groupBy: "month" });
  const rows = data ?? [];
  const current = rows.find((r) => r.period === currentPeriod)?.wonAmount ?? 0;

  return (
    <KpiCard
      to="/app/reports"
      icon={<Trophy size={18} />}
      label={t("home:stats.wonThisMonth")}
      value={formatMoney(current)}
      loading={isLoading}
      failedText={isError ? t("home:stats.unavailable") : undefined}
      trend={trendPercent(rows, currentPeriod)}
    />
  );
}
