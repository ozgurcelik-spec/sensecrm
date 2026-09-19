import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, Navigate, useNavigate } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Alert, Anchor, Button, PasswordInput, Stack, TextInput } from "@mantine/core";
import { applyValidationErrors, getApiErrorMessage } from "@/lib/api-error";
import { toLocale } from "@/lib/locale";
import { useAuthStore } from "@/store/auth.store";
import AuthLayout from "./auth-layout";

export const PASSWORD_MIN_LENGTH = 8;

const schema = z.object({
  organizationName: z.string().trim().min(1, "auth:validation.required"),
  displayName: z.string().trim().min(1, "auth:validation.required"),
  email: z.string().trim().min(1, "auth:validation.required").email("auth:validation.email"),
  password: z.string().min(PASSWORD_MIN_LENGTH, "auth:validation.passwordMin"),
});

type FormValues = z.infer<typeof schema>;
const FIELDS = ["organizationName", "displayName", "email", "password"] as const;

/** Zoho-style self sign-up: creates a new organization with the caller as its administrator. */
export default function SignupPage() {
  const { t, i18n } = useTranslation(["auth", "common"]);
  const navigate = useNavigate();
  const signup = useAuthStore((state) => state.signup);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated());
  const [serverError, setServerError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { organizationName: "", displayName: "", email: "", password: "" },
  });

  if (isAuthenticated) return <Navigate to="/app" replace />;

  const fieldError = (message?: string) =>
    message && t(message, { min: PASSWORD_MIN_LENGTH, defaultValue: message });

  const onSubmit = handleSubmit(async (values) => {
    setServerError(null);
    try {
      await signup({ ...values, locale: toLocale(i18n.resolvedLanguage ?? i18n.language) });
      navigate("/app", { replace: true });
    } catch (error) {
      if (!applyValidationErrors(error, setError, FIELDS)) {
        setServerError(getApiErrorMessage(error));
      }
    }
  });

  return (
    <AuthLayout
      title={t("auth:signup.title")}
      subtitle={t("auth:signup.subtitle")}
      footer={
        <>
          {t("auth:signup.haveAccount")}{" "}
          <Anchor component={Link} to="/login" fw={600}>
            {t("auth:signup.loginLink")}
          </Anchor>
        </>
      }
    >
      <form onSubmit={onSubmit} noValidate>
        <Stack gap="md">
          {serverError && (
            <Alert color="red" variant="light" role="alert">
              {serverError}
            </Alert>
          )}
          <TextInput
            id="signup-organization"
            label={t("auth:organizationName")}
            autoComplete="organization"
            withAsterisk
            error={fieldError(errors.organizationName?.message)}
            {...register("organizationName")}
          />
          <TextInput
            id="signup-name"
            label={t("auth:displayName")}
            autoComplete="name"
            withAsterisk
            error={fieldError(errors.displayName?.message)}
            {...register("displayName")}
          />
          <TextInput
            id="signup-email"
            type="email"
            label={t("auth:email")}
            autoComplete="email"
            withAsterisk
            error={fieldError(errors.email?.message)}
            {...register("email")}
          />
          <PasswordInput
            id="signup-password"
            label={t("auth:password")}
            autoComplete="new-password"
            withAsterisk
            error={fieldError(errors.password?.message)}
            {...register("password")}
          />
          <Button type="submit" fullWidth loading={isSubmitting}>
            {t("auth:signup.submit")}
          </Button>
        </Stack>
      </form>
    </AuthLayout>
  );
}
