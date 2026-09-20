import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Select, TextInput } from "@mantine/core";
import { ListPageFrame } from "@/components/crm/list-page-frame";
import { OrganizationSelect } from "@/components/platform/organization-select";
import { PlatformAuditTable } from "@/components/platform/platform-audit-table";
import { useListParams } from "@/hooks/use-list-params";
import { PLATFORM_AUDIT_ACTIONS } from "@/lib/platform";

const FILTERS = ["action", "tenantId", "from", "to"] as const;

/** Platform audit: filters (action, organization, UTC day range) and the page live in the URL. */
export default function PlatformAuditPage() {
  const { t } = useTranslation(["platform"]);
  const params = useListParams(FILTERS);
  const { action, tenantId, from, to } = params.filters;
  const query = useMemo(
    () => ({
      action: action || undefined,
      tenantId: tenantId || undefined,
      from: from || undefined,
      to: to || undefined,
    }),
    [action, tenantId, from, to]
  );

  return (
    <ListPageFrame
      title={t("platform:audit.title")}
      description={t("platform:audit.description")}
      createLabel=""
      q=""
      onSearch={() => undefined}
      searchPlaceholder=""
      hideSearch
      hasActiveFilters={params.hasActiveFilters}
      onClearFilters={params.clearFilters}
      filters={
        <>
          <Select
            aria-label={t("platform:audit.filters.action")}
            placeholder={t("platform:audit.filters.action")}
            w={240}
            clearable
            data={PLATFORM_AUDIT_ACTIONS.map((v) => ({
              value: v,
              label: t(`platform:audit.actions.${v}`),
            }))}
            value={action || null}
            onChange={(value) => params.setFilter("action", value)}
          />
          <OrganizationSelect
            value={tenantId || null}
            onChange={(value) => params.setFilter("tenantId", value)}
          />
          <TextInput
            type="date"
            aria-label={t("platform:audit.filters.from")}
            title={t("platform:audit.filters.from")}
            value={from}
            max={to || undefined}
            onChange={(event) => params.setFilter("from", event.currentTarget.value || null)}
          />
          <TextInput
            type="date"
            aria-label={t("platform:audit.filters.to")}
            title={t("platform:audit.filters.to")}
            value={to}
            min={from || undefined}
            onChange={(event) => params.setFilter("to", event.currentTarget.value || null)}
          />
        </>
      }
    >
      <PlatformAuditTable
        query={query}
        page={params.page}
        pageSize={params.pageSize}
        onPageChange={params.setPage}
        onPageSizeChange={params.setPageSize}
      />
    </ListPageFrame>
  );
}
