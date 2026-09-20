import { test, expect, signIn } from "../support/fixtures.ts";
import { addMember, createAccount, createDeal, uid } from "../support/seed.ts";

/**
 * Deal approval, end to end through the real engine (Conductor container + worker): the rule is defined in the UI, a big deal moves to
 * a "won" stage, the approver decides in "My approvals" and the workflow execution completes.
 * The deal owner cannot approve their own deal, so the approver is a second Administrator.
 */
test("a rule requests approval for a big won deal and the approver decides", async ({ adminPage: page, browser, tenant }) => {
  test.setTimeout(180_000);
  const suffix = uid();
  const ruleName = `Big deal approval ${suffix}`;
  const approver = await addMember(tenant, tenant.roles.administrator, { label: "approver" });
  const account = await createAccount(tenant.admin);
  const deal = await createDeal(tenant, account.id, { name: `Mega deal ${suffix}`, amount: 25000 });
  const smallDeal = await createDeal(tenant, account.id, { name: `Small deal ${suffix}`, amount: 100 });

  // ---- The administrator defines the rule in Settings -> Workflows.
  await page.goto("/app/settings/workflows");
  await page.getByRole("button", { name: "New rule" }).click();
  const form = page.getByRole("dialog", { name: "New rule" });
  await form.getByLabel("Rule name").fill(ruleName);
  await form.getByLabel("Type").click();
  await page.getByRole("option", { name: "Deal approval" }).click();
  await form.getByLabel("Minimum amount").fill("5000");
  await form.getByLabel("Approver role").click();
  await page.getByRole("option", { name: "Administrator" }).click();
  await form.getByRole("button", { name: "Create" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Rule created" })).toBeVisible();
  await expect(page.getByRole("row", { name: new RegExp(ruleName) })).toBeVisible();

  // ---- Both deals are won through the board's "move" menu; only the big one needs an approval.
  await page.goto("/app/deals");
  for (const won of [smallDeal, deal]) {
    await page.getByRole("button", { name: `Move deal ${won.name}` }).click();
    await page.getByRole("menuitem", { name: "Closed Won" }).click();
    await expect(page.getByRole("alert").filter({ hasText: `Deal "${won.name}" moved to "Closed Won"` })).toBeVisible();
  }

  // ---- The approver sees exactly one request (the big deal) and approves it.
  const context = await browser.newContext({ baseURL: page.url().split("/app")[0], locale: "en-US" });
  await signIn(context, approver.api);
  const approverPage = await context.newPage();
  // The engine creates the approval asynchronously (outbox -> Conductor -> worker); the page does not poll, so open it once it exists.
  await expect
    .poll(async () => (await approver.api.get<{ items: unknown[] }>("/approvals?mine=true&status=pending")).items.length, {
      timeout: 90_000,
      intervals: [500, 1000, 2000],
    })
    .toBe(1);
  await approverPage.goto("/app/approvals");
  const request = approverPage.getByRole("row", { name: new RegExp(`Deal approval: ${deal.name}`) });
  await expect(request).toBeVisible();
  await expect(approverPage.getByRole("row", { name: new RegExp(smallDeal.name) })).toHaveCount(0);

  await request.getByRole("button", { name: /^Approve / }).click();
  const decision = approverPage.getByRole("dialog", { name: "Approval decision" });
  await decision.getByLabel("Comment").fill("Margin is fine.");
  await decision.getByRole("button", { name: "Approve", exact: true }).click();
  await expect(approverPage.getByRole("alert").filter({ hasText: "Approval given" })).toBeVisible();
  await expect(approverPage.getByText("You have no pending approvals")).toBeVisible();

  await approverPage.getByRole("tab", { name: "History" }).click();
  const history = approverPage.getByRole("row", { name: new RegExp(`Deal approval: ${deal.name}`) });
  await expect(history).toContainText("Approved");
  await expect(history).toContainText("Margin is fine.");

  // ---- The engine finishes the execution (asynchronously) and the administrator can see it in the executions tab.
  await expect
    .poll(
      async () => {
        const executions = await tenant.admin.get<{ items: { subjectName?: string; status: string }[] }>("/workflows/executions");
        return executions.items.find((e) => e.subjectName === deal.name)?.status;
      },
      { timeout: 90_000, intervals: [1000, 2000, 3000] },
    )
    .toBe("completed");
  const executions = await tenant.admin.get<{ items: { subjectName?: string }[] }>("/workflows/executions");
  expect(executions.items.some((e) => e.subjectName === smallDeal.name)).toBe(false);
  await context.close();
});
