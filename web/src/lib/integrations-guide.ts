/** Static examples of the developer guide (M8B): signature verification, envelope and header list. */

export const SIGNATURE_HEADER = "X-Crm-Signature";

/** `X-Crm-Signature: t=<unix>,v1=<hex>[,v1=<hex>]` with `v1 = HMAC-SHA256(secret, "<t>." + body)`. */
export const SIGNATURE_EXAMPLE = "X-Crm-Signature: t=1700000000,v1=2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e";

export const ENVELOPE_EXAMPLE = JSON.stringify(
  {
    id: "0192f0a1-0000-7000-8000-000000000001",
    type: "lead.created",
    version: 1,
    occurredAt: "2026-09-20T09:00:00Z",
    tenantId: "0192f0a1-0000-7000-8000-0000000000aa",
    data: {
      leadId: "0192f0a1-0000-7000-8000-0000000000bb",
      source: "web",
      ownerUserId: "0192f0a1-0000-7000-8000-0000000000cc",
    },
  },
  null,
  2
);

export const REQUEST_HEADERS: readonly { name: string; key: string }[] = [
  { name: "Content-Type", key: "contentType" },
  { name: "User-Agent", key: "userAgent" },
  { name: "X-Crm-Event-Id", key: "eventId" },
  { name: "X-Crm-Event-Type", key: "eventType" },
  { name: "X-Crm-Delivery-Id", key: "deliveryId" },
  { name: "X-Crm-Delivery-Attempt", key: "deliveryAttempt" },
  { name: "X-Crm-Signature", key: "signature" },
];

export const VERIFY_NODE = `import crypto from "node:crypto";

// rawBody: the request body exactly as received (Buffer), never re-serialised JSON.
export function verify(secret, header, rawBody, nowSeconds = Date.now() / 1000, tolerance = 300) {
  const items = header.split(",").map((part) => part.trim().split("="));
  const t = Number(items.find(([key]) => key === "t")?.[1]);
  const signatures = items.filter(([key]) => key === "v1").map(([, value]) => value);
  if (!Number.isFinite(t) || signatures.length === 0) return false;
  if (Math.abs(nowSeconds - t) > tolerance) return false;
  const expected = crypto.createHmac("sha256", secret).update(\`\${t}.\`).update(rawBody).digest("hex");
  return signatures.some(
    (signature) =>
      signature.length === expected.length &&
      crypto.timingSafeEqual(Buffer.from(signature), Buffer.from(expected))
  );
}`;

export const VERIFY_PYTHON = `import hashlib
import hmac
import time


def verify(secret: str, header: str, raw_body: bytes, tolerance: int = 300) -> bool:
    parts = [part.strip().split("=", 1) for part in header.split(",")]
    t = next((value for key, value in parts if key == "t"), None)
    signatures = [value for key, value in parts if key == "v1"]
    if t is None or not t.isdigit() or not signatures:
        return False
    if abs(time.time() - int(t)) > tolerance:
        return False
    expected = hmac.new(secret.encode(), t.encode() + b"." + raw_body, hashlib.sha256).hexdigest()
    return any(hmac.compare_digest(signature, expected) for signature in signatures)`;

export const VERIFY_CSHARP = `using System.Security.Cryptography;
using System.Text;

static bool Verify(string secret, string header, byte[] rawBody, DateTimeOffset now, int toleranceSeconds = 300)
{
    var items = header.Split(',', StringSplitOptions.TrimEntries)
        .Select(part => part.Split('=', 2))
        .Where(part => part.Length == 2)
        .ToList();
    var t = items.FirstOrDefault(part => part[0] == "t")?[1];
    var signatures = items.Where(part => part[0] == "v1").Select(part => part[1]).ToList();
    if (!long.TryParse(t, out var timestamp) || signatures.Count == 0) return false;
    if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > toleranceSeconds) return false;

    var signed = Encoding.UTF8.GetBytes(t + ".").Concat(rawBody).ToArray();
    var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)).ToLowerInvariant();
    return signatures.Any(signature =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected)));
}`;

export const VERIFY_SAMPLES = [
  { key: "node", label: "Node.js", code: VERIFY_NODE },
  { key: "python", label: "Python", code: VERIFY_PYTHON },
  { key: "csharp", label: "C#", code: VERIFY_CSHARP },
] as const;

export const PAGING_EXAMPLE = `GET /api/v1/leads?page=2&pageSize=50&sort=-createdAt&q=acme

{
  "items": [ ... ],
  "page": 2,
  "pageSize": 50,
  "totalCount": 137
}`;

export const ERROR_EXAMPLE = `HTTP/1.1 403 Forbidden
Content-Type: application/problem+json

{
  "status": 403,
  "code": "forbidden",
  "title": "You do not have permission for this action",
  "args": { "permission": "crm.leads.write" }
}`;
