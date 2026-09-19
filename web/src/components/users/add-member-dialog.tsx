import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button, Group, Modal, Select, Stack, Text, TextInput } from "@mantine/core";
import { useCreateMember } from "@/hooks/use-organization-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import type { Role } from "@/types";
import { TemporaryPasswordDialog } from "./temporary-password-dialog";

const schema = z.object({
  email: z.string().trim().min(1, "auth:validation.required").email("auth:validation.email"),
  displayName: z.string().trim().min(1, "auth:validation.required"),
  roleId: z.string().min(1, "auth:validation.required"),
});

type FormValues = z.infer<typeof schema>;
const FIELDS = ["email", "displayName", "roleId"] as const;

interface AddMemberDialogProps {
  opened: boolean;
  onClose: () => void;
  roles: Role[];
}

export function AddMemberDialog({ opened, onClose, roles }: AddMemberDialogProps) {
  const { t } = useTranslation(["users", "common", "auth", "security"]);
  const create = useCreateMember();
  const defaultRole = roles.find((role) => !role.isSystem) ?? roles[0];
  // Shown once after creating a new account; dropped from memory as soon as the dialog is closed.
  const [created, setCreated] = useState<{ email: string; temporaryPassword: string } | null>(null);

  const {
    register,
    control,
    handleSubmit,
    setError,
    reset,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    values: { email: "", displayName: "", roleId: defaultRole?.id ?? "" },
  });

  const message = (key?: string) => key && t(key, { defaultValue: key });

  function close() {
    reset();
    onClose();
  }

  const onSubmit = handleSubmit(async (values) => {
    try {
      const result = await create.mutateAsync(values);
      if (result.temporaryPassword) {
        // New account: the one-time password dialog replaces the success toast.
        setCreated({ email: result.email, temporaryPassword: result.temporaryPassword });
      } else if (result.status === "pending") {
        toast({ variant: "default", description: t("security:members.pendingNotice") });
      } else {
        toast({ variant: "success", description: t("users:users.added") });
      }
      close();
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  return (
    <>
      <Modal opened={opened} onClose={close} title={t("users:users.addTitle")} centered>
        <form onSubmit={onSubmit} noValidate>
          <Stack gap="md">
            <TextInput
              id="member-email"
              type="email"
              label={t("users:users.email")}
              withAsterisk
              data-autofocus
              error={message(errors.email?.message)}
              {...register("email")}
            />
            <TextInput
              id="member-name"
              label={t("users:users.name")}
              withAsterisk
              error={message(errors.displayName?.message)}
              {...register("displayName")}
            />
            <Controller
              control={control}
              name="roleId"
              render={({ field }) => (
                <Select
                  label={t("users:users.role")}
                  withAsterisk
                  data={roles.map((role) => ({ value: role.id, label: role.name }))}
                  value={field.value || null}
                  onChange={(value) => field.onChange(value ?? "")}
                  allowDeselect={false}
                  error={message(errors.roleId?.message)}
                />
              )}
            />
            <Text size="xs" c="dimmed">
              {t("security:members.createHint")}
            </Text>
            <Group justify="flex-end" mt="sm">
              <Button variant="default" onClick={close} disabled={create.isPending}>
                {t("common:cancel")}
              </Button>
              <Button type="submit" loading={create.isPending}>
                {t("common:create")}
              </Button>
            </Group>
          </Stack>
        </form>
      </Modal>
      {created && (
        <TemporaryPasswordDialog
          email={created.email}
          temporaryPassword={created.temporaryPassword}
          onClose={() => setCreated(null)}
        />
      )}
    </>
  );
}
