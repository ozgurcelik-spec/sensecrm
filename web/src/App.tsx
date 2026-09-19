import { lazy, useEffect, type ReactNode } from "react";
import { BrowserRouter as Router, Navigate, Route, Routes } from "react-router";
import { useTranslation } from "react-i18next";
import NoAccess from "@/components/no-access";
import { PermissionGuard } from "@/components/permission-guard";
import ProtectedRoute, { CHANGE_PASSWORD_PATH } from "@/components/protected-route";
import RouteBoundary from "@/components/route-boundary";
import AppLayout from "@/layouts/app-layout";
import LoginPage from "@/pages/auth/login";
import NotFoundPage from "@/pages/not-found";
import { useAuthStore } from "@/store/auth.store";
import { PERMISSIONS } from "@/types";

// Only the shell and the login page are eager; every other page is a separate chunk.
const SignupPage = lazy(() => import("@/pages/auth/signup"));
const ForcedChangePasswordPage = lazy(() => import("@/pages/auth/change-password"));
const HomePage = lazy(() => import("@/pages/home"));
const AccountPage = lazy(() => import("@/pages/account"));
const OrganizationSettingsPage = lazy(() => import("@/pages/settings/organization"));
const UsersPage = lazy(() => import("@/pages/settings/users"));
const RolesPage = lazy(() => import("@/pages/settings/roles"));
const AuditLogPage = lazy(() => import("@/pages/audit-log"));
const LeadsPage = lazy(() => import("@/pages/crm/leads"));
const LeadDetailPage = lazy(() => import("@/pages/crm/lead-detail"));
const ContactsPage = lazy(() => import("@/pages/crm/contacts"));
const ContactDetailPage = lazy(() => import("@/pages/crm/contact-detail"));
const AccountsPage = lazy(() => import("@/pages/crm/accounts"));
const AccountDetailPage = lazy(() => import("@/pages/crm/account-detail"));
const DealsPage = lazy(() => import("@/pages/crm/deals"));
const DealDetailPage = lazy(() => import("@/pages/crm/deal-detail"));
const ActivitiesPage = lazy(() => import("@/pages/crm/activities"));
const CampaignsPage = lazy(() => import("@/pages/crm/campaigns"));
const CampaignDetailPage = lazy(() => import("@/pages/crm/campaign-detail"));
// Chart-heavy: recharts stays out of every other chunk.
const ReportsPage = lazy(() => import("@/pages/crm/reports"));
const PipelinesPage = lazy(() => import("@/pages/settings/pipelines"));
const WorkflowsPage = lazy(() => import("@/pages/settings/workflows"));
const ApprovalsPage = lazy(() => import("@/pages/approvals"));

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
          {/* Forced full-page password change (no shell): users with a temporary password land here. */}
          <Route
            path={CHANGE_PASSWORD_PATH}
            element={
              <ProtectedRoute allowPasswordChange>
                <ForcedChangePasswordPage />
              </ProtectedRoute>
            }
          />

          <Route
            path="/app"
            element={
              <ProtectedRoute>
                <AppLayout />
              </ProtectedRoute>
            }
          >
            <Route index element={<HomePage />} />
            <Route
              path="leads"
              element={
                <RequirePermission permission={PERMISSIONS.crmLeadsRead}>
                  <LeadsPage />
                </RequirePermission>
              }
            />
            <Route
              path="leads/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmLeadsRead}>
                  <LeadDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="contacts"
              element={
                <RequirePermission permission={PERMISSIONS.crmContactsRead}>
                  <ContactsPage />
                </RequirePermission>
              }
            />
            <Route
              path="contacts/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmContactsRead}>
                  <ContactDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="accounts"
              element={
                <RequirePermission permission={PERMISSIONS.crmAccountsRead}>
                  <AccountsPage />
                </RequirePermission>
              }
            />
            <Route
              path="accounts/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmAccountsRead}>
                  <AccountDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="deals"
              element={
                <RequirePermission permission={PERMISSIONS.crmDealsRead}>
                  <DealsPage />
                </RequirePermission>
              }
            />
            <Route
              path="deals/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmDealsRead}>
                  <DealDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="campaigns"
              element={
                <RequirePermission permission={PERMISSIONS.crmCampaignsRead}>
                  <CampaignsPage />
                </RequirePermission>
              }
            />
            <Route
              path="campaigns/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmCampaignsRead}>
                  <CampaignDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="activities"
              element={
                <RequirePermission permission={PERMISSIONS.crmActivitiesRead}>
                  <ActivitiesPage />
                </RequirePermission>
              }
            />
            {/* "mine=true" needs no permission: anyone can open the page, deciding needs crm.approvals.decide. */}
            <Route path="approvals" element={<ApprovalsPage />} />
            <Route
              path="reports"
              element={
                <RequirePermission permission={PERMISSIONS.crmReportsRead}>
                  <ReportsPage />
                </RequirePermission>
              }
            />
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
              path="settings/pipelines"
              element={
                <RequirePermission permission={PERMISSIONS.crmDealsRead}>
                  <PipelinesPage />
                </RequirePermission>
              }
            />
            <Route
              path="settings/workflows"
              element={
                <RequirePermission permission={PERMISSIONS.orgWorkflowsManage}>
                  <WorkflowsPage />
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
