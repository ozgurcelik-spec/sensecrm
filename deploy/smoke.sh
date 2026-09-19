#!/usr/bin/env bash
# Post-install smoke test of a running production stack, through the public entry point (the web container's nginx):
#   healthz -> auth config -> login -> /me -> create lead -> read it back -> nginx security headers.
# Read-only except for ONE lead named "Smoke Test" (delete it afterwards in the UI, or ignore it).
#
#   BASE_URL=https://crm.company.local ADMIN_EMAIL=admin@pilot.local ADMIN_PASSWORD='...' ./deploy/smoke.sh
#   (local: BASE_URL=http://localhost:8080 - use the host name from ALLOWED_HOSTS, an IP address is rejected by the API)
set -euo pipefail

base="${BASE_URL:-http://localhost:8080}"
email="${ADMIN_EMAIL:?set ADMIN_EMAIL (an organization user with crm.leads.write)}"
password="${ADMIN_PASSWORD:?set ADMIN_PASSWORD}"

step() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
json() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }

step "web container liveness (/healthz)"
[ "$(curl -fsS "$base/healthz" | tr -d '\r\n')" = "ok" ] || fail "/healthz did not return ok"

step "security headers on the SPA"
headers="$(curl -fsSI "$base/")"
for h in content-security-policy x-frame-options x-content-type-options referrer-policy; do
  echo "$headers" | grep -qi "^$h:" || fail "missing header $h"
done
echo "ok"

step "API reachable through the proxy (/api/v1/auth/config)"
curl -fsS "$base/api/v1/auth/config"; echo

step "login"
login="$(curl -sS -X POST "$base/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "$(printf '{"email":"%s","password":"%s"}' "$email" "$password")")"
token="$(echo "$login" | json accessToken)"
[ -n "$token" ] || fail "login failed: $login"
auth="Authorization: Bearer $token"
echo "token received (${#token} chars)"

step "/me"
curl -fsS "$base/api/v1/me" -H "$auth" | head -c 300; echo

step "create + read a lead"
created="$(curl -fsS -X POST "$base/api/v1/leads" -H "$auth" -H 'Content-Type: application/json' \
  -d '{"firstName":"Smoke","lastName":"Test","company":"Smoke Test","source":"other"}')"
id="$(echo "$created" | json id)"
[ -n "$id" ] || fail "lead was not created: $created"
curl -fsS "$base/api/v1/leads/$id" -H "$auth" | grep -q "Smoke" || fail "lead could not be read back"
echo "lead $id created and read back"

printf '\nSMOKE OK\n'
