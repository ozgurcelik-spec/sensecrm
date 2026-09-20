import { test as base, expect, type BrowserContext, type Page } from "@playwright/test";
import { ApiClient, rawLogin } from "./api.ts";
import { platformAdmin } from "./env.ts";
import { createTenant, type CreateTenantOptions, type Tenant } from "./seed.ts";

/**
 * Puts a signed-in session into the browser context BEFORE the app boots, the way a returning user would have it: the two tokens
 * plus the persisted profile of the auth store (so the router does not bounce through /login while /me loads).
 * The seed runs once per tab (sessionStorage flag), so a later logout is not undone by a page reload.
 */
export async function signIn(context: BrowserContext, user: ApiClient, locale: "tr" | "en" = "en"): Promise<void> {
  const tokens = await rawLogin({ email: user.email, password: user.password });
  const me = await user.get<{ mustChangePassword?: boolean }>("/me");
  const seed = {
    access: tokens.accessToken,
    refresh: tokens.refreshToken,
    store: JSON.stringify({ state: { me, mustChangePassword: tokens.mustChangePassword === true }, version: 1 }),
    locale,
  };
  await context.addInitScript((s) => {
    try {
      if (sessionStorage.getItem("__e2e_seeded")) return;
      sessionStorage.setItem("__e2e_seeded", "1");
      localStorage.setItem("auth_token", s.access);
      localStorage.setItem("refresh_token", s.refresh);
      localStorage.setItem("auth-store", s.store);
      localStorage.setItem("i18nextLng", s.locale);
    } catch {
      // storage unavailable: the test will fail on its own assertions
    }
  }, seed);
}

interface Fixtures {
  /** Creates another organization (own admin, own data). `tenant` below is the default one. */
  makeTenant: (options?: CreateTenantOptions) => Promise<Tenant>;
  tenant: Tenant;
  /** `page` already signed in as the tenant administrator. */
  adminPage: Page;
}

interface WorkerFixtures {
  /** Platform administrator API session, logged in once per worker. */
  platform: ApiClient;
}

export const test = base.extend<Fixtures, WorkerFixtures>({
  platform: [
    async ({}, use) => {
      const api = new ApiClient(platformAdmin());
      await api.login();
      await use(api);
    },
    { scope: "worker" },
  ],

  makeTenant: async ({ platform }, use) => {
    await use((options) => createTenant(platform, options));
  },

  tenant: async ({ makeTenant }, use) => {
    await use(await makeTenant());
  },

  adminPage: async ({ page, tenant }, use) => {
    await signIn(page.context(), tenant.admin);
    await use(page);
  },
});

export { expect };
