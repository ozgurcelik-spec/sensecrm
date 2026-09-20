import AxeBuilder from "@axe-core/playwright";
import type { Page, TestInfo } from "@playwright/test";
import { test, expect, signIn } from "../support/fixtures.ts";
import { createAccount, createDeal, createLead, createTenant, uid, type Tenant } from "../support/seed.ts";

/**
 * Accessibility smoke with axe-core (WCAG 2.0/2.1 A + AA). Serious and critical violations fail the test, EXCEPT the documented known
 * issues below (see the "Bulgular" section of e2e/README.md): they are reported as test annotations and in the HTML report but do not fail the run, so a NEW
 * problem (another rule, or a known rule on a page it was not known for) is caught immediately.
 */
interface KnownIssue {
  id: string;
  rule: string;
  /** Pages the issue is documented for (default: everywhere). */
  pages?: RegExp;
  /** Or the audit label (for states that have no URL of their own, such as an open dialog). */
  labels?: RegExp;
  reason: string;
}

const KNOWN_ISSUES: KnownIssue[] = [
  {
    id: "A11Y-1",
    rule: "color-contrast",
    reason: 'Mantine "dimmed" text (#868e96) on white/grey is 3.15-3.32:1 (AA needs 4.5:1); also green light badges at 3.8:1. Sitewide design-token issue.',
  },
  {
    id: "A11Y-2",
    rule: "aria-allowed-attr",
    pages: /\/app\/settings\/plan$/,
    reason: "Usage bars (Mantine Progress) carry aria-valuetext without role=progressbar.",
  },
  {
    id: "A11Y-3",
    rule: "button-name",
    pages: /\/app\/settings\/audit$/,
    reason: "Disabled first/previous pagination controls have no accessible name.",
  },
  {
    id: "A11Y-4",
    rule: "button-name",
    labels: /dialog/,
    reason: "The close (X) button of every Mantine Modal has no accessible name (no aria-label / closeButtonProps).",
  },
];

const IMPACTS = new Set(["serious", "critical"]);

async function audit(page: Page, info: TestInfo, label: string): Promise<void> {
  await expect(page.getByRole("main")).toBeVisible();
  await page.waitForLoadState("networkidle");
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  const relevant = results.violations.filter((v) => IMPACTS.has(v.impact ?? ""));
  const path = new URL(page.url()).pathname;
  const known = (ruleId: string) =>
    KNOWN_ISSUES.find((k) => k.rule === ruleId && (!k.pages || k.pages.test(path)) && (!k.labels || k.labels.test(label)));

  const blocking = relevant.filter((v) => !known(v.id));
  for (const v of relevant) {
    const issue = known(v.id);
    if (issue) info.annotations.push({ type: `known-issue ${issue.id}`, description: `${label}: ${v.id} x${v.nodes.length} - ${issue.reason}` });
  }
  expect(
    blocking.map((v) => `${v.id} [${v.impact}] ${v.help}: ${v.nodes.slice(0, 3).map((n) => n.html.slice(0, 200)).join(" | ")}`),
    `${label}: new serious/critical accessibility violations`,
  ).toEqual([]);
}

test.describe("accessibility (axe)", () => {
  test.describe.configure({ mode: "parallel" });

  let tenant: Tenant;
  let ids: { lead: string; account: string; deal: string; campaign: string; caseId: string; quote: string };

  test.beforeAll(async ({ platform }) => {
    tenant = await createTenant(platform);
    const account = await createAccount(tenant.admin);
    const lead = await createLead(tenant.admin);
    const deal = await createDeal(tenant, account.id);
    const campaign = await tenant.admin.post<{ id: string }>("/campaigns", { name: `A11y ${uid()}`, type: "email" });
    const item = await tenant.admin.post<{ id: string }>("/cases", { subject: `A11y case ${uid()}`, accountId: account.id });
    const quote = await tenant.admin.post<{ id: string }>("/quotes", {
      subject: `A11y quote ${uid()}`,
      accountId: account.id,
      currency: "TRY",
      lines: [{ description: "Item", quantity: 1, unitPrice: 10, discountPercent: 0, taxRate: 18 }],
    });
    ids = { lead: lead.id, account: account.id, deal: deal.id, campaign: campaign.id, caseId: item.id, quote: quote.id };
  });

  test("login screen (Turkish default and English)", async ({ page }, info) => {
    await page.goto("/login");
    await expect(page.getByRole("heading", { name: "Oturum açın" })).toBeVisible();
    await page.waitForLoadState("networkidle");
    const tr = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    const blocking = tr.violations.filter((v) => IMPACTS.has(v.impact ?? "") && !KNOWN_ISSUES.some((k) => k.rule === v.id && !k.pages));
    expect(blocking.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
    for (const v of tr.violations.filter((x) => x.id === "color-contrast")) {
      info.annotations.push({ type: "known-issue A11Y-1", description: `/login: ${v.id} x${v.nodes.length}` });
    }
  });

  const tenantPages: [string, () => string][] = [
    ["home", () => "/app"],
    ["leads list", () => "/app/leads"],
    ["lead detail", () => `/app/leads/${ids.lead}`],
    ["contacts list", () => "/app/contacts"],
    ["accounts list", () => "/app/accounts"],
    ["account detail", () => `/app/accounts/${ids.account}`],
    ["deals board", () => "/app/deals"],
    ["deals list", () => "/app/deals?view=list"],
    ["deal detail", () => `/app/deals/${ids.deal}`],
    ["activities", () => "/app/activities"],
    ["reports", () => "/app/reports"],
    ["campaigns list", () => "/app/campaigns"],
    ["campaign detail", () => `/app/campaigns/${ids.campaign}`],
    ["products", () => "/app/products"],
    ["quotes list", () => "/app/quotes"],
    ["quote detail", () => `/app/quotes/${ids.quote}`],
    ["quote editor", () => "/app/quotes/new"],
    ["orders", () => "/app/orders"],
    ["cases list", () => "/app/cases"],
    ["case detail", () => `/app/cases/${ids.caseId}`],
    ["approvals", () => "/app/approvals"],
    ["settings: organization", () => "/app/settings/organization"],
    ["settings: users", () => "/app/settings/users"],
    ["settings: roles", () => "/app/settings/roles"],
    ["settings: pipelines", () => "/app/settings/pipelines"],
    ["settings: workflows", () => "/app/settings/workflows"],
    ["settings: SLA", () => "/app/settings/sla"],
    ["settings: plan & usage", () => "/app/settings/plan"],
    ["settings: audit log", () => "/app/settings/audit"],
    ["my profile", () => "/app/account"],
  ];

  for (const [name, path] of tenantPages) {
    test(`${name}`, async ({ page }, info) => {
      await signIn(page.context(), tenant.admin);
      await page.goto(path());
      await audit(page, info, name);
    });
  }

  test("open dialog: new lead form", async ({ page }, info) => {
    await signIn(page.context(), tenant.admin);
    await page.goto("/app/leads");
    await page.getByRole("button", { name: "New lead" }).click();
    await expect(page.getByRole("dialog", { name: "New lead" })).toBeVisible();
    await audit(page, info, "new lead dialog");
  });

  for (const [name, path] of [
    ["platform: organizations", "/app/platform/organizations"],
    ["platform: plans", "/app/platform/plans"],
    ["platform: audit", "/app/platform/audit"],
  ] as const) {
    test(name, async ({ page, platform }, info) => {
      await signIn(page.context(), platform);
      await page.goto(path);
      await audit(page, info, name);
    });
  }
});
