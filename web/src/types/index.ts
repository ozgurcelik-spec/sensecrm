/**
 * Shared API types. JSON is camelCase (see the backend contract in docs/architecture/kararlar.md, K6-K8).
 */

export type Locale = "tr" | "en";

export interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  /** ISO timestamp of the access token expiry. */
  expiresAt: string;
}

export interface OrganizationSummary {
  id: string;
  name: string;
  slug: string;
}

export interface Organization extends OrganizationSummary {
  defaultLocale: Locale;
  timeZone: string;
}

export interface MeUser {
  id: string;
  email: string;
  displayName: string;
  locale: Locale;
  isPlatformAdmin: boolean;
}

export interface Me {
  user: MeUser;
  organization: Organization;
  role: { id: string; name: string };
  permissions: string[];
  organizations: OrganizationSummary[];
}

export type PermissionGroup = "org" | "crm";

export interface Permission {
  key: string;
  group: PermissionGroup;
}

export interface Member {
  userId: string;
  email: string;
  displayName: string;
  roleId: string;
  roleName: string;
  isActive: boolean;
  joinedAt: string;
}

export interface Role {
  id: string;
  name: string;
  isSystem: boolean;
  permissions: string[];
  memberCount: number;
}

export type AuditAction = "created" | "updated" | "deleted";

export interface AuditEntry {
  id: string;
  entityType: string;
  entityId: string;
  action: AuditAction;
  userId?: string | null;
  userDisplayName?: string | null;
  changes?: Record<string, unknown> | null;
  occurredAt: string;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
}

/** Permission keys known to the web app (GET /api/permissions is the source of truth for the role editor). */
export const PERMISSIONS = {
  orgSettingsManage: "org.settings.manage",
  orgUsersRead: "org.users.read",
  orgUsersManage: "org.users.manage",
  orgRolesManage: "org.roles.manage",
  orgAuditRead: "org.audit.read",
  crmAccountsRead: "crm.accounts.read",
  crmAccountsWrite: "crm.accounts.write",
  crmContactsRead: "crm.contacts.read",
  crmContactsWrite: "crm.contacts.write",
  crmLeadsRead: "crm.leads.read",
  crmLeadsWrite: "crm.leads.write",
  crmDealsRead: "crm.deals.read",
  crmDealsWrite: "crm.deals.write",
  crmActivitiesRead: "crm.activities.read",
  crmActivitiesWrite: "crm.activities.write",
  crmReportsRead: "crm.reports.read",
  orgWorkflowsManage: "org.workflows.manage",
  crmApprovalsDecide: "crm.approvals.decide",
  crmProductsRead: "crm.products.read",
  crmProductsWrite: "crm.products.write",
  crmQuotesRead: "crm.quotes.read",
  crmQuotesWrite: "crm.quotes.write",
  crmOrdersRead: "crm.orders.read",
  crmOrdersWrite: "crm.orders.write",
} as const;

/** Milestone 2 list envelope (`GET /accounts`, `/contacts`, `/leads`, `/deals`). */
export interface ListResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}
export * from "./crm";
export * from "./activities";
export * from "./workflows";
export * from "./commerce";
