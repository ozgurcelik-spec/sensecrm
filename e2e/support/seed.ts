/**
 * Seeding helpers built on ApiClient. Every helper creates NEW data with a unique suffix, so tests never share mutable state and can
 * run in parallel against the same stack.
 */
import { ApiClient, ApiError, type Credentials } from "./api.ts";

let counter = 0;

/** Short unique token: random + counter + time, safe in e-mail addresses, names and slugs. */
export function uid(): string {
  counter += 1;
  const random = Math.random().toString(36).slice(2, 7);
  return `${random}${counter.toString(36)}${Date.now().toString(36).slice(-4)}`;
}

/** Meets the server policy (10-128 chars, not a common password, does not contain the e-mail local part). */
export function strongPassword(): string {
  return `Qz!${uid()}-Vt7#mNpx`;
}

/** Polls a condition on the API side (state that the server converges to asynchronously). Not a fixed sleep: it returns as soon as true. */
export async function waitFor(condition: () => Promise<boolean>, what: string, timeoutMs = 30_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (let delay = 100; ; delay = Math.min(delay * 1.5, 1000)) {
    if (await condition()) return;
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}`);
    await new Promise((resolve) => setTimeout(resolve, delay));
  }
}

export type PlanCode = "internal" | "starter" | "business" | "enterprise";

export interface Role {
  id: string;
  name: string;
  isSystem: boolean;
  permissions: string[];
}

export interface Tenant {
  id: string;
  name: string;
  slug: string;
  planCode: PlanCode;
  /** The organization administrator, already past the forced password change. */
  admin: ApiClient;
  adminEmail: string;
  adminPassword: string;
  roles: { administrator: Role; standard: Role };
  /** Default pipeline stages by name (Qualification, Needs Analysis, Proposal, Negotiation, Closed Won, Closed Lost). */
  stages: Record<string, string>;
  pipelineId: string;
}

export interface CreateTenantOptions {
  plan?: PlanCode;
  locale?: "tr" | "en";
  namePrefix?: string;
}

interface CreatedOrganization {
  organizationId: string;
  name: string;
  slug: string;
  adminEmail: string;
  generatedPassword?: string;
}

/**
 * Creates an organization through the platform console API, completes the forced first-password change of its administrator and
 * returns a ready-to-use tenant. `plan` defaults to "enterprise" (every module on, 250 users) so feature tests are not limited.
 */
export async function createTenant(platform: ApiClient, options: CreateTenantOptions = {}): Promise<Tenant> {
  const suffix = uid();
  const plan = options.plan ?? "enterprise";
  const created = await platform.post<CreatedOrganization>("/platform/organizations", {
    organizationName: `${options.namePrefix ?? "E2E Org"} ${suffix}`,
    adminDisplayName: `Admin ${suffix}`,
    adminEmail: `admin.${suffix}@e2e.test`,
    locale: options.locale ?? "en",
    planCode: plan,
  });
  if (!created.generatedPassword) throw new Error("the platform API did not return a temporary password");

  // The platform account (with the requested plan) is created asynchronously by the outbox, normally within ~2 s. If the administrator
  // calls the API before that, the tenant is provisioned lazily on the default "starter" trial plan and the outbox handler then fails on
  // a duplicate row and retries (README, Bulgular F-1): wait for the account first, so tests always start on the plan they asked for.
  await waitFor(async () => {
    try {
      await platform.get(`/platform/organizations/${created.organizationId}`);
      return true;
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return false;
      throw error;
    }
  }, `platform account of organization ${created.organizationId}`);

  const admin = new ApiClient({ email: created.adminEmail, password: created.generatedPassword });
  const adminPassword = strongPassword();
  const changed = await admin.post<{ accessToken: string; refreshToken: string; expiresAt: string }>("/me/password", {
    currentPassword: created.generatedPassword,
    newPassword: adminPassword,
  });
  admin.setPassword(adminPassword, changed);

  await waitFor(
    async () => (await admin.get<{ subscription?: { planCode: string } }>("/me")).subscription?.planCode === plan,
    `/me of organization ${created.organizationId} to report plan ${plan}`,
  );

  const roles = await admin.get<Role[]>("/organization/roles");
  const administrator = roles.find((r) => r.name === "Administrator");
  const standard = roles.find((r) => r.name === "Standard");
  if (!administrator || !standard) throw new Error("system roles missing");

  const pipelines = await admin.get<{ id: string; stages: { id: string; name: string }[] }[]>("/pipelines");
  const pipeline = pipelines[0];
  if (!pipeline) throw new Error("default pipeline missing");

  return {
    id: created.organizationId,
    name: created.name,
    slug: created.slug,
    planCode: plan,
    admin,
    adminEmail: created.adminEmail,
    adminPassword,
    roles: { administrator, standard },
    stages: Object.fromEntries(pipeline.stages.map((s) => [s.name, s.id])),
    pipelineId: pipeline.id,
  };
}

export interface Member {
  email: string;
  password: string;
  userId: string;
  api: ApiClient;
}

/**
 * Adds a user to the tenant with the given role. `finalize` (default) completes the forced password change so the user can be used
 * right away; pass false to keep the temporary password (forced-change test).
 */
export async function addMember(
  tenant: Tenant,
  role: Role,
  options: { finalize?: boolean; label?: string } = {},
): Promise<Member & { temporaryPassword: string }> {
  const suffix = uid();
  const email = `${options.label ?? "user"}.${suffix}@e2e.test`;
  const created = await tenant.admin.post<{ userId: string; temporaryPassword?: string }>("/organization/members", {
    email,
    displayName: `${options.label ?? "User"} ${suffix}`,
    roleId: role.id,
  });
  if (!created.temporaryPassword) throw new Error("no temporary password returned for a new member");
  const credentials: Credentials = { email, password: created.temporaryPassword };
  const api = new ApiClient(credentials);
  let password = created.temporaryPassword;
  if (options.finalize !== false) {
    password = strongPassword();
    const changed = await api.post<{ accessToken: string; refreshToken: string; expiresAt: string }>("/me/password", {
      currentPassword: created.temporaryPassword,
      newPassword: password,
    });
    api.setPassword(password, changed);
  }
  return { email, password, userId: created.userId, api, temporaryPassword: created.temporaryPassword };
}

export async function createAccount(api: ApiClient, name = `Account ${uid()}`): Promise<{ id: string; name: string }> {
  const account = await api.post<{ id: string }>("/accounts", { name });
  return { id: account.id, name };
}

export async function createContact(
  api: ApiClient,
  accountId: string,
  lastName = `Contact${uid()}`,
): Promise<{ id: string; lastName: string }> {
  const contact = await api.post<{ id: string }>("/contacts", { firstName: "Test", lastName, accountId });
  return { id: contact.id, lastName };
}

export async function createLead(
  api: ApiClient,
  fields: { lastName?: string; company?: string; email?: string } = {},
): Promise<{ id: string; lastName: string; company: string }> {
  const lastName = fields.lastName ?? `Lead${uid()}`;
  const company = fields.company ?? `Company ${uid()}`;
  const lead = await api.post<{ id: string }>("/leads", { firstName: "Test", lastName, company, source: "web", email: fields.email });
  return { id: lead.id, lastName, company };
}

export async function createDeal(
  tenant: Tenant,
  accountId: string,
  fields: { name?: string; amount?: number; stage?: string } = {},
): Promise<{ id: string; name: string }> {
  const name = fields.name ?? `Deal ${uid()}`;
  const deal = await tenant.admin.post<{ id: string }>("/deals", {
    name,
    accountId,
    amount: fields.amount ?? 1000,
    currency: "TRY",
    pipelineId: tenant.pipelineId,
    stageId: tenant.stages[fields.stage ?? "Qualification"],
  });
  return { id: deal.id, name };
}
