import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Alert, Button, Group, Modal, ScrollArea, Stack, TextInput } from "@mantine/core";
import { Lock } from "lucide-react";
import { useSaveRole } from "@/hooks/use-role-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import type { Permission, Role } from "@/types";
import { PermissionChecklist } from "./permission-checklist";

const schema = z.object({
  name: z.string().trim().min(1, "auth:validation.required"),
  permissions: z.array(z.string()),
});

type FormValues = z.infer<typeof schema>;

interface RoleFormDialogProps {
  opened: boolean;
  onClose: () => void;
  /** Role to edit/view; undefined creates a new role. */
  role?: Role;
  catalog: Permission[];
  /** False shows the role read-only (no org.roles.manage, or a system role). */
  editable: boolean;
}

export function RoleFormDialog({ opened, onClose, role, catalog, editable }: RoleFormDialogProps) {
  const { t } = useTranslation(["users", "common", "auth"]);
  const save = useSaveRole();
  const readOnly = !editable || !!role?.isSystem;

  const {
    register,
    control,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    values: { name: role?.name ?? "", permissions: role?.permissions ?? [] },
  });

  const title = !role
    ? t("users:roles.createTitle")
    : readOnly
      ? t("users:roles.viewTitle")
      : t("users:roles.editTitle");

  const onSubmit = handleSubmit(async (values) => {
    try {
      await save.mutateAsync({ id: role?.id, ...values });
      toast({
        variant: "success",
        description: role ? t("users:roles.updated") : t("users:roles.created"),
      });
      onClose();
    } catch (error) {
      if (!applyValidationErrors(error, setError, ["name", "permissions"])) toastApiError(error);
    }
  });

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={title}
      size="xl"
      centered
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <form onSubmit={onSubmit} noValidate>
        <Stack gap="md">
          {role?.isSystem && (
            <Alert variant="light" color="gray" icon={<Lock size={16} />}>
              {t("users:roles.systemReadonly")}
            </Alert>
          )}
          <TextInput
            id="role-name"
            label={t("users:roles.name")}
            withAsterisk={!readOnly}
            readOnly={readOnly}
            data-autofocus
            error={errors.name?.message && t(errors.name.message)}
            {...register("name")}
          />
          <Controller
            control={control}
            name="permissions"
            render={({ field }) => (
              <PermissionChecklist
                catalog={catalog}
                value={field.value}
                onChange={field.onChange}
                readOnly={readOnly}
              />
            )}
          />
          <Group justify="flex-end" mt="sm">
            <Button variant="default" onClick={onClose} disabled={save.isPending}>
              {readOnly ? t("common:close") : t("common:cancel")}
            </Button>
            {!readOnly && (
              <Button type="submit" loading={save.isPending}>
                {role ? t("common:save") : t("common:create")}
              </Button>
            )}
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
