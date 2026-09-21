import { ApiError } from "../support/api.ts";
import { test, expect, signIn } from "../support/fixtures.ts";
import { expectShell, loginThroughForm, preferLanguage } from "../support/ui.ts";
import { uid } from "../support/seed.ts";

test.describe("platform console", () => {
  test("the platform admin creates an organization; its administrator must set a password on first sign-in", async ({
    page,
    platform,
    browser,
  }) => {
    const suffix = uid();
    const orgName = `Console Org ${suffix}`;
    const adminEmail = `console.${suffix}@e2e.test`;

    await signIn(page.context(), platform);
    await page.goto("/app/platform/organizations");
    await expect(page.getByRole("heading", { name: "Organizations", level: 2 })).toBeVisible();
    await page.getByRole("button", { name: "Create organization" }).click();

    const dialog = page.getByRole("dialog", { name: "Create organization" });
    await dialog.getByLabel("Organization name").fill(orgName);
    await dialog.getByLabel("Administrator name").fill("Console Admin");
    await dialog.getByLabel("Administrator e-mail").fill(adminEmail);
    await dialog.getByLabel("Language").click();
    await page.getByRole("option", { name: "English" }).click();
    await dialog.getByLabel("Plan").click();
    await page.getByRole("option", { name: "Business" }).click();
    await dialog.getByRole("button", { name: "Create", exact: true }).click();

    // A new administrator account gets a one-time password.
    const passwordDialog = page.getByRole("dialog", { name: "Temporary password" });
    await expect(passwordDialog).toBeVisible();
    const temporaryPassword = await passwordDialog.getByLabel("Temporary password").inputValue();
    await passwordDialog.getByRole("button", { name: "I saved the password, close" }).click();

    // ...and the console opens the new organization.
    await expect(page).toHaveURL(/\/app\/platform\/organizations\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { name: orgName })).toBeVisible();
    await expect(page.getByText("Business").first()).toBeVisible();

    // The list finds it too.
    await page.goto("/app/platform/organizations");
    await page.getByPlaceholder("Search name or slug").fill(orgName);
    await expect(page.getByRole("link", { name: orgName })).toBeVisible();

    // The new administrator signs in with the one-time password and lands on the forced password change.
    const context = await browser.newContext({ baseURL: page.url().split("/app")[0], locale: "en-US" });
    const fresh = await context.newPage();
    await preferLanguage(fresh, "en");
    await loginThroughForm(fresh, adminEmail, temporaryPassword);
    await expect(fresh).toHaveURL(/\/change-password$/);
    await context.close();
  });

  test("suspending an organization makes its users read-only until it is reactivated", async ({
    browser,
    platform,
    adminPage,
    tenant,
  }) => {
    const tenantPage = adminPage;
    await tenantPage.goto("/app/leads");
    await expectShell(tenantPage);
    await expect(tenantPage.getByRole("button", { name: "New lead" })).toBeVisible();
    await expect(tenantPage.getByTestId("subscription-banner")).toHaveCount(0);

    // The platform admin suspends the organization (read-only) from the console.
    const platformContext = await browser.newContext({ baseURL: tenantPage.url().split("/app")[0], locale: "en-US" });
    await signIn(platformContext, platform);
    const console_ = await platformContext.newPage();
    await console_.goto(`/app/platform/organizations/${tenant.id}`);
    await expect(console_.getByRole("heading", { name: tenant.name })).toBeVisible();
    await console_.getByRole("button", { name: "Suspend" }).click();
    const suspend = console_.getByRole("dialog", { name: "Suspend organization" });
    await suspend.getByLabel("Reason").fill("E2E: unpaid invoice");
    await suspend.getByRole("radio", { name: /Read-only/ }).check();
    await suspend.getByRole("button", { name: "Suspend", exact: true }).click();
    await expect(console_.getByRole("alert").filter({ hasText: "was suspended" })).toBeVisible();

    // The tenant now sees the banner, has no write actions and the API refuses writes.
    // (the tenant's plan state is cached briefly on the server: reload until the change shows, no fixed sleeps)
    const banner = tenantPage.getByTestId("subscription-banner");
    await expect(async () => {
      await tenantPage.reload();
      await expect(banner).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(banner).toHaveAttribute("data-status", "suspended");
    await expect(banner).toContainText("Your account is suspended; records are read-only.");
    await expect(tenantPage.getByRole("heading", { name: "Leads", level: 2 })).toBeVisible();
    await expect(tenantPage.getByRole("button", { name: "New lead" })).toHaveCount(0);
    const refused = await tenant.admin
      .post("/leads", { lastName: "Blocked", company: "Blocked Inc", source: "web" })
      .then(() => undefined, (error: unknown) => error);
    expect(refused).toBeInstanceOf(ApiError);
    expect([402, 403, 409]).toContain((refused as ApiError).status);
    // Reading still works.
    await tenant.admin.get("/leads");

    // Reactivation restores everything.
    await console_.reload();
    await console_.getByRole("button", { name: "Reactivate" }).click();
    const confirm = console_.getByRole("dialog", { name: "Reactivate organization" });
    await confirm.getByRole("button", { name: "Reactivate" }).click();
    await expect(console_.getByRole("alert").filter({ hasText: "Organization reactivated" })).toBeVisible();

    await expect(async () => {
      await tenantPage.reload();
      await expect(tenantPage.getByRole("button", { name: "New lead" })).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(tenantPage.getByTestId("subscription-banner")).toHaveCount(0);
    await platformContext.close();
  });
});
