#!/usr/bin/env bash
# Logical backup of the "crm" and "conductor" databases: pg_dump (plain SQL, no owners/privileges/comments; schema "public" of the crm database is left out because
# extensions and helper functions there are created by the db-init job, so a restore never collides with them) -> gzip -> timestamped file,
# integrity-checked, MANDATORY encryption, retention. Works against the running production stack (docker compose exec on the postgres service).
#
# Encryption is required: the script REFUSES to run (exit 2, before touching Docker) unless one of these is set, or --no-encryption is passed.
#   BACKUP_GPG_RECIPIENT=backup@company.local ./deploy/backup.sh      # encrypt with gpg (public key must be in the keyring) -> *.sql.gz.gpg
#   BACKUP_OPENSSL_PASSFILE=/root/.crm-backup-pass ./deploy/backup.sh # or AES-256 with a passphrase file (openssl enc -pbkdf2) -> *.sql.gz.enc
#   BACKUP_DIR=/mnt/nas/crm RETENTION_DAYS=30 BACKUP_GPG_RECIPIENT=... ./deploy/backup.sh
#   ./deploy/backup.sh --no-encryption                                # explicit opt-out: the backup is stored in CLEAR TEXT (loud warning)
#
# Not included (back them up separately, encrypted): deploy/.env and deploy/secrets/ (JWT signing key, DB passwords). Without the JWT key
# a restored installation still works, but every user has to sign in again.
# Cron example (daily 02:30):  30 2 * * *  cd /opt/crm && ./deploy/backup.sh >> /var/log/crm-backup.log 2>&1
set -euo pipefail

no_encryption=0
for arg in "$@"; do
  case "$arg" in
    --no-encryption) no_encryption=1 ;;
    *) echo "unknown argument: $arg (only --no-encryption is accepted)" >&2; exit 2 ;;
  esac
done

# Encryption is mandatory. Checked BEFORE any docker command so a misconfigured run never produces (or half-produces) a clear-text dump.
if [ "$no_encryption" = 1 ]; then
  echo "WARNING: --no-encryption given: the backups are stored in CLEAR TEXT (all customer data, password hashes, tokens). Protect the target directory, or use BACKUP_GPG_RECIPIENT / BACKUP_OPENSSL_PASSFILE." >&2
elif [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
  command -v gpg >/dev/null || { echo "BACKUP_GPG_RECIPIENT is set but gpg is not installed" >&2; exit 2; }
elif [ -n "${BACKUP_OPENSSL_PASSFILE:-}" ]; then
  command -v openssl >/dev/null || { echo "BACKUP_OPENSSL_PASSFILE is set but openssl is not installed" >&2; exit 2; }
  [ -s "$BACKUP_OPENSSL_PASSFILE" ] || { echo "BACKUP_OPENSSL_PASSFILE is not a readable, non-empty file: $BACKUP_OPENSSL_PASSFILE" >&2; exit 2; }
else
  {
    echo "REFUSING to run: backup encryption is mandatory and no key is configured."
    echo "  set BACKUP_GPG_RECIPIENT=<gpg key id / e-mail>        (public key in the keyring), or"
    echo "  set BACKUP_OPENSSL_PASSFILE=<path to a passphrase file> (AES-256, openssl enc -pbkdf2),"
    echo "or pass --no-encryption to knowingly store the backup in clear text."
  } >&2
  exit 2
fi

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
  if [ "$no_encryption" = 1 ]; then
    echo "WARNING: $target is stored in CLEAR TEXT"
  elif [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
    gpg --batch --yes --encrypt --recipient "$BACKUP_GPG_RECIPIENT" --output "$target.gpg" "$target" \
      || { rm -f "$target" "$target.gpg"; echo "gpg encryption failed; the clear-text dump was removed" >&2; exit 1; }
    rm -f "$target"
    target="$target.gpg"
  else
    openssl enc -aes-256-cbc -pbkdf2 -salt -pass "file:$BACKUP_OPENSSL_PASSFILE" -in "$target" -out "$target.enc" \
      || { rm -f "$target" "$target.enc"; echo "openssl encryption failed; the clear-text dump was removed" >&2; exit 1; }
    rm -f "$target"
    target="$target.enc"
  fi
  chmod 600 "$target" 2>/dev/null || true
  echo "[$(date +%T)] wrote $target ($(du -h "$target" | cut -f1))"
done

# Object storage (M8C): mirror the file bucket AFTER the database dumps (order matters: a restore may then find an object without a row = a harmless orphan that
# reconciliation removes, never a row without its object). The mirror is plaintext (SSE-S3 is transparent to readers) -> it is packed into
# files-<stamp>.tar.gz and ENCRYPTED WITH THE SAME OPTIONS (gpg / openssl) as the database dumps; encrypting this archive is mandatory.
# The bucket is encrypted at rest with the KMS key in deploy/secrets/minio-kms-key: back that key up together with these archives (restore needs the same key).
files_dir="$backup_dir/files-$stamp"
mkdir -p "$files_dir"
echo "[$(date +%T)] mirroring the object bucket ..."
"${compose[@]}" run --rm --no-deps -T -v "$files_dir:/backup" --entrypoint /bin/sh minio-init -c 'mc alias set local "$MINIO_ENDPOINT" "$(cat /run/secrets/minio_root_user)" "$(cat /run/secrets/minio_root_password)" >/dev/null && mc mirror --overwrite --quiet "local/${MINIO_BUCKET}" /backup'
target="$backup_dir/files-$stamp.tar.gz"
tar -C "$backup_dir" -czf "$target" "files-$stamp"
rm -rf "$files_dir"
gzip -t "$target"
if [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
  gpg --batch --yes --encrypt --recipient "$BACKUP_GPG_RECIPIENT" --output "$target.gpg" "$target" && rm -f "$target"
  target="$target.gpg"
elif [ -n "${BACKUP_OPENSSL_PASSFILE:-}" ]; then
  openssl enc -aes-256-cbc -pbkdf2 -salt -pass "file:$BACKUP_OPENSSL_PASSFILE" -in "$target" -out "$target.enc" && rm -f "$target"
  target="$target.enc"
else
  echo "WARNING: the object backup $target is NOT encrypted (set BACKUP_GPG_RECIPIENT or BACKUP_OPENSSL_PASSFILE); it holds every uploaded file in plaintext." >&2
fi
chmod 600 "$target" 2>/dev/null || true
echo "[$(date +%T)] wrote $target ($(du -h "$target" | cut -f1))"

# Retention: only files this script created (name pattern), older than RETENTION_DAYS.
find "$backup_dir" -maxdepth 1 -type f \( -name 'crm-*.sql.gz*' -o -name 'conductor-*.sql.gz*' -o -name 'files-*.tar.gz*' \) -mtime "+$retention_days" -print -delete | sed 's/^/pruned /'
echo "[$(date +%T)] backup finished"
