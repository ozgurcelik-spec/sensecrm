#!/usr/bin/env bash
# Generates deploy/.env (random passwords) and deploy/secrets/* (JWT signing key, platform admin password).
# Linux/macOS twin of generate-secrets.ps1. Needs: bash, openssl. Existing files are kept unless FORCE=1.
#
#   PUBLIC_HOSTNAME=crm.company.local PLATFORM_ADMIN_EMAIL=admin@company.local ./deploy/generate-secrets.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${OUT_DIR:-$here}"
public_hostname="${PUBLIC_HOSTNAME:-crm.example.local}"
admin_email="${PLATFORM_ADMIN_EMAIL:-}"
admin_name="${PLATFORM_ADMIN_NAME:-Platform Admin}"
force="${FORCE:-0}"

command -v openssl >/dev/null || { echo "openssl is required" >&2; exit 1; }
[ -f "$here/.env.example" ] || { echo ".env.example not found next to this script" >&2; exit 1; }

umask 077
mkdir -p "$out/secrets"
chmod 700 "$out/secrets"

# 32 alphanumeric characters without look-alikes; no ';', quotes or spaces (they would break connection strings).
secret() { openssl rand -base64 96 | tr -dc 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789' | head -c "${1:-32}"; }

write_file() { # path, content-from-stdin
  local path="$1"
  if [ -e "$path" ] && [ "$force" != "1" ]; then
    echo "Exists, kept (FORCE=1 to overwrite): $path" >&2
    cat >/dev/null
    return 1
  fi
  cat >"$path"
  echo "Wrote $path"
}

declare -A values=(
  [ALLOWED_HOSTS]="$public_hostname"
  [POSTGRES_PASSWORD]="$(secret)"
  [CRM_OWNER_PASSWORD]="$(secret)"
  [CRM_APP_PASSWORD]="$(secret)"
  [CONDUCTOR_DB_PASSWORD]="$(secret)"
  [REDIS_PASSWORD]="$(secret)"
  [PLATFORM_ADMIN_EMAIL]="$admin_email"
  [PLATFORM_ADMIN_NAME]="$admin_name"
)

while IFS= read -r line || [ -n "$line" ]; do
  if [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]] && [[ -v "values[${BASH_REMATCH[1]}]" ]]; then
    printf '%s=%s\n' "${BASH_REMATCH[1]}" "${values[${BASH_REMATCH[1]}]}"
  else
    printf '%s\n' "$line"
  fi
done <"$here/.env.example" | write_file "$out/.env" || echo "WARNING: .env was NOT regenerated; existing passwords are unchanged." >&2

# PKCS#1 "RSA PRIVATE KEY" PEM (the API also accepts PKCS#8). World-readable inside the 0700 directory so the container user (uid 1654)
# can read the bind-mounted file; the directory permission is what keeps other host users out.
if [ ! -e "$out/secrets/jwt-signing-key.pem" ] || [ "$force" = "1" ]; then
  openssl genrsa -traditional -out "$out/secrets/jwt-signing-key.pem" 2048 2>/dev/null || openssl genrsa -out "$out/secrets/jwt-signing-key.pem" 2048 2>/dev/null
  chmod 644 "$out/secrets/jwt-signing-key.pem"
  echo "Wrote $out/secrets/jwt-signing-key.pem"
else
  echo "Exists, kept (FORCE=1 to overwrite): $out/secrets/jwt-signing-key.pem" >&2
fi

secret 24 | write_file "$out/secrets/platform-admin-password" || true
chmod 644 "$out/secrets/platform-admin-password" 2>/dev/null || true

# Observability overlay (docker-compose.observability.yml, C-OPS1): scrape bearer token, Grafana admin password, postgres-exporter role password.
for name in metrics-bearer-token grafana-admin-password pg-monitor-password; do
  secret 40 | write_file "$out/secrets/$name" || true
  chmod 644 "$out/secrets/$name" 2>/dev/null || true
done
chmod 600 "$out/.env" 2>/dev/null || true

cat <<'EOF'

Done. Next steps:
  1. Review deploy/.env (ALLOWED_HOSTS, WEB_BIND/WEB_PORT, PLATFORM_ADMIN_EMAIL, resource limits).
  2. Store .env and secrets/ in your secret vault / encrypted backup. The JWT key and DB passwords are needed to restore.
  3. docker compose -f deploy/docker-compose.prod.yml up -d ; then create the first platform admin:
       docker compose -f deploy/docker-compose.prod.yml run --rm migrator create-platform-admin
     The password is in deploy/secrets/platform-admin-password (read it once, then empty the file).
EOF
