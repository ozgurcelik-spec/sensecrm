import { describe, expect, it, vi } from "vitest";
import { problem, meWith } from "@/test/crm";
import { PERMISSIONS } from "@/types";
import {
  apiKeyScopeOptions,
  apiKeyServerFieldErrors,
  canRedeliver,
  cidrListProblem,
  cidrProblem,
  curlExample,
  endOfUtcDay,
  expiryFromDays,
  expiryLevel,
  groupScopes,
  isApiKeyScopeAllowed,
  ownedScopes,
  parseCidrLines,
  usageRange,
  validateWebhookUrl,
  webhookServerFieldErrors,
} from "./integrations";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

describe("validateWebhookUrl (syntactic SSRF rules of the plan)", () => {
  it.each([
    ["https://hooks.example.com/crm", undefined],
    ["  https://hooks.example.com:8443/a?b=1  ", undefined],
    ["https://xn--mnchen-3ya.example/hook", undefined],
    ["", "required"],
    ["   ", "required"],
    ["hooks.example.com/crm", "scheme"],
    ["http://hooks.example.com", "scheme"],
    ["ftp://hooks.example.com", "scheme"],
    ["https://user:pw@hooks.example.com", "userinfo"],
    ["https://user@hooks.example.com", "userinfo"],
    ["https://127.0.0.1/hook", "ip_literal"],
    ["https://10.0.0.5/hook", "ip_literal"],
    ["https://2130706433/hook", "ip_literal"],
    ["https://0x7f.1/hook", "ip_literal"],
    ["https://017700000001/hook", "ip_literal"],
    ["https://[::1]/hook", "ip_literal"],
    ["https://[2001:db8::1]:8443/hook", "ip_literal"],
    ["https://localhost/hook", "host"],
    ["https://LOCALHOST./hook", "host"],
    ["https://a.localhost/hook", "host"],
    ["https://printer.local/hook", "host"],
    ["https://erp.internal/hook", "host"],
    ["https://nas.lan/hook", "host"],
    ["https://intranet/hook", "host"],
    ["https://", "invalid"],
  ])("%s -> %s", (url, reason) => {
    expect(validateWebhookUrl(url)).toBe(reason);
  });

  it("refuses more than 2048 characters", () => {
    expect(validateWebhookUrl(`https://example.com/${"a".repeat(2040)}`)).toBe("too_long");
    expect(validateWebhookUrl(`https://example.com/${"a".repeat(2000)}`)).toBeUndefined();
  });
});

describe("CIDR checks", () => {
  it.each([
    ["203.0.113.0/24", undefined],
    ["10.0.0.1/32", undefined],
    ["2001:db8::/32", undefined],
    ["fe80::1/128", undefined],
    ["0.0.0.0/0", "any"],
    ["::/0", "any"],
    ["203.0.113.0", "invalid"],
    ["203.0.113.0/33", "invalid"],
    ["203.0.113.256/24", "invalid"],
    ["2001:db8::/129", "invalid"],
    ["2001:::1/64", "invalid"],
    ["not-an-ip/24", "invalid"],
    ["1.2.3.4/24/1", "invalid"],
    ["/24", "invalid"],
  ])("%s -> %s", (value, expected) => {
    expect(cidrProblem(value)).toBe(expected);
  });

  it("splits lines, commas and spaces and reports the first bad entry or too many entries", () => {
    expect(parseCidrLines("203.0.113.0/24\n\n 10.0.0.0/8, 2001:db8::/32 ")).toEqual([
      "203.0.113.0/24",
      "10.0.0.0/8",
      "2001:db8::/32",
    ]);
    expect(cidrListProblem(["203.0.113.0/24", "oops"])).toEqual({ key: "invalid", value: "oops" });
    expect(cidrListProblem(["0.0.0.0/0"])).toEqual({ key: "any", value: "0.0.0.0/0" });
    expect(cidrListProblem(Array.from({ length: 11 }, (_, i) => `10.0.0.${i}/32`))).toEqual({ key: "tooMany" });
    expect(cidrListProblem([])).toBeUndefined();
  });
});

