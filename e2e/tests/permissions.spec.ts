import { test, expect, signIn } from "../support/fixtures.ts";
import { addMember } from "../support/seed.ts";
import { settingsNav, sideNav } from "../support/ui.ts";

test.describe("permission gating", () => {
  test("a Standard user gets no workflow, plan, audit or platform pages; the administrator gets all of them", async ({
    page,
    browser,
    tenant,
  }) => {
    const standard = await addMember(tenant, tenant.roles.standard, { label: "standard" });
    await signIn(page.context(), standard.api);
    await page.goto("/app");

    // Navigation: business modules yes, administration that needs org.* permissions no.
    await expect(sideNav(page).getByRole("link", { name: "Leads" })).toBeVisible();
    await expect(settingsNav(page).getByRole("link", { name: "Users" })).toBeVisible(); // Standard holds org.users.read
    for (const hidden of ["Workflows", "SLA policies", "Plan & usage", "Audit Log"]) {
      await expect(settingsNav(page).getByRole("link", { name: hidden })).toHaveCount(0);
    }
    await expect(page.getByRole("navigation", { name: "Platform" })).toHaveCount(0);
    await expect(sideNav(page).getByRole("link", { name: "My approvals" })).toHaveCount(0);

    // Deep links land on "Access denied", not on the page.
    for (const path of ["/app/settings/workflows", "/app/settings/sla", "/app/settings/plan", "/app/settings/audit", "/app/platform/organizations"]) {
      await page.goto(path);
      await expect(page.getByRole("heading", { name: "Access denied" })).toBeVisible();
    }

    // Roles are readable (org.users.read) but a Standard user cannot manage them.
    await page.goto("/app/settings/roles");
    await expect(page.getByRole("heading", { name: "Roles & Permissions", level: 2 })).toBeVisible();
    await expect(page.getByRole("button", { name: "Create role" })).toHaveCount(0);
    // ...and cannot add users.
    await page.goto("/app/settings/users");
    await expect(page.getByRole("heading", { name: "Users", level: 2 })).toBeVisible();
    await expect(page.getByRole("button", { name: "Add user" })).toHaveCount(0);

    // The server enforces the same: forbidden, not just hidden.
    const status = (promise: Promise<unknown>) => promise.then(() => 200, (error: { status?: number }) => error.status);
    expect(await status(standard.api.get("/workflows/rules"))).toBe(403);
    expect(await status(standard.api.get("/organization/audit"))).toBe(403);
    expect(await status(standard.api.post("/organization/roles", { name: "Sneaky", permissions: [] }))).toBe(403);
    expect(await status(standard.api.get("/platform/organizations"))).toBe(403);

    // Control: the administrator sees what the Standard user does not.
    const adminContext = await browser.newContext({ baseURL: page.url().split("/app")[0], locale: "en-US" });
    await signIn(adminContext, tenant.admin);
    const adminPage = await adminContext.newPage();
    await adminPage.goto("/app");
    for (const visible of ["Workflows", "SLA policies", "Plan & usage", "Audit Log", "Roles & Permissions"]) {
      await expect(settingsNav(adminPage).getByRole("link", { name: visible })).toBeVisible();
    }
    await adminPage.goto("/app/settings/roles");
    await expect(adminPage.getByRole("button", { name: "Create role" })).toBeVisible();
    await adminContext.close();
  });
});
