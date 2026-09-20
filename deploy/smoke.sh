#!/usr/bin/env bash
# Post-install smoke test of a running production stack, through the public entry point (the web container's nginx):
#   healthz -> auth config -> login -> /me -> create lead -> read it back -> nginx security headers
#   [-> optional: platform operations tenant check, see PLATFORM_ADMIN_* below].
# Read-only except for ONE lead named "Smoke Test" (delete it afterwards in the UI, or ignore it).
#
#   BASE_URL=https://crm.company.local ADMIN_EMAIL=admin@pilot.local ADMIN_PASSWORD='...' ./deploy/smoke.sh
#   (local: BASE_URL=http://localhost:8080 - use the host name from ALLOWED_HOSTS, an IP address is rejected by the API)
#
# Variables (all read from the environment, never from arguments):
#   BASE_URL                Entry point (default http://localhost:8080).
#   ADMIN_EMAIL             Required. An organization user with crm.leads.write.
#   ADMIN_PASSWORD          Required. Its password.
#   PLATFORM_ADMIN_EMAIL    Optional. With PLATFORM_ADMIN_PASSWORD: also log in as the platform admin and require that
#   PLATFORM_ADMIN_PASSWORD GET /api/v1/platform/organizations lists at least one organization with "isSystem":true (the platform
#                           operations tenant must be marked is_system). Skipped when either variable is empty/unset. The platform
#                           admin must already have changed the bootstrap password (a mustChangePassword account cannot call the API).
#
# Secrets never appear in the process list (`ps`): request bodies are sent with `--data-binary @-` from a shell builtin (printf) and the
# bearer token goes into a 0600 temp file passed with `-H @file` (removed on exit).
set -euo pipefail

base="${BASE_URL:-http://localhost:8080}"
email="${ADMIN_EMAIL:?set ADMIN_EMAIL (an organization user with crm.leads.write)}"
password="${ADMIN_PASSWORD:?set ADMIN_PASSWORD}"

umask 077
authfile="$(mktemp)"
bodyfile="$(mktemp)"
trap 'rm -f "$authfile" "$bodyfile"' EXIT

step() { printf '\n== %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
json() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }
jesc() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }   # JSON string escaping for \ and "
# Login with a body that never touches argv (printf is a builtin, curl reads the body from stdin). Prints the response.
login() { printf '{"email":"%s","password":"%s"}' "$(jesc "$1")" "$(jesc "$2")" \
  | curl -sS -X POST "$base/api/v1/auth/login" -H 'Content-Type: application/json' --data-binary @-; }
# Store the bearer header in the temp file; use it with -H @"$authfile" so the token is not visible in `ps`.
set_auth() { printf 'Authorization: Bearer %s\n' "$1" >"$authfile"; }

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
login="$(login "$email" "$password")"
token="$(echo "$login" | json accessToken)"
[ -n "$token" ] || fail "login failed: $login"
set_auth "$token"
echo "token received (${#token} chars)"

step "/me"
curl -fsS "$base/api/v1/me" -H @"$authfile" | head -c 300; echo

step "create + read a lead"
created="$(printf '%s' '{"firstName":"Smoke","lastName":"Test","company":"Smoke Test","source":"other"}' \
  | curl -fsS -X POST "$base/api/v1/leads" -H @"$authfile" -H 'Content-Type: application/json' --data-binary @-)"
id="$(echo "$created" | json id)"
[ -n "$id" ] || fail "lead was not created: $created"
curl -fsS "$base/api/v1/leads/$id" -H @"$authfile" | grep -q "Smoke" || fail "lead could not be read back"
echo "lead $id created and read back"

if [ -n "${PLATFORM_ADMIN_EMAIL:-}" ] && [ -n "${PLATFORM_ADMIN_PASSWORD:-}" ]; then
  step "platform operations tenant is marked is_system"
  plogin="$(login "$PLATFORM_ADMIN_EMAIL" "$PLATFORM_ADMIN_PASSWORD")"
  ptoken="$(echo "$plogin" | json accessToken)"
  [ -n "$ptoken" ] || fail "platform admin login failed: $plogin"
  set_auth "$ptoken"
  code="$(curl -sS -o "$bodyfile" -w '%{http_code}' "$base/api/v1/platform/organizations?pageSize=100" -H @"$authfile")" \
    || fail "platform organizations request could not be sent"
  if [ "$code" != "200" ]; then
    hint=""
    if echo "$plogin" | grep -q '"mustChangePassword":true' || grep -q 'password_change_required' "$bodyfile"; then
      hint=" (the platform admin still has the bootstrap password: sign in once and change it, then re-run)"
    fi
    fail "GET /api/v1/platform/organizations returned HTTP $code$hint: $(head -c 300 "$bodyfile")"
  fi
  grep -Eq '"isSystem"[[:space:]]*:[[:space:]]*true' "$bodyfile" \
    || fail "no organization with \"isSystem\":true in GET /api/v1/platform/organizations: the platform operations tenant is not marked is_system"
  echo "platform operations tenant found (isSystem:true)"
else
  step "platform operations tenant check"
  echo "skipped (set PLATFORM_ADMIN_EMAIL and PLATFORM_ADMIN_PASSWORD to enable)"
fi

printf '\nSMOKE OK\n'
