import { useTranslation } from "react-i18next";
import type { DetailTab } from "@/components/crm/record-detail-shell";
import { AttachmentsTab } from "@/components/files/attachments-tab";
import { usePermission } from "@/hooks/use-permission";
import { ATTACHMENT_PERMISSIONS } from "@/lib/files";
import type { AttachmentRecordType } from "@/types";

/**
 * The "Ekler" tab of a record detail page, ready to spread into `RecordDetailShell` `tabs`: empty
 * without the record type's read permission (or a disabled plan module - `usePermission` is false
 * then) or before the record id is known. The content mounts only while the tab is open (the shell
 * does not keep inactive tabs mounted), so the list is requested lazily.
 */
export function useAttachmentsTab(
  recordType: AttachmentRecordType,
  recordId: string | undefined
): DetailTab[] {
  const { t } = useTranslation(["files"]);
  const canRead = usePermission(ATTACHMENT_PERMISSIONS[recordType].read);
  if (!canRead || !recordId) return [];
  return [
    {
      value: "attachments",
      label: t("files:tab.title"),
      content: <AttachmentsTab key={recordId} recordType={recordType} recordId={recordId} />,
    },
  ];
}
