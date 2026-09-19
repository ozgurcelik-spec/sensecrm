import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Alert, Button, Group, PasswordInput, Stack, Text } from "@mantine/core";
import { toast } from "@/hooks/use-toast";
import { applyValidationErrors, getApiErrorMessage, getApiErrorStatus } from "@/lib/api-error";
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  passwordField,
  refinePasswordAgainstEmail,
} from "@/lib/password-policy";
import { useAuthStore } from "@/store/auth.store";

const FIELDS = ["currentPassword", "newPassword"] as const;

function createSchema(email: string) {
  return z
    .object({
      currentPassword: z.string().min(1, "auth:validation.required"),
      newPassword: passwordField(),
      confirmPassword: z.string().min(1, "auth:validation.required"),
    })
    .superRefine((values, ctx) => {
      refinePasswordAgainstEmail(values.newPassword, email, ctx, "newPassword");
      if (values.confirmPassword && values.confirmPassword !== values.newPassword) {
        ctx.addIssue({
          code: "custom",
          path: ["confirmPassword"],
          message: "security:changePassword.mismatch",
        });
      }
      if (values.newPassword && values.newPassword === values.currentPassword) {
        ctx.addIssue({
          code: "custom",
          path: ["newPassword"],
          message: "security:changePassword.sameAsCurrent",
        });
      }
    });
}

type FormValues = z.infer<ReturnType<typeof createSchema>>;

interface ChangePasswordFormProps {
  /** The signed-in user's e-mail (the new password must not contain its local part). */
  email: string;
  /** Called after the password was changed and the new tokens were stored. */
  onChanged?: () => void;
  /** Forced screen: no "saved" toast, the caller navigates on. */
  forced?: boolean;
}

/** Shared by the Profile page and the forced "change your password" screen. */
export function ChangePasswordForm({ email, onChanged, forced = false }: ChangePasswordFormProps) {
  const { t } = useTranslation(["security", "auth", "common"]);
  const changePassword = useAuthStore((state) => state.changePassword);
  const [serverError, setServerError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(createSchema(email)),
    defaultValues: { currentPassword: "", newPassword: "", confirmPassword: "" },
  });

  const message = (key?: string) =>
    key && t(key, { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_LENGTH, defaultValue: key });

  const onSubmit = handleSubmit(async (values) => {
    setServerError(null);
    try {
      await changePassword(values.currentPassword, values.newPassword);
    } catch (error) {
      if (getApiErrorStatus(error) === 401) {
        setError("currentPassword", {
          type: "server",
          message: "security:changePassword.currentWrong",
        });
      } else if (!applyValidationErrors(error, setError, FIELDS)) {
        setServerError(getApiErrorMessage(error));
      }
      return;
    }
    reset();
    if (!forced) toast({ variant: "success", description: t("security:changePassword.success") });
    onChanged?.();
  });

  return (
    <form onSubmit={onSubmit} noValidate>
      <Stack gap="md">
        {serverError && (
          <Alert color="red" variant="light" role="alert">
            {serverError}
          </Alert>
        )}
        <PasswordInput
          id="current-password"
          autoComplete="current-password"
          label={t("security:changePassword.current")}
          withAsterisk
          error={message(errors.currentPassword?.message)}
          {...register("currentPassword")}
        />
        <Stack gap={4}>
          <PasswordInput
            id="new-password"
            autoComplete="new-password"
            label={t("security:changePassword.new")}
            withAsterisk
            error={message(errors.newPassword?.message)}
            {...register("newPassword")}
          />
          <Text size="xs" c="dimmed">
            {t("security:password.policy", { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_LENGTH })}
          </Text>
        </Stack>
        <PasswordInput
          id="confirm-password"
          autoComplete="new-password"
          label={t("security:changePassword.confirm")}
          withAsterisk
          error={message(errors.confirmPassword?.message)}
          {...register("confirmPassword")}
        />
        <Group justify="flex-end">
          <Button type="submit" loading={isSubmitting}>
            {t("security:changePassword.submit")}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
