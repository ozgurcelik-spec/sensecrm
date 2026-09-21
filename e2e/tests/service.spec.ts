import { test, expect } from "../support/fixtures.ts";
import { uid } from "../support/seed.ts";

test("a support case is opened, gets an SLA badge and receives a public reply and an internal note", async ({
  adminPage: page,
  tenant,
}) => {
  const subject = `Cannot export report ${uid()}`;

  // ---- Open the case in the UI.
  await page.goto("/app/cases");
  await page.getByRole("button", { name: "New case" }).click();
  const form = page.getByRole("dialog", { name: "New case" });
  await form.getByLabel("Subject").fill(subject);
  await form.getByLabel("Description").fill("The CSV export spins forever.");
  await form.getByRole("button", { name: "Create" }).click();

  await expect(page).toHaveURL(/\/app\/cases\/[0-9a-f-]{36}$/);
  await expect(page.getByRole("heading", { name: new RegExp(subject) })).toBeVisible();
  const caseId = /\/app\/cases\/([0-9a-f-]{36})$/.exec(page.url())?.[1] as string;

  // A new case is on its SLA clock from the start: the badge says "On track" and the panel lists both targets.
  const badge = page.locator("[data-sla-state]");
  await expect(badge).toHaveAttribute("data-sla-state", "ok");
  await expect(badge).toHaveText("On track");
  await expect(page.getByTestId("sla-line-firstResponse")).toBeVisible();
  await expect(page.getByTestId("sla-line-resolution")).toBeVisible();

  // ---- The reply type must be chosen explicitly: Send stays disabled until then.
  const box = page.getByTestId("reply-box");
  const send = box.getByRole("button", { name: "Send" });
  await box.getByLabel("Reply text").fill("We are looking into it.");
  await expect(send).toBeDisabled();

  await box.getByText("Public reply", { exact: true }).click();
  await expect(send).toBeEnabled();
  await send.click();
  await expect(page.getByRole("alert").filter({ hasText: "Comment added" })).toBeVisible();

  const timeline = page.getByText("We are looking into it.");
  await expect(timeline).toBeVisible();
  // The choice resets after sending (a conscious decision for every comment) and the box is empty again.
  await expect(box.getByLabel("Reply text")).toHaveValue("");
  await expect(send).toBeDisabled();

  // ---- An internal note is stored, but flagged as internal.
  await box.getByText("Internal note", { exact: true }).click();
  await box.getByLabel("Reply text").fill("Customer is on the legacy plan.");
  await send.click();
  await expect(page.getByText("Customer is on the legacy plan.")).toBeVisible();

  // Server truth: one public and one internal comment; the first agent reply satisfied the first-response target.
  await expect
    .poll(async () => {
      const timeline = await tenant.admin.get<{ items: { visibility?: string }[] }>(`/cases/${caseId}/timeline`);
      return timeline.items.flatMap((item) => (item.visibility ? [item.visibility] : [])).sort();
    })
    .toEqual(["internal", "public"]);
  const detail = await tenant.admin.get<{ firstResponseAt?: string; slaState: string }>(`/cases/${caseId}`);
  expect(detail.firstResponseAt).toBeTruthy();
  expect(detail.slaState).toBe("ok");
});
