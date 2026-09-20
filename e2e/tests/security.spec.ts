import { test, expect } from "../support/fixtures.ts";

/**
 * The nginx entry point in production mode (Docs disabled, registration disabled): security headers on every kind of response, and
 * nothing of the operational / documentation endpoints reachable. nginx answers unknown paths with the SPA shell (history fallback),
 * so "not reachable" means: no metrics / OpenAPI / Scalar content, only the app shell (or 404 under /api).
 */

const REQUIRED_HEADERS: Record<string, RegExp> = {
  "x-content-type-options": /^nosniff$/i,
  "x-frame-options": /^DENY$/i,
  "referrer-policy": /^strict-origin-when-cross-origin$/i,
  "permissions-policy": /camera=\(\)/,
  "cross-origin-opener-policy": /^same-origin$/i,
};

function expectSecureHeaders(headers: Record<string, string>, label: string): void {
  const csp = headers["content-security-policy"];
  expect(csp, `${label}: Content-Security-Policy`).toBeTruthy();
  for (const directive of [
    "default-src 'self'",
    "script-src 'self'",
    "object-src 'none'",
    "frame-ancestors 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "connect-src 'self'",
  ]) {
    expect(csp, `${label}: CSP must contain ${directive}`).toContain(directive);
  }
  // No inline / eval scripts allowed anywhere.
  expect(csp).not.toMatch(/script-src[^;]*('unsafe-inline'|'unsafe-eval'|\*)/);
  for (const [name, pattern] of Object.entries(REQUIRED_HEADERS)) {
    expect(headers[name], `${label}: ${name}`).toMatch(pattern);
  }
  // The proxy does not advertise its version or the application stack.
  expect(headers["server"] ?? "", `${label}: server`).toMatch(/^nginx$/i);
  expect(headers["x-powered-by"], `${label}: x-powered-by`).toBeUndefined();
}

test.describe("security headers", () => {
  test("the SPA shell, hashed assets, locale files and deep links all carry CSP, nosniff and frame protection", async ({ request }) => {
    const shell = await request.get("/");
    expect(shell.status()).toBe(200);
    expectSecureHeaders(shell.headers(), "GET /");
    expect(shell.headers()["cache-control"]).toBe("no-cache");

    const html = await shell.text();
    const asset = /\/assets\/[^"']+\.js/.exec(html)?.[0];
    expect(asset, "the shell references a hashed bundle").toBeTruthy();
    const script = await request.get(asset as string);
    expect(script.status()).toBe(200);
    expectSecureHeaders(script.headers(), `GET ${asset}`);
    expect(script.headers()["cache-control"]).toContain("immutable");

    for (const path of ["/locales/en/common.json", "/index.html", "/app/leads", "/login", "/definitely/not/a/page"]) {
      const response = await request.get(path);
      expect(response.status(), path).toBe(200);
      expectSecureHeaders(response.headers(), `GET ${path}`);
    }
  });

  test("API responses behind the proxy have their own hardening headers and never cache auth data", async ({ request }) => {
    const config = await request.get("/api/v1/auth/config");
    expect(config.status()).toBe(200);
    expect(config.headers()["x-content-type-options"]).toBe("nosniff");
    expect(config.headers()["x-frame-options"]).toBe("DENY");
    expect(config.headers()["referrer-policy"]).toBe("no-referrer");
    expect(config.headers()["server"]).toMatch(/^nginx$/i);
    expect(config.headers()["x-powered-by"]).toBeUndefined();

    const login = await request.post("/api/v1/auth/login", { data: { email: "nobody@e2e.test", password: "wrong-password-1" } });
    expect(login.status()).toBe(401);
    expect(login.headers()["cache-control"]).toContain("no-store");
    expect(login.headers()["content-type"]).toContain("application/problem+json");
    const problem = (await login.json()) as { detail?: string; stack?: string; trace?: string };
    expect(JSON.stringify(problem)).not.toMatch(/Exception|StackTrace|at Sense\.Crm/);
  });

  test("a foreign Host header is refused by the API (host filtering)", async ({ request }) => {
    const response = await request.get("/api/v1/auth/config", { headers: { Host: "evil.example" } });
    expect(response.status()).toBe(400);
  });
});

test.describe("operational and documentation endpoints are closed in production mode", () => {
  for (const path of ["/metrics", "/scalar", "/scalar/v1", "/openapi", "/openapi/v1.json", "/openapi/crm.json", "/swagger", "/swagger/index.html", "/health/ready", "/health"]) {
    test(`GET ${path} serves only the app shell`, async ({ request }) => {
      const response = await request.get(path);
      const body = await response.text();
      // nginx falls back to the SPA for unknown paths: it must be the shell and nothing else.
      expect(response.headers()["content-type"] ?? "").toContain("text/html");
      expect(body).toContain('<div id="root">');
      expect(body).not.toMatch(/# HELP|# TYPE|"openapi"\s*:|Scalar|swagger-ui|"status"\s*:\s*"Healthy"/i);
    });
  }

  for (const path of ["/api/metrics", "/api/scalar", "/api/openapi/v1.json", "/api/v1/openapi/v1.json", "/api/health/ready", "/api/v1/health/ready"]) {
    test(`GET ${path} is 404`, async ({ request }) => {
      const response = await request.get(path);
      expect(response.status()).toBe(404);
      expect(await response.text()).not.toMatch(/# HELP|"openapi"\s*:|Healthy/);
    });
  }

  test("anonymous callers get 401 on protected API routes, never data", async ({ request }) => {
    for (const path of ["/api/v1/me", "/api/v1/leads", "/api/v1/organization/members", "/api/v1/platform/organizations", "/api/v1/subscription"]) {
      const response = await request.get(path);
      expect(response.status(), path).toBe(401);
    }
  });

  test("public sign-up is closed: the API says so and refuses direct sign-up", async ({ request }) => {
    const config = (await (await request.get("/api/v1/auth/config")).json()) as { signupEnabled: boolean };
    expect(config.signupEnabled).toBe(false);
    const signup = await request.post("/api/v1/auth/signup", {
      data: { organizationName: "Sneaky Org", displayName: "Sneaky", email: "sneaky@e2e.test", password: "Sneaky-Passw0rd-42!", locale: "en" },
    });
    expect([403, 404]).toContain(signup.status());
  });
});

test("a real browser session is not blocked by the Content-Security-Policy", async ({ adminPage: page }) => {
  const problems: string[] = [];
  page.on("console", (message) => {
    if (/Content Security Policy|Refused to (load|execute|apply|connect)/i.test(message.text())) problems.push(message.text());
  });
  page.on("pageerror", (error) => problems.push(`pageerror: ${error.message}`));

  for (const path of ["/app", "/app/leads", "/app/deals", "/app/quotes", "/app/cases", "/app/reports", "/app/settings/users"]) {
    await page.goto(path);
    await expect(page.getByRole("navigation", { name: "Modules" })).toBeVisible();
    await expect(page.getByRole("main")).toBeVisible();
  }
  expect(problems).toEqual([]);
});
