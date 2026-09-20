import { test, expect } from "../support/fixtures.ts";
import { expectShell, loginThroughForm, openUserMenu, preferLanguage } from "../support/ui.ts";

test.describe("login and logout", () => {
  test.beforeEach(async ({ page }) => {
    await preferLanguage(page, "en");
  });

  test("a user signs in, sees the shell and signs out again", async ({ page, tenant }) => {
    await loginThroughForm(page, tenant.adminEmail, tenant.adminPassword);
    await expectShell(page);

    await openUserMenu(page, /^Admin /);
    await page.getByRole("menuitem", { name: "Sign out" }).click();
    await expect(page).toHaveURL(/\/login$/);

    // The session is really gone: a protected URL bounces back to the login form.
    await page.goto("/app/leads");
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.locator("#login-email")).toBeVisible();
  });

  test("a wrong password shows the error and keeps the user on the login page", async ({ page, tenant }) => {
    await loginThroughForm(page, tenant.adminEmail, "definitely-not-the-password-1!");
    await expect(page.getByRole("alert").filter({ hasText: "Incorrect email or password" })).toBeVisible();
    await expect(page).toHaveURL(/\/login$/);
  });

  test("required fields are validated before anything is sent", async ({ page }) => {
    await page.goto("/login");
    await page.locator("form").getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByText("This field is required")).toHaveCount(2);
  });
});

test("a member created by the admin must replace the temporary password before using the app", async ({
  browser,
  adminPage,
  tenant,
}) => {
  const email = `member.${Date.now().toString(36)}@e2e.test`;

  // 1) The administrator adds a user in the UI; the one-time temporary password dialog shows the password.
  await adminPage.goto("/app/settings/users");
  await adminPage.getByRole("button", { name: "Add user" }).click();
  const addDialog = adminPage.getByRole("dialog", { name: "New user" });
  await addDialog.getByLabel("Email").fill(email);
  await addDialog.getByLabel("Full name").fill("Forced Change");
  await addDialog.getByRole("button", { name: "Create" }).click();

  const passwordDialog = adminPage.getByRole("dialog", { name: "Temporary password" });
  await expect(passwordDialog).toBeVisible();
  await expect(passwordDialog).toContainText(email);
  const temporaryPassword = await passwordDialog.getByLabel("Temporary password").inputValue();
  expect(temporaryPassword.length).toBeGreaterThanOrEqual(12);
  await passwordDialog.getByRole("button", { name: "I saved the password, close" }).click();
  await expect(passwordDialog).toBeHidden();
  await expect(adminPage.getByRole("row", { name: new RegExp(email) })).toBeVisible();

  // 2) The new user signs in with it and is sent to the full-page password change (no app shell).
  const context = await browser.newContext({ baseURL: adminPage.url().split("/app")[0], locale: "en-US" });
  const page = await context.newPage();
  await preferLanguage(page, "en");
  await loginThroughForm(page, email, temporaryPassword);
  await expect(page).toHaveURL(/\/change-password$/);
  await expect(page.getByRole("heading", { name: "Change your password" })).toBeVisible();
  // Nothing of the app is reachable meanwhile.
  await page.goto("/app/leads");
  await expect(page).toHaveURL(/\/change-password$/);

  // 3) Choosing a new password opens the app.
  const newPassword = `Nw!${tenant.slug.slice(-6)}-Kd82#xQ`;
  await page.locator("#current-password").fill(temporaryPassword);
  await page.locator("#new-password").fill(newPassword);
  await page.locator("#confirm-password").fill(newPassword);
  await page.getByRole("button", { name: "Change password" }).click();
  await expectShell(page);

  // 4) The temporary password no longer works, the new one does.
  const second = await browser.newContext({ baseURL: adminPage.url().split("/app")[0], locale: "en-US" });
  const other = await second.newPage();
  await preferLanguage(other, "en");
  await loginThroughForm(other, email, temporaryPassword);
  await expect(other.getByRole("alert").filter({ hasText: "Incorrect email or password" })).toBeVisible();
  await loginThroughForm(other, email, newPassword);
  await expectShell(other);

  await context.close();
  await second.close();
});
