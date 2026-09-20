import type { LucideIcon } from "lucide-react";
import {
  BarChart3,
  Bell,
  Building2,
  CalendarCheck,
  ClipboardCheck,
  Contact,
  Gauge,
  FileText,
  Handshake,
  Home,
  Receipt,
  Store,
  Tags,
  Truck,
  Layers,
  Megaphone,
  Package,
  Plug,
  LifeBuoy,
  ScrollText,
  ShieldCheck,
  ShoppingCart,
  Target,
  Timer,
  Users,
  Workflow,
  Zap,
} from "lucide-react";
import { PERMISSIONS, type GatedModule } from "@/types";

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
  /** Hidden while the plan switches this module off (`me.subscription.modules`). */
  module?: GatedModule;
  /** Only platform admins (`me.user.isPlatformAdmin`) see the item. */
  platformAdminOnly?: boolean;
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
    key: "campaigns",
    labelKey: "campaigns",
    module: "marketing",
    path: "/app/campaigns",
    icon: Megaphone,
    permissions: [PERMISSIONS.crmCampaignsRead],
  },
  {
    key: "products",
    labelKey: "products",
    module: "commerce",
    path: "/app/products",
    icon: Package,
    permissions: [PERMISSIONS.crmProductsRead],
  },
  {
    key: "quotes",
    labelKey: "quotes",
    module: "commerce",
    path: "/app/quotes",
    icon: FileText,
    permissions: [PERMISSIONS.crmQuotesRead],
  },
  {
    key: "orders",
    labelKey: "orders",
    module: "commerce",
    path: "/app/orders",
    icon: ShoppingCart,
    permissions: [PERMISSIONS.crmOrdersRead],
  },
  // M9C "Envanter": price books, purchase orders, invoices, vendors (each behind its own read permission).
  {
    key: "pricebooks",
    labelKey: "pricebooks",
    module: "commerce",
    path: "/app/pricebooks",
    icon: Tags,
    permissions: [PERMISSIONS.crmPriceBooksRead],
  },
  {
    key: "purchaseOrders",
    labelKey: "purchaseOrders",
    module: "commerce",
    path: "/app/purchase-orders",
    icon: Truck,
    permissions: [PERMISSIONS.crmPurchaseOrdersRead],
  },
  {
    key: "invoices",
    labelKey: "invoices",
    module: "commerce",
    path: "/app/invoices",
    icon: Receipt,
    permissions: [PERMISSIONS.crmInvoicesRead],
  },
  {
    key: "vendors",
    labelKey: "vendors",
    module: "commerce",
    path: "/app/vendors",
    icon: Store,
    permissions: [PERMISSIONS.crmVendorsRead],
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
    module: "service",
    path: "/app/cases",
    icon: LifeBuoy,
    permissions: [PERMISSIONS.crmCasesRead],
  },
  {
    key: "approvals",
    labelKey: "approvals",
    module: "workflows",
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
    module: "workflows",
    path: "/app/settings/workflows",
    icon: Zap,
    permissions: [PERMISSIONS.orgWorkflowsManage],
  },
  {
    key: "sla",
    labelKey: "sla",
    module: "service",
    path: "/app/settings/sla",
    icon: Timer,
    permissions: [PERMISSIONS.orgSettingsManage],
  },
  {
    key: "plan",
    labelKey: "planUsage",
    path: "/app/settings/plan",
    icon: Gauge,
    permissions: [PERMISSIONS.orgSettingsManage],
  },
  {
    key: "notifications",
    labelKey: "notificationsSettings",
    path: "/app/settings/notifications",
    icon: Bell,
    permissions: [PERMISSIONS.orgNotificationsManage],
  },
  {
    key: "integrations",
    labelKey: "integrations",
    module: "integrations",
    path: "/app/settings/integrations",
    icon: Plug,
    permissions: [PERMISSIONS.orgIntegrationsManage],
  },
  {
    key: "audit",
    labelKey: "audit",
    path: "/app/settings/audit",
    icon: ScrollText,
    permissions: [PERMISSIONS.orgAuditRead],
  },
];

/** Platform console (platform admins only): a separate "Platform" group in the navigation. */
export const PLATFORM_ITEMS: readonly NavItem[] = [
  {
    key: "platformOrganizations",
    labelKey: "platformOrganizations",
    path: "/app/platform/organizations",
    icon: Building2,
    platformAdminOnly: true,
  },
  {
    key: "platformPlans",
    labelKey: "platformPlans",
    path: "/app/platform/plans",
    icon: Layers,
    platformAdminOnly: true,
  },
  {
    key: "platformAudit",
    labelKey: "platformAudit",
    path: "/app/platform/audit",
    icon: ScrollText,
    platformAdminOnly: true,
  },
  {
    key: "platformAdmins",
    labelKey: "platformAdmins",
    path: "/app/platform/admins",
    icon: ShieldCheck,
    platformAdminOnly: true,
  },
];

/** CRM modules shown in quick links (everything except the home entry). */
export const CRM_MODULE_ITEMS = NAV_ITEMS.filter((item) => item.key !== "home");
