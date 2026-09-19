import { useTranslation } from "react-i18next";
import { Navigate, useNavigate } from "react-router";
import { Button } from "@mantine/core";
import { ChangePasswordForm } from "@/components/security/change-password-form";
import { useAuthStore } from "@/store/auth.store";
import AuthLayout from "./auth-layout";

/**
 * Forced full-page "change your password" screen (no app shell): shown while the API answers
 * everything but the password change with 403 `auth.password_change_required`.
 */
export default function ForcedChangePasswordPage() {
  const { t } = useTranslation(["security"]);
  const navigate = useNavigate();
  const me = useAuthStore((state) => state.me);
  const mustChangePassword = useAuthStore((state) => state.mustChangePassword);
  const logout = useAuthStore((state) => state.logout);

  // Nothing to force (e.g. a bookmarked URL): go to the app.
  if (!mustChangePassword) return <Navigate to="/app" replace />;
  if (!me) return null;

  return (
    <AuthLayout
      title={t("security:forced.title")}
      subtitle={t("security:forced.subtitle")}
      footer={
        <Button
          variant="subtle"
          size="compact-sm"
          onClick={() => {
            void logout().finally(() => navigate("/login", { replace: true }));
          }}
        >
          {t("security:forced.logout")}
        </Button>
      }
    >
      <ChangePasswordForm
        email={me.user.email}
        forced
        onChanged={() => navigate("/app", { replace: true })}
      />
    </AuthLayout>
  );
}