describe("API key scopes", () => {
  it("allows crm.* but never crm.approvals.decide or org.*", () => {
    expect(isApiKeyScopeAllowed("crm.leads.read")).toBe(true);
    expect(isApiKeyScopeAllowed("crm.approvals.decide")).toBe(false);
    expect(isApiKeyScopeAllowed("org.settings.manage")).toBe(false);
    expect(isApiKeyScopeAllowed("org.integrations.manage")).toBe(false);
  });

  it("lists only crm.* scopes (read before write) and drops the permissions of a module the plan switches off", () => {
    const me = meWith([PERMISSIONS.crmLeadsRead, PERMISSIONS.orgSettingsManage, PERMISSIONS.crmApprovalsDecide]);
    const all = apiKeyScopeOptions(me);
    expect(all.every((key) => key.startsWith("crm."))).toBe(true);
    expect(all).not.toContain("crm.approvals.decide");
    expect(all).toContain("crm.leads.read");
    expect(all.indexOf("crm.leads.read")).toBeLessThan(all.indexOf("crm.leads.write"));

    const withoutCommerce = apiKeyScopeOptions({
      ...me,
      subscription: {
        planCode: "starter",
        planName: "Starter",
        status: "active",
        accessLevel: "full",
        modules: { commerce: false },
      },
    });
    expect(withoutCommerce.some((key) => key.startsWith("crm.quotes."))).toBe(false);
    expect(withoutCommerce).toContain("crm.leads.read");
  });

  it("hands out only what the creator holds (own effective permissions)", () => {
    const owned = ownedScopes(meWith([PERMISSIONS.crmLeadsRead, PERMISSIONS.crmDealsWrite, PERMISSIONS.orgUsersManage]));
    expect([...owned].sort()).toEqual(["crm.deals.write", "crm.leads.read"]);
  });

  it("groups scopes by resource in order", () => {
    expect(groupScopes(["crm.leads.read", "crm.leads.write", "crm.deals.read"])).toEqual([
      { resource: "leads", scopes: ["crm.leads.read", "crm.leads.write"] },
      { resource: "deals", scopes: ["crm.deals.read"] },
    ]);
  });
});

describe("expiry and usage range", () => {
  const now = Date.parse("2026-09-20T12:00:00Z");

  it("colours expiries: expired, within 14 days, later", () => {
    expect(expiryLevel("2026-09-20T11:59:59Z", now)).toBe("expired");
    expect(expiryLevel("2026-09-20T12:00:00Z", now)).toBe("expired");
    expect(expiryLevel("2026-10-04T12:00:00Z", now)).toBe("soon");
    expect(expiryLevel("2026-10-04T12:00:01Z", now)).toBe("ok");
  });

  it("builds instants and UTC day bounds", () => {
    expect(expiryFromDays(30, now)).toBe("2026-10-20T12:00:00.000Z");
    expect(endOfUtcDay("2027-01-31")).toBe("2027-01-31T23:59:59.000Z");
    expect(usageRange(30, now)).toEqual({ from: "2026-08-22", to: "2026-09-20" });
    expect(usageRange(1, now)).toEqual({ from: "2026-09-20", to: "2026-09-20" });
  });

  it("only finished deliveries can be resent", () => {
    expect(canRedeliver("succeeded")).toBe(true);
    expect(canRedeliver("failed")).toBe(true);
    expect(canRedeliver("pending")).toBe(false);
    expect(canRedeliver("delivering")).toBe(false);
  });

  it("puts the key into the curl example", () => {
    expect(curlExample("crmk_x", "https://crm.example/api/v1")).toBe(
      'curl -H "Authorization: Bearer crmk_x" \\\n  "https://crm.example/api/v1/leads?page=1&pageSize=25"'
    );
  });
});

describe("server error mapping", () => {
  it("maps every webhook.url_invalid reason to the URL field", () => {
    for (const reason of ["scheme", "too_long", "userinfo", "ip_literal", "host", "port", "not_allow_listed"]) {
      const result = webhookServerFieldErrors(problem(400, { code: "webhook.url_invalid", args: { reason } }));
      expect(result.url, reason).toBeTruthy();
      expect(result.url).not.toBe("Geçerli bir adres girin");
    }
    expect(webhookServerFieldErrors(problem(400, { code: "webhook.url_invalid", args: { reason: "port" } })).url).toContain("443, 8443");
  });

  it("maps name conflicts and validation errors onto fields, and leaves other codes to the toast", () => {
    expect(webhookServerFieldErrors(problem(409, { code: "webhook.name_taken" })).name).toBe("Bu adla bir webhook zaten var");
    expect(
      webhookServerFieldErrors(problem(400, { code: "validation", errors: { EventTypes: ["bad type"], name: ["too long"] } }))
    ).toEqual({ eventTypes: "bad type", name: "too long" });
    expect(webhookServerFieldErrors(problem(402, { code: "plan.limit_exceeded" }))).toEqual({});
    expect(webhookServerFieldErrors(new Error("network"))).toEqual({});
  });

  it("maps scope errors of the API key form onto the scope picker", () => {
    expect(
      apiKeyServerFieldErrors(problem(400, { code: "api_key.scope_not_allowed", args: { scope: "org.roles.manage" } })).scopes
    ).toContain("org.roles.manage");
    expect(apiKeyServerFieldErrors(problem(403, { code: "role.permission_escalation" })).scopes).toContain("Sahip olmadığınız");
    expect(apiKeyServerFieldErrors(problem(409, { code: "api_key.name_taken" })).name).toBeTruthy();
    expect(
      apiKeyServerFieldErrors(problem(400, { code: "validation", errors: { allowedCidrs: ["bad"], expiresAt: ["past"] } }))
    ).toEqual({ allowedCidrs: "bad", expiresAt: "past" });
    expect(apiKeyServerFieldErrors(problem(402, { code: "plan.limit_exceeded", args: { limit: "api_keys" } }))).toEqual({});
  });
});
