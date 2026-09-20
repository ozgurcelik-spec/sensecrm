#!/bin/sh
# CRM - nesne deposu (MinIO) tek seferlik/idempotent hazirlik betigi (M8C, K19). Uretim ve gelistirme compose'u ayni betigi calistirir (mc imaji, `sh init.sh`).
#
# Yaptiklari (her `up`ta tekrar calisir, idempotent):
#   1. kovayi olusturur (yoksa),
#   2. kovada varsayilan SSE-S3 sifrelemesini ayarlar (Files:Storage:Encryption=required iken API bunu dogrular),
#   3. YALNIZ bu kovaya izinli bir politika ve uygulama hesabini (servis hesabi) olusturur/gunceller.
# Uygulamada kok kimlik bilgisi YOKTUR: uygulama hesabi kova olusturamaz/silemez, yonetim API'sine erisemez.
#
# Sirlar (deger ya da <AD>_FILE ile dosyadan; uretimde Docker secret dosyalari /run/secrets/...):
#   MINIO_ROOT_USER, MINIO_ROOT_PASSWORD        MinIO kok hesabi (yalniz bu betik ve yonetici kullanir)
#   MINIO_APP_ACCESS_KEY, MINIO_APP_SECRET_KEY  uygulama hesabi (API/Worker/Migrator'a Files__Storage__AccessKey/SecretKey olarak verilir)
# Ayarlar: MINIO_ENDPOINT (varsayilan http://minio:9000), MINIO_BUCKET (varsayilan crm-files), MINIO_ENCRYPTION (required|none; varsayilan required).
set -eu

secret() {
  name="$1"
  eval "direct=\${$name:-}"
  if [ -n "$direct" ]; then
    printf '%s' "$direct"
    return
  fi
  eval "file=\${${name}_FILE:-}"
  if [ -n "$file" ] && [ -r "$file" ]; then
    # Sondaki satir sonlarini at (secret dosyalari genelde yeni satirla biter).
    tr -d '\r\n' < "$file"
    return
  fi
  echo "init: $name (or ${name}_FILE) is required" >&2
  exit 1
}

ENDPOINT="${MINIO_ENDPOINT:-http://minio:9000}"
BUCKET="${MINIO_BUCKET:-crm-files}"
ENCRYPTION="${MINIO_ENCRYPTION:-required}"
ROOT_USER="$(secret MINIO_ROOT_USER)"
ROOT_PASSWORD="$(secret MINIO_ROOT_PASSWORD)"
APP_ACCESS="$(secret MINIO_APP_ACCESS_KEY)"
APP_SECRET="$(secret MINIO_APP_SECRET_KEY)"

# MinIO'nun hazir olmasini bekle (compose healthcheck'e ek guvence).
i=0
until mc alias set local "$ENDPOINT" "$ROOT_USER" "$ROOT_PASSWORD" >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -ge 60 ]; then
    echo "init: MinIO did not become ready at $ENDPOINT" >&2
    exit 1
  fi
  sleep 2
done

mc mb --ignore-existing "local/$BUCKET"
if [ "$ENCRYPTION" = "required" ]; then
  # Statik KMS anahtari (MINIO_KMS_SECRET_KEY_FILE) gerekir; yoksa bu komut hata verir ve yayin durur (sessizce sifresiz kalinmaz).
  mc encrypt set sse-s3 "local/$BUCKET"
fi

POLICY_FILE="$(mktemp)"
cat > "$POLICY_FILE" <<POLICY
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:GetBucketLocation",
        "s3:ListBucket",
        "s3:GetEncryptionConfiguration",
        "s3:GetObject",
        "s3:PutObject",
        "s3:DeleteObject"
      ],
      "Resource": ["arn:aws:s3:::$BUCKET", "arn:aws:s3:::$BUCKET/*"]
    }
  ]
}
POLICY

mc admin policy create local crm-files-app "$POLICY_FILE"
# `user add` mevcut kullanicinin parolasini gunceller (idempotent); politika eklemesi tekrarinda "zaten ekli" uyarisi normaldir.
mc admin user add local "$APP_ACCESS" "$APP_SECRET"
mc admin policy attach local crm-files-app --user "$APP_ACCESS" || true
rm -f "$POLICY_FILE"

echo "init: bucket '$BUCKET' ready (encryption: $ENCRYPTION), application account '$APP_ACCESS' limited to this bucket."
