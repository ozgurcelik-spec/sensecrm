import { useTranslation } from "react-i18next";
import { formatSlaDelta } from "@/lib/case";
import { formatDateTime } from "@/lib/dates";
import { useAuthStore } from "@/store/auth.store";
import type { CaseListItem } from "@/types";

export type SlaSource = Pick<
  CaseListItem,
  | "status"
  | "slaState"
  | "isSlaBreached"
  | "firstResponseAt"
  | "firstResponseDueAt"
  | "resolvedAt"
  | "dueAt"
  | "firstResponseBreached"
  | "resolutionBreached"
>;

export interface SlaLine {
  key: "firstResponse" | "resolution";
  label: string;
  text: string;
  breached: boolean;
  /** Nothing has happened yet: the target is still ahead of (or behind) us. */
  pending: boolean;
}

/** The two SLA lines (first response, resolution) of a case: target, and what already happened. */
export function useSlaLines(item: SlaSource): SlaLine[] {
  const { t } = useTranslation(["service"]);
  const timeZone = useAuthStore((state) => state.me?.organization.timeZone);
  const at = (iso: string) => formatDateTime(iso, timeZone);

  return [
    {
      key: "firstResponse",
      label: t("service:sla.firstResponse"),
      breached: item.firstResponseBreached,
      pending: !item.firstResponseAt,
      text: item.firstResponseAt
        ? t("service:sla.answered", {
            date: at(item.firstResponseAt),
            target: at(item.firstResponseDueAt),
          })
        : t("service:sla.target", {
            date: at(item.firstResponseDueAt),
            delta: formatSlaDelta(item.firstResponseDueAt),
          }),
    },
    {
      key: "resolution",
      label: t("service:sla.resolution"),
      breached: item.resolutionBreached,
      pending: !item.resolvedAt,
      text: item.resolvedAt
        ? t("service:sla.resolvedLine", { date: at(item.resolvedAt), target: at(item.dueAt) })
        : t("service:sla.target", { date: at(item.dueAt), delta: formatSlaDelta(item.dueAt) }),
    },
  ];
}
