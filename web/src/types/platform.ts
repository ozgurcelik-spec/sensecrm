/**
 * Milestone 7 API types (SaaS readiness: plans, tenant lifecycle, platform console, subscription
 * and onboarding) - see docs/plan/m7-saas-hazirlik.md. JSON is camelCase, null fields are absent
 * from responses and "date only" fields are `YYYY-MM-DD`.
 */

/** Modules a plan can switch off (`identity`, `sales` and `activities` are core and always on). */
export const GATED_MODULES = ["workflows", "commerce", "service", "marketing"] as const;
export type GatedModule = (typeof GATED_MODULES)[number];

/** Modules that count records against `maxRecords.<module>`. */
export const RECORD_MODULES = [
  "sales",
  "activities",
  "workflows",
  "commerce",
  "service",
  "marketing",
] as const;
export type RecordModule = (typeof RECORD_MODULES)[number];

/** Effective tenant status (derived on the server from the stored status, suspension and trial end). */
export const TENANT_STATUSES = [
  "trial",
  "active",
  "trial_expired",
  "suspended",
  "pending_deletion",
  "deleted",
] as const;
export type PlatformTenantStatus = (typeof TENANT_STATUSES)[number];

/** `readOnly`: reads work, user-driven writes are refused. `none`: everything but `/me` is refused. */
export type PlatformAccessLevel = "full" | "readOnly" | "none";

export const ACCOUNT_SOURCES = ["signup", "platform", "bootstrap", "backfill", "lazy"] as const;
export type PlatformAccountSource = (typeof ACCOUNT_SOURCES)[number];

export type PlatformSuspensionMode = "readOnly" | "blocked";

/** `GET /me` `subscription`; absent on servers without the platform module (= everything on, full access). */
export interface MeSubscription {
  planCode: string;
  planName: string;
  status: PlatformTenantStatus;
  accessLevel: PlatformAccessLevel;
  trialEndsOn?: string;
  /** Only while in trial; 0 = the last day. */
  trialDaysLeft?: number;
  modules: Partial<Record<GatedModule, boolean>>;
}

// ---- Platform console (`/platform/**`, platform admins only) ---------------------------------------

export interface PlatformOrganizationUsage {
  day: string;
  usersActive: number;
  usersPending: number;
  records: Record<string, number>;
}

export interface PlatformOrganization {
  tenantId: string;
  name: string;
  slug: string;
  planCode: string;
  planName: string;
  source: PlatformAccountSource;
  status: PlatformTenantStatus;
  accessLevel: PlatformAccessLevel;
  trialEndsOn?: string;
  isSystem: boolean;
  createdAt: string;
  usage?: PlatformOrganizationUsage;
}

/** Effective limits (plan + overrides): only finite limits are present (absent = unlimited). */
export interface PlanLimits {
  maxUsers?: number;
  /** Storage quota in MB (M8C); absent = unlimited. */
  maxStorageMb?: number;
  maxRecords: Record<string, number>;
  modules: Partial<Record<GatedModule, boolean>>;
}

export interface PlatformSuspension {
  mode: PlatformSuspensionMode;
  reason?: string;
  at?: string;
}

export const DELETION_STATUSES = [
  "scheduled",
  "cancelled",
  "running",
  "completed",
  "failed",
] as const;
export type PlatformDeletionStatus = (typeof DELETION_STATUSES)[number];

export interface PlatformDeletion {
  requestId: string;
  status: PlatformDeletionStatus;
  requestedAt: string;
  requestedByEmail?: string;
  reason: string;
  retentionDays: number;
  scheduledFor: string;
  cancelledAt?: string;
  completedAt?: string;
  attempts: number;
  lastError?: string;
}

/** Tenant specific exceptions; every key is optional. `maxUsers: null` means explicitly unlimited. */
export interface PlatformOverrides {
  maxUsers?: number | null;
  /** Storage quota in MB (M8C): absent key = plan, `null` = unlimited. */
  maxStorageMb?: number | null;
  maxRecords?: Record<string, number | null>;
  modules?: Partial<Record<GatedModule, boolean>>;
}

export interface PlatformOrganizationDetail extends PlatformOrganization {
  limits: PlanLimits;
  overrides?: PlatformOverrides;
  planChangedAt?: string;
  suspension?: PlatformSuspension;
  deletion?: PlatformDeletion;
}

export interface PlatformOverLimit {
  limit: "users" | "records" | "storage";
  module?: string;
  max: number;
  used: number;
}

export interface PlatformSubscriptionResult {
  overLimit: PlatformOverLimit[];
}

export interface PlatformSubscriptionInput {
  planCode: string;
  trialEndsOn?: string;
  overrides?: PlatformOverrides;
}

export interface CreatePlatformOrganizationInput {
  organizationName: string;
  adminDisplayName: string;
  adminEmail: string;
  locale: "tr" | "en";
  planCode?: string;
  trialEndsOn?: string;
}

export interface CreatedPlatformOrganization {
  organizationId: string;
  name: string;
  slug: string;
  adminUserId: string;
  adminEmail: string;
  adminAccountCreated: boolean;
  /** Only when the account was created without a given password; never returned again. */
  generatedPassword?: string;
  adminInvitationPending?: boolean;
  planCode?: string;
}

export interface PlatformPlan {
  code: string;
  name: string;
  description?: string;
  isActive: boolean;
  sortOrder: number;
  trialDays?: number;
  limits: {
    maxUsers?: number | null;
    maxStorageMb?: number | null;
    /** M8A: `null` = platform default, `0` = no e-mail. */
    maxEmailsPerDay?: number | null;
    maxRecords: Record<string, number | null>;
  };
  modules: Partial<Record<GatedModule, boolean>>;
  /** M8A plan flags (`notifications.email`, `notifications.sms`); a missing key means off. */
  features?: Record<string, boolean>;
  assignedCount: number;
}

export interface PlatformSuspendInput {
  reason: string;
  mode: PlatformSuspensionMode;
}

export interface PlatformDeletionInput {
  reason: string;
  retentionDays?: number;
}

export interface PlatformDeletionResult {
  requestId: string;
  scheduledFor: string;
}

export interface PlatformUsageDay {
  day: string;
  usersActive: number;
  usersPending: number;
  metrics: Record<string, number>;
}

export interface PlatformAuditEntry {
  id: string;
  occurredAt: string;
  action: string;
  actorUserId?: string;
  actorEmail?: string;
  targetTenantId?: string;
  targetTenantName?: string;
  details: Record<string, unknown>;
  correlationId?: string;
}

// ---- Tenant side ("Plan ve kullanım", onboarding) ---------------------------------------------------

export interface SubscriptionInfo {
  planCode: string;
  planName: string;
  status: PlatformTenantStatus;
  accessLevel: PlatformAccessLevel;
  trialEndsOn?: string;
  trialDaysLeft?: number;
  modules: Partial<Record<GatedModule, boolean>>;
  /** Only finite limits are present. */
  limits: { maxUsers?: number; maxStorageMb?: number; maxRecords: Record<string, number> };
  usage: {
    asOf: string;
    users: number;
    pendingUsers: number;
    records: Record<string, number>;
    /** M8C; absent on servers without the files module. */
    storageBytes?: number;
    fileCount?: number;
  };
  overLimit: PlatformOverLimit[];
}

export const ONBOARDING_KEYS = [
  "profile",
  "invite_user",
  "create_lead",
  "create_workflow_rule",
] as const;

export interface OnboardingItem {
  key: string;
  done: boolean;
}

export interface OnboardingState {
  dismissed: boolean;
  completedCount: number;
  totalCount: number;
  items: OnboardingItem[];
}
