import type { LucideIcon } from "lucide-react";
import {
  BarChart3,
  Building2,
  CalendarCheck,
  ClipboardCheck,
  Contact,
  Handshake,
  Home,
  LifeBuoy,
  ScrollText,
  ShieldCheck,
  Target,
  Timer,
  Users,
  Workflow,
  Zap,
} from "lucide-react";
import { PERMISSIONS } from "@/types";

export interface NavItem {
  key: string;
  /** i18n key in the "navigation" namespace. */
  labelKey: string;
  path: string;
  icon: LucideIcon;
  /** At least one of these permissions is required to see the item (empty = always visible). */
  permissions?: string[];
  /** Also visible without the permissions while this flag (see `useVisibleItems`) is true. */
  visibleWhen?: string;
  /** Match the path exactly (index routes). */
  end?: boolean;
}

/** CRM modules in the left navigation (Zoho order). */
export const NAV_ITEMS: readonly NavItem[] = [
  { key: "home", labelKey: "home", path: "/app", icon: Home, end: true },
  {
    key: "leads",
    labelKey: "leads",
    path: "/app/leads",
    icon: Target,
    permissions: [PERMISSIONS.crmLeadsRead],
  },
  {
    key: "contacts",
    labelKey: "contacts",
    path: "/app/contacts",
    icon: Contact,
    permissions: [PERMISSIONS.crmContactsRead],
  },
  {
    key: "accounts",
    labelKey: "accounts",
    path: "/app/accounts",
    icon: Building2,
    permissions: [PERMISSIONS.crmAccountsRead],
  },
  {
    key: "deals",
    labelKey: "deals",
    path: "/app/deals",
    icon: Handshake,
    permissions: [PERMISSIONS.crmDealsRead],
  },
  {
    key: "activities",
    labelKey: "activities",
    path: "/app/activities",
    icon: CalendarCheck,
    permissions: [PERMISSIONS.crmActivitiesRead],
  },
  {
    key: "cases",
    labelKey: "cases",
    path: "/app/cases",
    icon: LifeBuoy,
    permissions: [PERMISSIONS.crmCasesRead],
  },
  {
    key: "approvals",
    labelKey: "approvals",
    path: "/app/approvals",
    icon: ClipboardCheck,
    // Visible to approvers and to anyone who has a pending approval (mine=true needs no permission).
    permissions: [PERMISSIONS.crmApprovalsDecide],
    visibleWhen: "pendingApprovals",
  },
  {
    key: "reports",
    labelKey: "reports",
    path: "/app/reports",
    icon: BarChart3,
    permissions: [PERMISSIONS.crmReportsRead],
  },
];

/** Settings (Ayarlar) section. */
export const SETTINGS_ITEMS: readonly NavItem[] = [
  {
    key: "organization",
    labelKey: "organization",
    path: "/app/settings/organization",
    icon: Building2,
  },
  {
    key: "users",
    labelKey: "users",
    path: "/app/settings/users",
    icon: Users,
    permissions: [PERMISSIONS.orgUsersRead],
  },
  {
    key: "roles",
    labelKey: "roles",
    path: "/app/settings/roles",
    icon: ShieldCheck,
    permissions: [PERMISSIONS.orgUsersRead],
  },
  {
    key: "pipelines",
    labelKey: "pipelines",
    path: "/app/settings/pipelines",
    icon: Workflow,
    permissions: [PERMISSIONS.crmDealsRead],
  },
  {
    key: "workflows",
    labelKey: "workflows",
    path: "/app/settings/workflows",
    icon: Zap,
    permissions: [PERMISSIONS.orgWorkflowsManage],
  },
  {
    key: "sla",
    labelKey: "sla",
    path: "/app/settings/sla",
    icon: Timer,
    permissions: [PERMISSIONS.orgSettingsManage],
  },
  {
    key: "audit",
    labelKey: "audit",
    path: "/app/settings/audit",
    icon: ScrollText,
    permissions: [PERMISSIONS.orgAuditRead],
  },
];

/** CRM modules shown in quick links (everything except the home entry). */
export const CRM_MODULE_ITEMS = NAV_ITEMS.filter((item) => item.key !== "home");
