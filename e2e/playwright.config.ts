import { defineConfig, devices } from "@playwright/test";

const BASE_URL = process.env.E2E_BASE_URL ?? "http://localhost:8181";
const PHASE = process.env.E2E_PHASE ?? "main";
const CI = !!process.env.CI;

/**
 * Two phases, because Registration:Mode is a start-up setting of the API container:
 *  - "main": registration disabled, exactly like production (everything except registration-open.spec.ts)
 *  - "registration-open": the API recreated with open registration (only registration-open.spec.ts)
 * run.ps1 / run.sh execute both against the same stack.
 */
export default defineConfig({
  testDir: "./tests",
  outputDir: "./test-results",
  globalSetup: "./support/global-setup.ts",
  fullyParallel: true,
  forbidOnly: CI,
  // No retries: a test that needs one is flaky and must be fixed, not hidden.
  retries: 0,
  workers: Number(process.env.E2E_WORKERS ?? (CI ? 2 : 4)),
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: [
    [CI ? "github" : "list"],
    ["html", { outputFolder: "playwright-report", open: "never" }],
    ["junit", { outputFile: "test-results/junit.xml" }],
  ],
  use: {
    baseURL: BASE_URL,
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    trace: "retain-on-failure",
    video: "retain-on-failure",
    screenshot: "only-on-failure",
    // Deterministic rendering: the app's language comes from the signed-in user (tests seed "en"), not from the browser.
    locale: "en-US",
    timezoneId: "Europe/Istanbul",
    viewport: { width: 1440, height: 900 },
  },
  projects:
    PHASE === "registration-open"
      ? [{ name: "registration-open", testMatch: /registration-open\.spec\.ts/, use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } }]
      : [{ name: "main", testIgnore: /registration-open\.spec\.ts/, use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } }],
});
