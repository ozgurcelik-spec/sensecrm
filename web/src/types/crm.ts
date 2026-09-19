/**
 * Milestone 2 (sales core) API types - see docs/plan/m2-api-kontrat.md. JSON is camelCase, enums
 * are camelCase strings and null fields are absent from responses.
 */

export interface Address {
  street?: string;
  city?: string;
  state?: string;
  postalCode?: string;
  country?: string;
}

interface RecordBase {
  id: string;
  createdAt: string;
  updatedAt?: string;
  ownerUserId: string;
  ownerName?: string;
}

export interface Account extends RecordBase {
  name: string;
  industry?: string;
  website?: string;
  phone?: string;
  email?: string;
  billingAddress?: Address;
  description?: string;
  /** Detail response only. */
  contactCount?: number;
  dealCount?: number;
}

export interface AccountInput {
  name: string;
  industry?: string;
  website?: string;
  phone?: string;
  email?: string;
  billingAddress?: Address;
  description?: string;
  ownerUserId?: string;
}

export interface Contact extends RecordBase {
  firstName?: string;
  lastName: string;
  fullName: string;
  email?: string;
  phone?: string;
  mobile?: string;
  title?: string;
  accountId?: string;
  accountName?: string;
  mailingAddress?: Address;
}

export interface ContactInput {
  firstName?: string;
  lastName: string;
  email?: string;
  phone?: string;
  mobile?: string;
  title?: string;
  accountId?: string;
  mailingAddress?: Address;
  ownerUserId?: string;
}

export const LEAD_SOURCES = ["web", "referral", "campaign", "coldCall", "other"] as const;
export type LeadSource = (typeof LEAD_SOURCES)[number];

export const LEAD_STATUSES = ["new", "contacted", "qualified", "unqualified", "converted"] as const;
export type LeadStatus = (typeof LEAD_STATUSES)[number];

export const LEAD_RATINGS = ["hot", "warm", "cold"] as const;
export type LeadRating = (typeof LEAD_RATINGS)[number];

export interface Lead extends RecordBase {
  firstName?: string;
  lastName: string;
  fullName: string;
  company: string;
  email?: string;
  phone?: string;
  source: LeadSource;
  status: LeadStatus;
  rating?: LeadRating;
  convertedAccountId?: string;
  convertedContactId?: string;
  convertedDealId?: string;
  convertedAt?: string;
}

export interface LeadInput {
  firstName?: string;
  lastName: string;
  company: string;
  email?: string;
  phone?: string;
  source: LeadSource;
  /** Sent on update only (creation always starts as `new`). */
  status?: LeadStatus;
  rating?: LeadRating;
  ownerUserId?: string;
}

export interface ConvertLeadInput {
  accountId?: string;
  createDeal: boolean;
  dealName?: string;
  amount?: number;
  closingDate?: string;
  pipelineId?: string;
}

export interface ConvertLeadResult {
  accountId: string;
  contactId: string;
  dealId?: string;
}

export const STAGE_KINDS = ["open", "won", "lost"] as const;
export type StageKind = (typeof STAGE_KINDS)[number];

export interface PipelineStage {
  id: string;
  name: string;
  order: number;
  probability: number;
  kind: StageKind;
}

export interface Pipeline {
  id: string;
  name: string;
  isDefault: boolean;
  stages: PipelineStage[];
}

export interface StageInput {
  id?: string;
  name: string;
  probability: number;
  kind: StageKind;
}

export interface Deal extends RecordBase {
  name: string;
  accountId: string;
  accountName: string;
  contactId?: string;
  contactName?: string;
  pipelineId: string;
  pipelineName: string;
  stageId: string;
  stageName: string;
  stageKind: StageKind;
  probability: number;
  amount?: number;
  currency: string;
  closingDate?: string;
  lostReason?: string;
}

export interface DealInput {
  name: string;
  accountId: string;
  contactId?: string;
  pipelineId?: string;
  stageId?: string;
  amount?: number;
  currency: string;
  closingDate?: string;
  ownerUserId?: string;
  lostReason?: string;
}

export interface DealSummary {
  id: string;
  name: string;
  accountName: string;
  amount?: number;
  currency: string;
  closingDate?: string;
  ownerName?: string;
}

export interface BoardStage {
  id: string;
  name: string;
  kind: StageKind;
  probability: number;
  totalAmount: number;
  count: number;
  deals: DealSummary[];
}

export interface DealBoard {
  pipelineId: string;
  stages: BoardStage[];
}

/** Audit `changes` are `{ field: { old, new } }`; rendering lives in components/audit. */
export interface RecordAuditEntry {
  id: string;
  entityType: string;
  entityId: string;
  action: "created" | "updated" | "deleted";
  userId?: string | null;
  userDisplayName?: string | null;
  changes?: Record<string, unknown> | null;
  occurredAt: string;
}

export type AuditEntityType = "Account" | "Contact" | "Lead" | "Deal" | "Pipeline";
