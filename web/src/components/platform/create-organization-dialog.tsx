import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button, Group, Modal, Select, Stack, Text, TextInput } from "@mantine/core";
import { TemporaryPasswordDialog } from "@/components/users/temporary-password-dialog";
import { useCreatePlatformOrganization } from "@/hooks/use-platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import { blankToUndefined } from "@/lib/format";
import type { PlatformPlan } from "@/types";

const schema = z.object({
  organizationName: z.string().trim().min(1, "auth:validation.required"),
  adminDisplayName: z.string().trim().min(1, "auth:validation.required"),
  adminEmail: z.string().trim().min(1, "auth:validation.required").email("auth:validation.email"),
  locale: z.enum(["tr", "en"]),
  planCode: z.string(),
  trialEndsOn: z.string(),
});

type FormValues = z.infer<typeof schema>;
const FIELDS = [
  "organizationName",
  "adminDisplayName",
  "adminEmail",
  "locale",
  "planCode",
  "trialEndsOn",
] as const;

interface CreateOrganizationDialogProps {
  plans: PlatformPlan[];
  onClose: () => void;
  /** Called once the dialog is done (after the one-time password dialog, when there is one). */
  onCreated: (tenantId: string) => void;
}

/**
 * "Organizasyon aç": the M5 endpoint with plan and trial date. A generated admin password comes back
 * once; it is shown in its own dialog and dropped from memory as soon as that dialog is closed.
 */
export function CreateOrganizationDialog({
  plans,
  onClose,
  onCreated,
}: CreateOrganizationDialogProps) {
  const { t } = useTranslation(["platform", "common", "auth"]);
  const create = useCreatePlatformOrganization();
  const [created, setCreated] = useState<{
    tenantId: string;
    email: string;
    password: string;
  } | null>(null);

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      organizationName: "",
      adminDisplayName: "",
      adminEmail: "",
      locale: "tr",
      planCode: "",
      trialEndsOn: "",
    },
  });

  const message = (key?: string) => key && t(key, { defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const result = await create.mutateAsync({
        organizationName: values.organizationName,
        adminDisplayName: values.adminDisplayName,
        adminEmail: values.adminEmail,
        locale: values.locale,
        planCode: blankToUndefined(values.planCode),
        trialEndsOn: blankToUndefined(values.trialEndsOn),
      });
      if (result.generatedPassword) {
        setCreated({
          tenantId: result.organizationId,
          email: result.adminEmail,
          password: result.generatedPassword,
        });
        return;
      }
      toast({
        variant: result.adminInvitationPending ? "default" : "success",
        description: result.adminInvitationPending
          ? t("platform:create.pendingInvitation", { email: result.adminEmail })
          : t("platform:create.created"),
      });
      onCreated(result.organizationId);
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  if (created) {
    return (
      <TemporaryPasswordDialog
        email={created.email}
        temporaryPassword={created.password}
        onClose={() => {
          const tenantId = created.tenantId;
          setCreated(null);
          onCreated(tenantId);
        }}
      />
    );
  }

  return (
    <Modal opened onClose={onClose} title={t("platform:create.title")} centered size="md">
      <form onSubmit={onSubmit} noValidate>
        <Stack gap="md">
          <TextInput
            label={t("platform:create.organizationName")}
            withAsterisk
            data-autofocus
            error={message(errors.organizationName?.message)}
            {...register("organizationName")}
          />
          <TextInput
            label={t("platform:create.adminDisplayName")}
            withAsterisk
            error={message(errors.adminDisplayName?.message)}
            {...register("adminDisplayName")}
          />
          <TextInput
            type="email"
            label={t("platform:create.adminEmail")}
            withAsterisk
            error={message(errors.adminEmail?.message)}
            {...register("adminEmail")}
          />
          <Controller
            control={control}
            name="locale"
            render={({ field }) => (
              <Select
                label={t("platform:create.locale")}
                data={[
                  { value: "tr", label: t("common:languages.tr") },
                  { value: "en", label: t("common:languages.en") },
                ]}
                value={field.value}
                onChange={(value) => field.onChange(value ?? "tr")}
                allowDeselect={false}
                error={message(errors.locale?.message)}
              />
            )}
          />
          <Controller
            control={control}
            name="planCode"
            render={({ field }) => (
              <Select
                label={t("platform:create.plan")}
                placeholder={t("platform:create.planDefault")}
                clearable
                data={plans
                  .filter((p) => p.isActive)
                  .map((p) => ({ value: p.code, label: p.name }))}
                value={field.value || null}
                onChange={(value) => field.onChange(value ?? "")}
                error={message(errors.planCode?.message)}
              />
            )}
          />
          <TextInput
            type="date"
            label={t("platform:create.trialEndsOn")}
            description={t("platform:create.trialHint")}
            error={message(errors.trialEndsOn?.message)}
            {...register("trialEndsOn")}
          />
          <Text size="xs" c="dimmed">
            {t("platform:create.passwordHint")}
          </Text>
          <Group justify="flex-end" mt="sm">
            <Button variant="default" onClick={onClose} disabled={create.isPending}>
              {t("common:cancel")}
            </Button>
            <Button type="submit" loading={create.isPending}>
              {t("common:create")}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
