import { test, expect, signIn } from "../support/fixtures.ts";
import { addMember, uid } from "../support/seed.ts";
import { sideNav, settingsNav } from "../support/ui.ts";

const GATED_LINKS = ["Campaigns", "Products", "Quotes", "Orders", "Cases", "My approvals"];

test.describe("plan entitlements", () => {
  test("the Starter plan hides the paid modules and explains why when their URL is opened", async ({ page, makeTenant }) => {
    const starter = await makeTenant({ plan: "starter" });
    await signIn(page.context(), starter.admin);
    await page.goto("/app");

    // Core modules stay, gated ones are not offered at all.
    await expect(sideNav(page).getByRole("link", { name: "Leads" })).toBeVisible();
    await expect(sideNav(page).getByRole("link", { name: "Deals" })).toBeVisible();
    for (const name of GATED_LINKS) {
      await expect(sideNav(page).getByRole("link", { name, exact: true })).toHaveCount(0);
    }
    await expect(settingsNav(page).getByRole("link", { name: "Workflows" })).toHaveCount(0);

    // A bookmarked URL of a gated module shows the "not in your plan" page, not a broken screen.
    for (const path of ["/app/quotes", "/app/campaigns", "/app/cases", "/app/settings/workflows"]) {
      await page.goto(path);
      await expect(page.getByTestId("module-disabled")).toBeVisible();
      await expect(page.getByRole("heading", { name: "This module is not included in your plan" })).toBeVisible();
    }

    // The API agrees (the UI is not the only gate).
    const refused = await starter.admin.get("/quotes").then(() => 200, (error: { status?: number }) => error.status);
    expect(refused).toBe(403);

    // "Plan & usage" lists the modules with their state.
    await page.goto("/app/settings/plan");
    await expect(page.getByRole("heading", { name: "Plan & usage" })).toBeVisible();
    await expect(page.getByText("Starter").first()).toBeVisible();
    await expect(page.getByText("Not in plan").first()).toBeVisible();
  });

  test("the Enterprise plan offers every module", async ({ adminPage: page }) => {
    await page.goto("/app");
    for (const name of GATED_LINKS.filter((n) => n !== "My approvals")) {
      await expect(sideNav(page).getByRole("link", { name, exact: true })).toBeVisible();
    }
    await expect(settingsNav(page).getByRole("link", { name: "Workflows" })).toBeVisible();
  });

  test("the 5-user limit of the Starter plan refuses the sixth user with a plan-limit toast", async ({ page, makeTenant }) => {
    const starter = await makeTenant({ plan: "starter" });
    // The administrator is user 1; four more fill the plan.
    for (let n = 1; n <= 4; n++) await addMember(starter, starter.roles.standard, { label: `filler${n}`, finalize: false });

    await signIn(page.context(), starter.admin);
    await page.goto("/app/settings/users");
    await expect(page.getByRole("row", { name: /filler4/ })).toBeVisible();

    await page.getByRole("button", { name: "Add user" }).click();
    const dialog = page.getByRole("dialog", { name: "New user" });
    await dialog.getByLabel("Email").fill(`overflow.${uid()}@e2e.test`);
    await dialog.getByLabel("Full name").fill("One Too Many");
    await dialog.getByRole("button", { name: "Create" }).click();

    const toast = page.getByRole("alert").filter({ hasText: "Plan limit reached" });
    await expect(toast).toBeVisible();
    await expect(toast).toContainText("5/5");
    await expect(toast.getByRole("button", { name: "Plan & usage" })).toBeVisible();
    // The dialog stays open so nothing typed is lost, and no temporary password was issued.
    await expect(dialog).toBeVisible();
    await expect(page.getByRole("dialog", { name: "Temporary password" })).toHaveCount(0);
    const members = await starter.admin.get<unknown[]>("/organization/members");
    expect(members).toHaveLength(5);
  });
});
