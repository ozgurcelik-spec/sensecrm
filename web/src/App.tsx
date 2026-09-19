import { lazy, useEffect, type ReactNode } from "react";
import { BrowserRouter as Router, Navigate, Route, Routes } from "react-router";
import { useTranslation } from "react-i18next";
import NoAccess from "@/components/no-access";
import { PermissionGuard } from "@/components/permission-guard";
import ProtectedRoute from "@/components/protected-route";
import RouteBoundary from "@/components/route-boundary";
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
const LeadsPage = lazy(() => import("@/pages/crm/leads"));
const LeadDetailPage = lazy(() => import("@/pages/crm/lead-detail"));
const ContactsPage = lazy(() => import("@/pages/crm/contacts"));
const ContactDetailPage = lazy(() => import("@/pages/crm/contact-detail"));
const AccountsPage = lazy(() => import("@/pages/crm/accounts"));
const AccountDetailPage = lazy(() => import("@/pages/crm/account-detail"));
const DealsPage = lazy(() => import("@/pages/crm/deals"));
const DealDetailPage = lazy(() => import("@/pages/crm/deal-detail"));
const ActivitiesPage = lazy(() => import("@/pages/crm/activities"));
const ProductsPage = lazy(() => import("@/pages/commerce/products"));
const QuotesPage = lazy(() => import("@/pages/commerce/quotes"));
const QuoteDetailPage = lazy(() => import("@/pages/commerce/quote-detail"));
const QuoteEditorPage = lazy(() => import("@/pages/commerce/quote-editor"));
const OrdersPage = lazy(() => import("@/pages/commerce/orders"));
const OrderDetailPage = lazy(() => import("@/pages/commerce/order-detail"));
const OrderEditorPage = lazy(() => import("@/pages/commerce/order-editor"));
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
              path="products"
              element={
                <RequirePermission permission={PERMISSIONS.crmProductsRead}>
                  <ProductsPage />
                </RequirePermission>
              }
            />
            <Route
              path="quotes"
              element={
                <RequirePermission permission={PERMISSIONS.crmQuotesRead}>
                  <QuotesPage />
                </RequirePermission>
              }
            />
            {/* The editors only exist for writers: reading alone gets NoAccess, not a form the server would refuse. */}
            <Route
              path="quotes/new"
              element={
                <RequirePermission permission={PERMISSIONS.crmQuotesWrite}>
                  <QuoteEditorPage />
                </RequirePermission>
              }
            />
            <Route
              path="quotes/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmQuotesRead}>
                  <QuoteDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="quotes/:id/edit"
              element={
                <RequirePermission permission={PERMISSIONS.crmQuotesWrite}>
                  <QuoteEditorPage />
                </RequirePermission>
              }
            />
            <Route
              path="orders"
              element={
                <RequirePermission permission={PERMISSIONS.crmOrdersRead}>
                  <OrdersPage />
                </RequirePermission>
              }
            />
            <Route
              path="orders/new"
              element={
                <RequirePermission permission={PERMISSIONS.crmOrdersWrite}>
                  <OrderEditorPage />
                </RequirePermission>
              }
            />
            <Route
              path="orders/:id"
              element={
                <RequirePermission permission={PERMISSIONS.crmOrdersRead}>
                  <OrderDetailPage />
                </RequirePermission>
              }
            />
            <Route
              path="orders/:id/edit"
              element={
                <RequirePermission permission={PERMISSIONS.crmOrdersWrite}>
                  <OrderEditorPage />
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
