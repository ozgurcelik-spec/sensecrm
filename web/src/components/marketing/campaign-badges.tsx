import { useTranslation } from "react-i18next";
import { Badge } from "@mantine/core";
import { CAMPAIGN_STATUS_COLOR, CAMPAIGN_TYPE_COLOR, MEMBER_STATUS_COLOR } from "@/lib/campaign";
import type { CampaignStatus, CampaignType, CampaignMemberStatus } from "@/types";

export function CampaignTypeBadge({ type }: { type: CampaignType }) {
  const { t } = useTranslation(["campaigns"]);
  return (
    <Badge variant="outline" color={CAMPAIGN_TYPE_COLOR[type] ?? "gray"}>
      {t(`campaigns:types.${type}`, { defaultValue: type })}
    </Badge>
  );
}

export function CampaignStatusBadge({ status }: { status: CampaignStatus }) {
  const { t } = useTranslation(["campaigns"]);
  return (
    <Badge variant="light" color={CAMPAIGN_STATUS_COLOR[status] ?? "gray"}>
      {t(`campaigns:statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function MemberStatusBadge({ status }: { status: CampaignMemberStatus }) {
  const { t } = useTranslation(["campaigns"]);
  return (
    <Badge variant="light" color={MEMBER_STATUS_COLOR[status] ?? "gray"}>
      {t(`campaigns:memberStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}
