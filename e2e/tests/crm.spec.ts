import { test, expect } from "../support/fixtures.ts";
import { createAccount, createDeal, uid } from "../support/seed.ts";

test.describe("sales", () => {
  test("a lead is created, then converted into an account, a contact and a deal", async ({ adminPage: page, tenant }) => {
    const suffix = uid();
    const company = `Acme ${suffix}`;
    const lastName = `Converter${suffix}`;
    const dealName = `Acme rollout ${suffix}`;

    // Create the lead in the UI.
    await page.goto("/app/leads");
    await page.getByRole("button", { name: "New lead" }).click();
    const form = page.getByRole("dialog", { name: "New lead" });
    await form.getByLabel("First name").fill("Ada");
    await form.getByLabel("Last name").fill(lastName);
    await form.getByLabel("Company").fill(company);
    await form.getByLabel("Email").fill(`ada.${suffix}@e2e.test`);
    await form.getByRole("button", { name: "Create" }).click();
    await expect(page).toHaveURL(/\/app\/leads\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { name: new RegExp(lastName) })).toBeVisible();

    // Convert it from the list: new account + contact + a deal.
    await page.goto("/app/leads");
    const row = page.getByRole("row", { name: new RegExp(lastName) });
    await row.getByRole("button", { name: "Convert" }).click();
    const convert = page.getByRole("dialog", { name: /Convert lead/ });
    await expect(convert.getByRole("radio", { name: `New account: ${company}` })).toBeChecked();
    await convert.getByLabel("Also create a deal").check();
    await convert.getByLabel("Deal name").fill(dealName);
    await convert.getByLabel("Amount").fill("12500");
    await convert.getByRole("button", { name: "Convert", exact: true }).click();

    // The deal page opens; account and contact exist and the lead is marked converted.
    await expect(page).toHaveURL(/\/app\/deals\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { name: dealName })).toBeVisible();
    await expect(page.getByText(company).first()).toBeVisible();

    const accounts = await tenant.admin.get<{ items: { id: string; name: string }[] }>(`/accounts?q=${encodeURIComponent(company)}`);
    expect(accounts.items.map((a) => a.name)).toContain(company);
    const contacts = await tenant.admin.get<{ items: { fullName: string; accountId?: string }[] }>(`/contacts?q=${lastName}`);
    expect(contacts.items).toHaveLength(1);
    expect(contacts.items[0]?.accountId).toBe(accounts.items.find((a) => a.name === company)?.id);
    const deals = await tenant.admin.get<{ items: { name: string; amount: number }[] }>(`/deals?q=${encodeURIComponent(dealName)}`);
    expect(deals.items).toHaveLength(1);
    expect(deals.items[0]?.amount).toBe(12500);

    await page.goto("/app/leads");
    const converted = page.getByRole("row", { name: new RegExp(lastName) });
    await expect(converted).toContainText("Converted");
    await expect(converted.getByRole("button", { name: "Convert" })).toHaveCount(0);
  });

  test("a deal is dragged to another stage of the kanban board and the move is saved", async ({ adminPage: page, tenant }) => {
    const account = await createAccount(tenant.admin);
    const deal = await createDeal(tenant, account.id, { name: `Board deal ${uid()}`, amount: 4200 });
    const proposal = tenant.stages["Proposal"] as string;
    const qualification = tenant.stages["Qualification"] as string;

    await page.goto("/app/deals");
    const source = page.getByTestId(`stage-${qualification}`);
    const target = page.getByTestId(`stage-${proposal}`);
    const card = source.getByTestId(`deal-card-${deal.id}`);
    await expect(card).toBeVisible();
    await expect(target.getByTestId(`deal-card-${deal.id}`)).toHaveCount(0);

    // A real pointer drag (press, move past the activation distance, glide to the column, release).
    const from = (await card.boundingBox())!;
    const to = (await target.boundingBox())!;
    await page.mouse.move(from.x + 30, from.y + 30);
    await page.mouse.down();
    await page.mouse.move(from.x + 45, from.y + 40, { steps: 4 });
    await page.mouse.move(to.x + to.width / 2, to.y + 90, { steps: 20 });
    await page.mouse.up();

    await expect(target.getByTestId(`deal-card-${deal.id}`)).toBeVisible();
    await expect(source.getByTestId(`deal-card-${deal.id}`)).toHaveCount(0);
    await expect(page.getByRole("alert").filter({ hasText: `Deal "${deal.name}" moved to "Proposal"` })).toBeVisible();

    // Saved on the server, so a fresh load agrees.
    await expect.poll(async () => (await tenant.admin.get<{ stageId: string }>(`/deals/${deal.id}`)).stageId).toBe(proposal);
    await page.reload();
    await expect(page.getByTestId(`stage-${proposal}`).getByTestId(`deal-card-${deal.id}`)).toBeVisible();
  });
});
