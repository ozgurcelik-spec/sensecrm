import { useTranslation } from "react-i18next";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button, Group, Modal, PasswordInput, Select, Stack, Text, TextInput } from "@mantine/core";
import { useCreateMember } from "@/hooks/use-organization-queries";
import { toast, toastApiError } from "@/hooks/use-toast";
import { applyValidationErrors } from "@/lib/api-error";
import type { Role } from "@/types";

const PASSWORD_MIN_LENGTH = 8;

const schema = z.object({
  email: z.string().trim().min(1, "auth:validation.required").email("auth:validation.email"),
  displayName: z.string().trim().min(1, "auth:validation.required"),
  // Ignored by the server when the email already has an account, so it may stay empty in that case;
  // when given it must meet the minimum length.
  password: z
    .string()
    .refine(
      (value) => value === "" || value.length >= PASSWORD_MIN_LENGTH,
      "auth:validation.passwordMin"
    ),
  roleId: z.string().min(1, "auth:validation.required"),
});

type FormValues = z.infer<typeof schema>;
const FIELDS = ["email", "displayName", "password", "roleId"] as const;

interface AddMemberDialogProps {
  opened: boolean;
  onClose: () => void;
  roles: Role[];
}

export function AddMemberDialog({ opened, onClose, roles }: AddMemberDialogProps) {
  const { t } = useTranslation(["users", "common", "auth"]);
  const create = useCreateMember();
  const defaultRole = roles.find((role) => !role.isSystem) ?? roles[0];

  const {
    register,
    control,
    handleSubmit,
    setError,
    reset,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    values: { email: "", displayName: "", password: "", roleId: defaultRole?.id ?? "" },
  });

  const message = (key?: string) => key && t(key, { min: PASSWORD_MIN_LENGTH });

  function close() {
    reset();
    onClose();
  }

  const onSubmit = handleSubmit(async (values) => {
    try {
      await create.mutateAsync(values);
      toast({ variant: "success", description: t("users:users.added") });
      close();
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) toastApiError(error);
    }
  });

  return (
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
          <Stack gap={4}>
            <PasswordInput
              id="member-password"
              autoComplete="new-password"
              label={t("users:users.password")}
              error={message(errors.password?.message)}
              {...register("password")}
            />
            <Text size="xs" c="dimmed">
              {t("users:users.passwordHint")}
            </Text>
          </Stack>
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
  );
}
