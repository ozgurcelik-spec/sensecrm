import { lazy, useEffect, type ReactNode } from "react";
import { BrowserRouter as Router, Navigate, Route, Routes } from "react-router";
import { useTranslation } from "react-i18next";
import ComingSoonPage from "@/components/coming-soon-page";
import NoAccess from "@/components/no-access";
import { PermissionGuard } from "@/components/permission-guard";
import ProtectedRoute from "@/components/protected-route";
import RouteBoundary from "@/components/route-boundary";
import { CRM_MODULE_ITEMS } from "@/config/navigation";
import AppLayout from "@/layouts/app-layout";
import LoginPage from "@/pages/auth/login";
import NotFoundPage from "@/pages/not-found";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

// Only the shell and the login page are eager; every other page is a separate chunk.
const SignupPage = lazy(() => import("@/pages/auth/signup"));
const HomePage = lazy(() => import("@/pages/home"));
const AccountPage = lazy(() => import("@/pages/account"));
const OrganizationSettingsPage = lazy(() => import("@/pages/settings/organization"));
const UsersPage = lazy(() => import("@/pages/settings/users"));
const RolesPage = lazy(() => import("@/pages/settings/roles"));
const AuditLogPage = lazy(() => import("@/pages/audit-log"));

function RequirePermission({ permission, children }: { permission: string; children: ReactNode }) {
  return (
    <PermissionGuard permission={permission} fallback={<NoAccess />}>
      {children}
    </PermissionGuard>
  );
}

/** Keeps the UI language in line with the signed-in user's saved locale. */
function useUserLocaleSync() {
  const { i18n } = useTranslation();
  const locale = useAuthStore((state) => state.me?.user.locale);
  useEffect(() => {
    if (locale && !i18n.language?.startsWith(locale)) void i18n.changeLanguage(locale);
  }, [locale, i18n]);
}

export default function App() {
  const hasSession = useAuthStore((state) => !!(state.token || state.refreshToken));
  const refreshMe = useAuthStore((state) => state.refreshMe);
  useUserLocaleSync();

  // The persisted profile may be stale (permissions or organizations changed since the last visit).
  useEffect(() => {
    if (hasSession) void refreshMe();
  }, [hasSession, refreshMe]);

  return (
    <Router>
      <RouteBoundary>
        <Routes>
          <Route path="/" element={<Navigate to="/app" replace />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/signup" element={<SignupPage />} />

          <Route
            path="/app"
            element={
              <ProtectedRoute>
                <AppLayout />
              </ProtectedRoute>
            }
          >
            <Route index element={<HomePage />} />
            {CRM_MODULE_ITEMS.map((item) => (
              <Route
                key={item.key}
                path={item.path.replace("/app/", "")}
                element={
                  <PermissionGuard anyOf={item.permissions} fallback={<NoAccess />}>
                    <ComingSoonPage titleKey={item.labelKey} />
                  </PermissionGuard>
                }
              />
            ))}
            <Route path="account" element={<AccountPage />} />
            <Route path="settings" element={<Navigate to="/app/settings/organization" replace />} />
            <Route path="settings/organization" element={<OrganizationSettingsPage />} />
            <Route
              path="settings/users"
              element={
                <RequirePermission permission={PERMISSIONS.orgUsersRead}>
                  <UsersPage />
                </RequirePermission>
              }
            />
            <Route
              path="settings/roles"
              element={
                <RequirePermission permission={PERMISSIONS.orgUsersRead}>
                  <RolesPage />
                </RequirePermission>
              }
            />
            <Route
              path="settings/audit"
              element={
                <RequirePermission permission={PERMISSIONS.orgAuditRead}>
                  <AuditLogPage />
                </RequirePermission>
              }
            />
            <Route path="*" element={<NotFoundPage />} />
          </Route>

          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </RouteBoundary>
    </Router>
  );
}
