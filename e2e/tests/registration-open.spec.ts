import { test, expect } from "../support/fixtures.ts";
import { expectShell, preferLanguage } from "../support/ui.ts";
import { strongPassword, uid } from "../support/seed.ts";

/** Runs only in the "registration-open" phase (API recreated with Registration:Mode=open); see playwright.config.ts. */
test.describe("open registration", () => {
  test("the API announces sign-up and the login page links to it", async ({ page, request }) => {
    const config = (await (await request.get("/api/v1/auth/config")).json()) as { signupEnabled: boolean };
    expect(config.signupEnabled).toBe(true);
    await preferLanguage(page, "en");
    await page.goto("/login");
    await page.getByRole("link", { name: "Sign up for free" }).click();
    await expect(page).toHaveURL(/\/signup$/);
    await expect(page.getByRole("heading", { name: "Create your organization" })).toBeVisible();
  });

  test("a visitor creates an organization and becomes its administrator", async ({ page }) => {
    const suffix = uid();
    const company = `Self-service ${suffix}`;
    await preferLanguage(page, "en");
    await page.goto("/signup");
    await page.locator("#signup-organization").fill(company);
    await page.locator("#signup-name").fill("Sam Signup");
    await page.locator("#signup-email").fill(`sam.${suffix}@e2e.test`);
    await page.locator("#signup-password").fill(strongPassword());
    await page.getByRole("button", { name: "Create account" }).click();

    await expectShell(page);
    await expect(page.getByRole("button", { name: "Switch organization" })).toContainText(company);
    // The founder is the Administrator: administration is available.
    await expect(page.getByRole("navigation", { name: "Settings" }).getByRole("link", { name: "Users" })).toBeVisible();
    // A self-service organization starts on a trial plan.
    await page.goto("/app/settings/plan");
    await expect(page.getByRole("heading", { name: "Plan & usage" })).toBeVisible();
  });

  test("weak passwords are rejected by the sign-up form", async ({ page }) => {
    const suffix = uid();
    await preferLanguage(page, "en");
    await page.goto("/signup");
    await page.locator("#signup-organization").fill(`Weak ${suffix}`);
    await page.locator("#signup-name").fill("Weak Pass");
    await page.locator("#signup-email").fill(`weak.${suffix}@e2e.test`);
    await page.locator("#signup-password").fill("short");
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page.getByText(/at least 10 characters/i)).toBeVisible();
    await expect(page).toHaveURL(/\/signup$/);
  });
});
