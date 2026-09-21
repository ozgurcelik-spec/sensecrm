import type { Page } from "@playwright/test";
import { test, expect } from "../support/fixtures.ts";
import { createAccount, uid } from "../support/seed.ts";
import { minorUnits } from "../support/ui.ts";

/**
 * Expected amounts for the two lines below (half-up rounding per line, like the server):
 *   line 1: 2 x 100.00, 10 % discount, 20 % tax  -> 200.00 - 20.00 + 36.00           = 216.00
 *   line 2: 3 x 49.99,  no discount,   18 % tax  -> 149.97 + 26.99 (26.9946 rounded) = 176.96
 */
const EXPECTED = { subtotal: 34997, discount: 2000, tax: 6299, grand: 39296, line1: 21600, line2: 17696 };

async function expectTotals(page: Page): Promise<void> {
  const card = page.getByTestId("totals-card");
  expect(minorUnits(await card.getByTestId("total-subtotal").textContent())).toBe(EXPECTED.subtotal);
  expect(minorUnits(await card.getByTestId("total-discount").textContent())).toBe(EXPECTED.discount);
  expect(minorUnits(await card.getByTestId("total-tax").textContent())).toBe(EXPECTED.tax);
  expect(minorUnits(await card.getByTestId("total-grand").textContent())).toBe(EXPECTED.grand);
}

test("a quote with line items goes draft -> sent -> accepted -> order, with correct totals everywhere", async ({
  adminPage: page,
  tenant,
}) => {
  const account = await createAccount(tenant.admin, `Quote Customer ${uid()}`);
  const subject = `Annual licence ${uid()}`;

  // ---- Compose the quote in the editor.
  await page.goto("/app/quotes/new");
  await page.getByLabel("Subject").fill(subject);
  await page.getByRole("combobox", { name: "Account" }).fill(account.name);
  await page.getByRole("option", { name: account.name }).click();

  await page.getByLabel("Description 1", { exact: true }).fill("Platform licence");
  await page.getByLabel("Quantity 1", { exact: true }).fill("2");
  await page.getByLabel("Unit price 1", { exact: true }).fill("100");
  await page.getByLabel("Discount % 1", { exact: true }).fill("10");
  await page.getByLabel("Tax % 1", { exact: true }).fill("20");

  await page.getByRole("button", { name: "Add line" }).click();
  await page.getByLabel("Description 2", { exact: true }).fill("Onboarding workshop");
  await page.getByLabel("Quantity 2", { exact: true }).fill("3");
  await page.getByLabel("Unit price 2", { exact: true }).fill("49.99");
  await page.getByLabel("Tax % 2", { exact: true }).fill("18");

  // Live preview (computed in the browser).
  const lineTotals = page.getByTestId("line-total");
  await expect(lineTotals).toHaveCount(2);
  await expect.poll(async () => minorUnits(await lineTotals.nth(0).textContent())).toBe(EXPECTED.line1);
  await expect.poll(async () => minorUnits(await lineTotals.nth(1).textContent())).toBe(EXPECTED.line2);
  await expect.poll(async () => minorUnits(await page.getByTestId("total-grand").textContent())).toBe(EXPECTED.grand);
  await expectTotals(page);

  await page.getByRole("button", { name: "Save" }).click();

  // ---- The saved quote: server-calculated totals equal the preview; it starts as a draft.
  await expect(page).toHaveURL(/\/app\/quotes\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { name: new RegExp(subject) })).toBeVisible();
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeVisible(); // only a draft can be sent
  await expect(page.getByRole("row", { name: /Platform licence/ })).toBeVisible();
  await expect(page.getByRole("row", { name: /Onboarding workshop/ })).toBeVisible();
  await expectTotals(page);

  // ---- send -> accept
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Quote marked as sent" })).toBeVisible();
  await page.getByRole("button", { name: "Accept", exact: true }).click(); // only offered for a sent quote
  await expect(page.getByRole("alert").filter({ hasText: "Quote accepted" })).toBeVisible();

  // ---- convert to an order (only offered for an accepted quote)
  await page.getByRole("button", { name: "Convert to order" }).click();
  await expect(page).toHaveURL(/\/app\/orders\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { name: new RegExp(subject) })).toBeVisible();
  await expect(page.getByRole("row", { name: /Platform licence/ })).toBeVisible();
  await expectTotals(page);

  // The order list knows it and the quote points to the order.
  const orders = await tenant.admin.get<{ items: { subject: string; grandTotal: number; status: string }[] }>(
    `/orders?q=${encodeURIComponent(subject)}`,
  );
  expect(orders.items).toHaveLength(1);
  expect(orders.items[0]?.grandTotal).toBe(392.96);
  const quotes = await tenant.admin.get<{ items: { status: string; convertedOrderId?: string }[] }>(
    `/quotes?q=${encodeURIComponent(subject)}`,
  );
  expect(quotes.items[0]?.status).toBe("accepted");
});
