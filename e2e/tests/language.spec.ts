import { test, expect } from "../support/fixtures.ts";
import { expectShell, sideNav } from "../support/ui.ts";

test("the login screen defaults to Turkish and can be switched to English (and stays switched)", async ({ page }) => {
  await page.goto("/login");
  await expect(page.getByRole("heading", { name: "Oturum açın" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "tr");

  await page.getByRole("button", { name: "Dil" }).click();
  await page.getByRole("menuitem", { name: "English" }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");

  await page.reload();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
});

test("a signed-in user switches TR/EN; the choice is saved on the user and survives a reload", async ({
  adminPage: page,
  tenant,
}) => {
  await page.goto("/app");
  await expectShell(page);
  await expect(sideNav(page).getByRole("link", { name: "Leads" })).toBeVisible();

  await page.getByRole("button", { name: "Language" }).click();
  await page.getByRole("menuitem", { name: "Türkçe" }).click();
  await expect(sideNav(page).getByRole("link", { name: "Potansiyeller" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "tr");

  // Saved server-side (follows the user across devices)...
  await expect.poll(async () => (await tenant.admin.get<{ user: { locale: string } }>("/me")).user.locale).toBe("tr");
  // ...so a reload comes back in Turkish.
  await page.reload();
  await expect(sideNav(page).getByRole("link", { name: "Potansiyeller" })).toBeVisible();

  await page.getByRole("button", { name: "Dil" }).click();
  await page.getByRole("menuitem", { name: "English" }).click();
  await expect(sideNav(page).getByRole("link", { name: "Leads" })).toBeVisible();
  await expect.poll(async () => (await tenant.admin.get<{ user: { locale: string } }>("/me")).user.locale).toBe("en");
});
