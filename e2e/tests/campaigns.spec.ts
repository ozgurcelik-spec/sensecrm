import { test, expect } from "../support/fixtures.ts";
import { createLead, uid } from "../support/seed.ts";

test("a campaign is created and a lead is added to it as a member", async ({ adminPage: page, tenant }) => {
  const suffix = uid();
  const name = `Spring launch ${suffix}`;
  const lead = await createLead(tenant.admin, { lastName: `Prospect${suffix}` });

  // ---- Create the campaign in the UI.
  await page.goto("/app/campaigns");
  await page.getByRole("button", { name: "New campaign" }).click();
  const form = page.getByRole("dialog", { name: "New campaign" });
  await form.getByLabel("Campaign name").fill(name);
  await form.getByRole("button", { name: "Create" }).click();
  await expect(page).toHaveURL(/\/app\/campaigns\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { name })).toBeVisible();
  const campaignId = /\/app\/campaigns\/([0-9a-f-]{36})$/.exec(page.url())?.[1] as string;

  // ---- Members tab: empty at first, then add the lead.
  await page.getByRole("tab", { name: "Members" }).click();
  await expect(page.getByText("This campaign has no members yet")).toBeVisible();
  await page.getByRole("button", { name: "Add members" }).click();
  const dialog = page.getByRole("dialog", { name: "Add members to the campaign" });
  await dialog.getByLabel("Member type").click();
  await page.getByRole("option", { name: "Lead", exact: true }).click();
  await dialog.getByLabel("Records").fill(lead.lastName);
  await page.getByRole("option", { name: new RegExp(lead.lastName) }).click();
  // The multi-select keeps its dropdown open after a pick (it would cover the button): a click on the title closes it.
  await dialog.getByRole("heading", { name: "Add members to the campaign" }).click();
  await expect(page.getByRole("option")).toHaveCount(0);
  await dialog.getByRole("button", { name: "Add", exact: true }).click();

  await expect(dialog).toBeHidden();
  await expect(page.getByRole("link", { name: new RegExp(lead.lastName) })).toBeVisible();
  await expect(page.getByText("This campaign has no members yet")).toHaveCount(0);

  // Persisted on the server.
  const members = await tenant.admin.get<{ items: { memberId: string; memberType: string; status: string }[]; totalCount: number }>(
    `/campaigns/${campaignId}/members`,
  );
  expect(members.totalCount).toBe(1);
  expect(members.items[0]?.memberId).toBe(lead.id);
  expect(members.items[0]?.memberType).toBe("lead");
});
