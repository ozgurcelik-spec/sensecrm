import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button, Card, Group, Select, Stack, TextInput } from "@mantine/core";
import { PageHeader } from "@/components/page-header";
import { SUPPORTED_LOCALES } from "@/lib/locale";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { updateMe } from "@/services/me.service";
import { useAuthStore } from "@/store/auth.store";
import type { Locale, Me } from "@/types";

const schema = z.object({
  displayName: z.string().trim().min(1, "auth:validation.required"),
  locale: z.enum(["tr", "en"]),
});

type FormValues = z.infer<typeof schema>;

function ProfileForm({ me }: { me: Me }) {
  const { t, i18n } = useTranslation(["account", "common", "auth"]);
  const refreshMe = useAuthStore((state) => state.refreshMe);

  const {
    register,
    control,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isDirty, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { displayName: me.user.displayName, locale: me.user.locale },
  });

  const onSubmit = handleSubmit(async (values) => {
    try {
      await updateMe(values);
      await refreshMe();
      await i18n.changeLanguage(values.locale);
      reset(values);
      toast({ variant: "success", description: t("common:saved") });
    } catch (error) {
      if (!applyValidationErrors(error, setError, ["displayName", "locale"])) toastApiError(error);
    }
  });

  return (
    <Card withBorder padding="lg" maw={560}>
      <form onSubmit={onSubmit} noValidate>
        <Stack gap="md">
          <TextInput label={t("account:email")} value={me.user.email} readOnly disabled />
          <TextInput
            id="profile-display-name"
            label={t("account:displayName")}
            withAsterisk
            error={errors.displayName?.message && t(errors.displayName.message)}
            {...register("displayName")}
          />
          <Controller
            control={control}
            name="locale"
            render={({ field }) => (
              <Select
                label={t("account:language")}
                data={SUPPORTED_LOCALES.map((locale) => ({
                  value: locale,
                  label: t(`common:languages.${locale}`),
                }))}
                value={field.value}
                onChange={(value) => value && field.onChange(value as Locale)}
                allowDeselect={false}
              />
            )}
          />
          <Group justify="flex-end">
            <Button type="submit" loading={isSubmitting} disabled={!isDirty}>
              {t("common:save")}
            </Button>
          </Group>
        </Stack>
      </form>
    </Card>
  );
}

export default function AccountPage() {
  const { t } = useTranslation(["account"]);
  const me = useAuthStore((state) => state.me);
  if (!me) return null;

  return (
    <>
      <PageHeader title={t("account:title")} description={t("account:description")} />
      {/* Keyed by user so the form re-initializes if the profile changes underneath it. */}
      <ProfileForm key={me.user.id} me={me} />
    </>
  );
}
