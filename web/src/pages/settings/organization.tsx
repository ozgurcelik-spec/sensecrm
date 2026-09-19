import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Alert, Button, Card, Group, Select, Skeleton, Stack, TextInput } from "@mantine/core";
import { Info } from "lucide-react";
import { LoadError } from "@/components/load-error";
import { PageHeader } from "@/components/page-header";
import { usePermission } from "@/hooks/use-permission";
import { useOrganization, useUpdateOrganization } from "@/hooks/use-organization-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { SUPPORTED_LOCALES } from "@/lib/locale";
import { applyValidationErrors } from "@/lib/api-error";
import { PERMISSIONS, type Locale, type Organization } from "@/types";

const schema = z.object({
  name: z.string().trim().min(1, "auth:validation.required"),
  defaultLocale: z.enum(["tr", "en"]),
  timeZone: z.string().min(1, "auth:validation.required"),
});

type FormValues = z.infer<typeof schema>;

/** IANA zones known to the browser, plus the stored value in case the browser does not list it. */
function useTimeZones(current: string): string[] {
  return useMemo(() => {
    const intl = Intl as unknown as { supportedValuesOf?: (key: "timeZone") => string[] };
    const zones = intl.supportedValuesOf?.("timeZone") ?? ["Europe/Istanbul", "UTC"];
    return zones.includes(current) || !current ? zones : [current, ...zones];
  }, [current]);
}

function OrganizationForm({ organization }: { organization: Organization }) {
  const { t } = useTranslation(["settings", "common", "auth"]);
  const canManage = usePermission(PERMISSIONS.orgSettingsManage);
  const update = useUpdateOrganization();
  const timeZones = useTimeZones(organization.timeZone);

  const {
    register,
    control,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isDirty },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: organization.name,
      defaultLocale: organization.defaultLocale,
      timeZone: organization.timeZone,
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    try {
      await update.mutateAsync(values);
      reset(values);
      toast({ variant: "success", description: t("common:saved") });
    } catch (error) {
      if (!applyValidationErrors(error, setError, ["name", "defaultLocale", "timeZone"])) {
        toastApiError(error);
      }
    }
  });

  return (
    <Card withBorder padding="lg" maw={640}>
      <form onSubmit={onSubmit} noValidate>
        <Stack gap="md">
          {!canManage && (
            <Alert variant="light" color="gray" icon={<Info size={16} />}>
              {t("settings:organization.readOnly")}
            </Alert>
          )}
          <TextInput
            id="org-name"
            label={t("settings:organization.name")}
            withAsterisk
            disabled={!canManage}
            error={errors.name?.message && t(errors.name.message)}
            {...register("name")}
          />
          <TextInput
            label={t("settings:organization.slug")}
            value={organization.slug}
            readOnly
            disabled
          />
          <Controller
            control={control}
            name="defaultLocale"
            render={({ field }) => (
              <Select
                label={t("settings:organization.defaultLocale")}
                data={SUPPORTED_LOCALES.map((locale) => ({
                  value: locale,
                  label: t(`common:languages.${locale}`),
                }))}
                value={field.value}
                onChange={(value) => value && field.onChange(value as Locale)}
                allowDeselect={false}
                disabled={!canManage}
              />
            )}
          />
          <Controller
            control={control}
            name="timeZone"
            render={({ field }) => (
              <Select
                label={t("settings:organization.timeZone")}
                data={timeZones}
                value={field.value}
                onChange={(value) => value && field.onChange(value)}
                searchable
                allowDeselect={false}
                limit={200}
                disabled={!canManage}
                error={errors.timeZone?.message && t(errors.timeZone.message)}
              />
            )}
          />
          {canManage && (
            <Group justify="flex-end">
              <Button type="submit" loading={update.isPending} disabled={!isDirty}>
                {t("common:save")}
              </Button>
            </Group>
          )}
        </Stack>
      </form>
    </Card>
  );
}

export default function OrganizationSettingsPage() {
  const { t } = useTranslation(["settings", "common"]);
  const { data: organization, isLoading, error, refetch } = useOrganization();

  return (
    <>
      <PageHeader
        title={t("settings:organization.title")}
        description={t("settings:organization.description")}
      />
      {isLoading && <Skeleton h={320} maw={640} />}
      {error && <LoadError error={error} onRetry={() => void refetch()} />}
      {organization && <OrganizationForm key={organization.id} organization={organization} />}
    </>
  );
}
