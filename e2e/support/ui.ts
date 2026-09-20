import { expect, type Locator, type Page } from "@playwright/test";

/** Forces the UI language for anonymous pages (the login screen defaults to Turkish). */
export async function preferLanguage(page: Page, language: "tr" | "en"): Promise<void> {
  await page.addInitScript((lng) => {
    try {
      localStorage.setItem("i18nextLng", lng);
    } catch {
      // ignore
    }
  }, language);
}

/** Signs in through the login form (used by the tests whose subject IS the login flow). */
export async function loginThroughForm(page: Page, email: string, password: string): Promise<void> {
  await page.goto("/login");
  await page.locator("#login-email").fill(email);
  await page.locator("#login-password").fill(password);
  await page.locator("form").getByRole("button", { name: /^(sign in|giriş yap)$/i }).click();
}

/** Main navigation region of the signed-in shell. */
export function sideNav(page: Page): Locator {
  return page.getByRole("navigation", { name: /^(modules|modüller)$/i });
}

export function settingsNav(page: Page): Locator {
  return page.getByRole("navigation", { name: /^(settings|ayarlar)$/i });
}

/** Mantine notification (toast) region items. */
export function toasts(page: Page): Locator {
  return page.getByRole("alert");
}

/** Waits until the signed-in shell is rendered (top bar with the user menu and the module navigation). */
export async function expectShell(page: Page): Promise<void> {
  await expect(page).toHaveURL(/\/app(\/|$|\?)/);
  await expect(sideNav(page)).toBeVisible();
}

/**
 * Amount text -> integer minor units, independent of the UI language ("1.234,56 ₺", "TRY 1,234.56", "₺392.96"):
 * everything but digits is dropped, the last two digits are the fraction.
 */
export function minorUnits(text: string | null): number {
  const digits = (text ?? "").replace(/[^\d]/g, "");
  if (!digits) throw new Error(`no amount in "${text}"`);
  return Number(digits);
}

export async function openUserMenu(page: Page, displayName: string | RegExp): Promise<void> {
  await page.getByRole("button", { name: displayName }).first().click();
}
