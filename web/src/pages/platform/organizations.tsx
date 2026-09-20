import { useState } from "react";
import { useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Button, Select } from "@mantine/core";
import { Download } from "lucide-react";
import { ListPageFrame } from "@/components/crm/list-page-frame";
import { CreateOrganizationDialog } from "@/components/platform/create-organization-dialog";
import { OrganizationTable } from "@/components/platform/organization-table";
import { UsageExportDialog } from "@/components/platform/usage-export-dialog";
import { useListParams } from "@/hooks/use-list-params";
import { usePlatformOrganizations, usePlatformPlans } from "@/hooks/use-platform";
import { ACCOUNT_SOURCES, TENANT_STATUSES } from "@/types";

/** URL-synced filters: `?q&status&planCode&source&sort&page&pageSize`. Module-level so the array is stable. */
const FILTERS = ["status", "planCode", "source"] as const;

export default function PlatformOrganizationsPage() {
  const { t } = useTranslation(["platform", "common"]);
  const navigate = useNavigate();
  const params = useListParams(FILTERS);
  const { data, isLoading, isFetching, error, refetch } = usePlatformOrganizations(params.query);
  const plans = usePlatformPlans();
  const [creating, setCreating] = useState(false);
  const [exporting, setExporting] = useState(false);

  return (
    <>
      <ListPageFrame
        title={t("platform:organizations.title")}
        description={t("platform:organizations.description")}
        createLabel={t("platform:organizations.new")}
        onCreate={() => setCreating(true)}
        extraActions={
          <Button
            variant="default"
            leftSection={<Download size={16} />}
            onClick={() => setExporting(true)}
          >
            {t("platform:organizations.export")}
          </Button>
        }
        q={params.q}
        onSearch={params.setQ}
        searchPlaceholder={t("platform:organizations.search")}
        hasActiveFilters={params.hasActiveFilters}
        onClearFilters={params.clearFilters}
        filters={
          <>
            <Select
              aria-label={t("platform:filters.status")}
              placeholder={t("platform:filters.status")}
              w={200}
              clearable
              data={TENANT_STATUSES.map((v) => ({ value: v, label: t(`platform:status.${v}`) }))}
              value={params.filters.status || null}
              onChange={(value) => params.setFilter("status", value)}
            />
            <Select
              aria-label={t("platform:filters.plan")}
              placeholder={t("platform:filters.plan")}
              w={200}
              clearable
              data={(plans.data ?? []).map((p) => ({ value: p.code, label: p.name }))}
              value={params.filters.planCode || null}
              onChange={(value) => params.setFilter("planCode", value)}
            />
            <Select
              aria-label={t("platform:filters.source")}
              placeholder={t("platform:filters.source")}
              w={200}
              clearable
              data={ACCOUNT_SOURCES.map((v) => ({ value: v, label: t(`platform:sources.${v}`) }))}
              value={params.filters.source || null}
              onChange={(value) => params.setFilter("source", value)}
            />
          </>
        }
      >
        <OrganizationTable
          params={params}
          data={data}
          isLoading={isLoading}
          isFetching={isFetching}
          error={error}
          onRetry={() => void refetch()}
        />
      </ListPageFrame>

      {creating && (
        <CreateOrganizationDialog
          plans={plans.data ?? []}
          onClose={() => setCreating(false)}
          onCreated={(tenantId) => navigate(`/app/platform/organizations/${tenantId}`)}
        />
      )}
      {exporting && <UsageExportDialog onClose={() => setExporting(false)} />}
    </>
  );
}
