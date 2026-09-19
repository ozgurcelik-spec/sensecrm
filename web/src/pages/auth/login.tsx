import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, Navigate, useLocation, useNavigate } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Alert, Anchor, Button, PasswordInput, Stack, TextInput } from "@mantine/core";
import { getApiErrorMessage } from "@/lib/api-error";
import { useAuthStore } from "@/store/auth.store";
import AuthLayout from "./auth-layout";

// Messages are i18n keys, translated at render time so they follow a language switch.
const schema = z.object({
  email: z.string().trim().min(1, "auth:validation.required").email("auth:validation.email"),
  password: z.string().min(1, "auth:validation.required"),
});

type FormValues = z.infer<typeof schema>;

export default function LoginPage() {
  const { t } = useTranslation(["auth", "common"]);
  const navigate = useNavigate();
  const location = useLocation();
  const login = useAuthStore((state) => state.login);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated());
  const [serverError, setServerError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { email: "", password: "" },
  });

  const from = (location.state as { from?: string } | null)?.from;
  const target = from?.startsWith("/app") ? from : "/app";

  if (isAuthenticated) return <Navigate to={target} replace />;

  const onSubmit = handleSubmit(async (values) => {
    setServerError(null);
    try {
      await login(values.email, values.password);
      navigate(target, { replace: true });
    } catch (error) {
      setServerError(getApiErrorMessage(error));
    }
  });

  return (
    <AuthLayout
      title={t("auth:login.title")}
      subtitle={t("auth:login.subtitle")}
      footer={
        <>
          {t("auth:login.noAccount")}{" "}
          <Anchor component={Link} to="/signup" fw={600}>
            {t("auth:login.signupLink")}
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
            id="login-email"
            type="email"
            autoComplete="email"
            label={t("auth:email")}
            withAsterisk
            error={errors.email?.message && t(errors.email.message)}
            {...register("email")}
          />
          <PasswordInput
            id="login-password"
            autoComplete="current-password"
            label={t("auth:password")}
            withAsterisk
            error={errors.password?.message && t(errors.password.message)}
            {...register("password")}
          />
          <Button type="submit" fullWidth loading={isSubmitting}>
            {t("auth:login.submit")}
          </Button>
        </Stack>
      </form>
    </AuthLayout>
  );
}
