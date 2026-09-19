import type { LucideIcon } from "lucide-react";
import {
  BarChart3,
  Building2,
  CalendarCheck,
  Contact,
  Handshake,
  Home,
  ScrollText,
  ShieldCheck,
  Target,
  Users,
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
  /** Match the path exactly (index routes). */
  end?: boolean;
}

/** CRM modules in the left navigation (Zoho order). CRM entries are placeholders until Milestone 2. */
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
    key: "audit",
    labelKey: "audit",
    path: "/app/settings/audit",
    icon: ScrollText,
    permissions: [PERMISSIONS.orgAuditRead],
  },
];

/** CRM module keys that render the "coming soon" placeholder for now. */
export const CRM_MODULE_ITEMS = NAV_ITEMS.filter((item) => item.key !== "home");
