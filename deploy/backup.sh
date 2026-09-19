#!/usr/bin/env bash
# Logical backup of the "crm" and "conductor" databases: pg_dump (plain SQL, no owners/privileges/comments; schema "public" of the crm database is left out because
# extensions and helper functions there are created by the db-init job, so a restore never collides with them) -> gzip -> timestamped file,
# integrity-checked, optional encryption, retention. Works against the running production stack (docker compose exec on the postgres service).
#
#   ./deploy/backup.sh                                   # -> deploy/backups/{crm,conductor}-YYYYmmdd-HHMMSS.sql.gz
#   BACKUP_DIR=/mnt/nas/crm RETENTION_DAYS=30 ./deploy/backup.sh
#   BACKUP_GPG_RECIPIENT=backup@company.local ./deploy/backup.sh     # also encrypt with gpg (public key must be in the keyring)
#   BACKUP_OPENSSL_PASSFILE=/root/.crm-backup-pass ./deploy/backup.sh # or AES-256 with a passphrase file (openssl enc -pbkdf2)
#
# Not included (back them up separately, encrypted): deploy/.env and deploy/secrets/ (JWT signing key, DB passwords). Without the JWT key
# a restored installation still works, but every user has to sign in again.
# Cron example (daily 02:30):  30 2 * * *  cd /opt/crm && ./deploy/backup.sh >> /var/log/crm-backup.log 2>&1
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="${ENV_FILE:-$here/.env}"
backup_dir="${BACKUP_DIR:-$here/backups}"
retention_days="${RETENTION_DAYS:-14}"
compose=(docker compose -f "$here/docker-compose.prod.yml")
[ -f "$env_file" ] && compose+=(--env-file "$env_file")

mkdir -p "$backup_dir"
chmod 700 "$backup_dir" 2>/dev/null || true
stamp="$(date +%Y%m%d-%H%M%S)"

for db in crm conductor; do
  target="$backup_dir/$db-$stamp.sql.gz"
  # crm: everything lives in module schemas (public holds only db-init helpers). conductor: its tables ARE in public, so nothing is excluded.
  exclude=""; [ "$db" = crm ] && exclude="--exclude-schema=public"
  echo "[$(date +%T)] dumping $db ..."
  # Dump inside the container into its /tmp (binary-safe on every host), copy it out, delete the temp file.
  "${compose[@]}" exec -T postgres sh -c "pg_dump -U postgres --no-owner --no-privileges --no-comments $exclude -f /tmp/$db.sql $db && gzip -9 -f /tmp/$db.sql"
  "${compose[@]}" cp "postgres:/tmp/$db.sql.gz" "$target"
  "${compose[@]}" exec -T postgres rm -f "/tmp/$db.sql.gz"

  gzip -t "$target"   # fails (and stops the script) on a truncated/corrupt archive
  if [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
    gpg --batch --yes --encrypt --recipient "$BACKUP_GPG_RECIPIENT" --output "$target.gpg" "$target" && rm -f "$target"
    target="$target.gpg"
  elif [ -n "${BACKUP_OPENSSL_PASSFILE:-}" ]; then
    openssl enc -aes-256-cbc -pbkdf2 -salt -pass "file:$BACKUP_OPENSSL_PASSFILE" -in "$target" -out "$target.enc" && rm -f "$target"
    target="$target.enc"
  fi
  chmod 600 "$target" 2>/dev/null || true
  echo "[$(date +%T)] wrote $target ($(du -h "$target" | cut -f1))"
done

# Retention: only files this script created (name pattern), older than RETENTION_DAYS.
find "$backup_dir" -maxdepth 1 -type f \( -name 'crm-*.sql.gz*' -o -name 'conductor-*.sql.gz*' \) -mtime "+$retention_days" -print -delete | sed 's/^/pruned /'
echo "[$(date +%T)] backup finished"
