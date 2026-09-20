# Geri yükleme (restore) — doğrulanmış adımlar

Bu adımlar `deploy/backup.sh` / `deploy/backup.ps1` çıktısı (`crm-<zaman>.sql.gz`, `conductor-<zaman>.sql.gz`) ile **gerçek bir paketli
yığında** uygulanıp doğrulanmıştır: yedek alındı, boş bir örneğe (ayrı proje adı, ayrı volume) geri yüklendi, yığın ayağa kalktı,
pilot yöneticisi eski parolasıyla girdi, lead/kural/workflow yürütme kayıtları geri geldi ve yeni yazma (crm_app rolü) çalıştı.

Senaryo B'nin "aynı örnekte veritabanını bırakıp yeniden yükleme" yolu da aynı yığında denenmiştir (yeniden yüklemeden sonra `deploy/smoke.sh` geçti).

Kısaca: **roller ve veritabanları `db-init` ile yeniden yaratılır, veri yedekten yüklenir, şema/izinler `up` ile tamamlanır.**
Yedek dosyası rol/sahiplik içermez (`--no-owner --no-privileges`); nesneler geri yükleyen role (`crm_owner`, `conductor`) ait olur ve
`db-init`'in varsayılan izinleri `crm_app` için otomatik geçerli olur.

Aşağıda `DC` kısaltması kullanılır:

```bash
DC="docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env"
```

## Senaryo A — yeni/boş sunucuya (felaket kurtarma) ya da prova

1. Gizlileri geri getirin: `deploy/.env` ve `deploy/secrets/` (JWT anahtarı dâhil) — şifreli yedekten. Aynı JWT anahtarı verilirse kullanıcılar
   oturumlarını korur; anahtar kaybolduysa `generate-secrets` ile yenisi üretilir, herkes yeniden giriş yapar (veri etkilenmez).
   **Farklı** parolalar üretilecekse bile sorun yoktur: `db-init` rol parolalarını `.env` ile eşitler.
2. Yalnız veritabanı katmanını başlatın (roller + boş `crm` ve `conductor` veritabanları oluşur):

   ```bash
   $DC up -d postgres db-init
   $DC ps -a        # db-init: Exited (0) olmalı
   ```

3. Yedekleri yükleyin (tek transaction, ilk hatada durur). Yerel yedek dizini `BACKUPS` olsun.
   **Yedekler varsayılan olarak şifrelidir** (`backup.sh`/`backup.ps1` şifreleme anahtarı olmadan çalışmaz; `.sql.gz.gpg` / `.sql.gz.enc`): önce
   şifreyi çözün (bkz. Notlar), aşağıdaki örneklerdeki `.sql.gz` dosyası çözülmüş dosyadır:

   ```bash
   gzip -dc "$BACKUPS"/crm-20260919-233631.sql.gz \
     | $DC exec -T postgres psql -X -q -U crm_owner -d crm -v ON_ERROR_STOP=1 --single-transaction
   gzip -dc "$BACKUPS"/conductor-20260919-233631.sql.gz \
     | $DC exec -T postgres psql -X -q -U conductor -d conductor -v ON_ERROR_STOP=1 --single-transaction
   ```

   PowerShell (Windows) — ikili veri boru hattından geçmesin diye önce konteynere kopyalayın:

   ```powershell
   $dc = @('compose','-f','deploy/docker-compose.prod.yml','--env-file','deploy/.env')
   foreach ($p in @(@('crm','crm_owner','crm'), @('conductor','conductor','conductor'))) {
       $file = (Get-ChildItem "$BACKUPS\$($p[0])-*.sql.gz" | Sort-Object Name | Select-Object -Last 1).FullName
       & docker @dc cp $file "postgres:/tmp/restore.sql.gz"
       & docker @dc exec -T postgres sh -c "gunzip -f /tmp/restore.sql.gz && psql -X -q -U $($p[1]) -d $($p[2]) -v ON_ERROR_STOP=1 --single-transaction -f /tmp/restore.sql && rm -f /tmp/restore.sql"
   }
   ```

4. Yığının geri kalanını başlatın. `db-init` yeniden çalışıp geri yüklenen nesnelere `crm_app` izinlerini verir, migrator bekleyen
   migration varsa uygular (yedek eski sürümdense şema yükselir):

   ```bash
   $DC up -d
   ```

5. Doğrulayın: `BASE_URL=http://localhost:8080 ADMIN_EMAIL=... ADMIN_PASSWORD=... ./deploy/smoke.sh`, ardından arayüzden son kayıtları kontrol edin.

## Senaryo B — aynı sunucuda yanlışlıkla silinen/bozulan veri (mevcut veritabanının üzerine)

Yedekten yalnız **belirli** verileri kurtarmak en güvenlisidir: yedeği ayrı bir örneğe yükleyin (Senaryo A, farklı proje adı ve
`WEB_PORT`/alt ağlar ile: `COMPOSE_PROJECT_NAME=crm-restore`, `BACKEND_SUBNET`, `FRONTEND_SUBNET`, `EDGE_SUBNET` değiştirilmiş bir `.env` kopyası),
gerekli kayıtları oradan okuyup asıl sisteme arayüz/API ile geri girin, sonra prova örneğini kaldırın:

```bash
docker compose -p crm-restore -f deploy/docker-compose.prod.yml --env-file /path/to/.env.restore down -v
```

Tüm veritabanını **aynı** örnekte geri yüklemek gerekiyorsa (kabul edilmiş veri kaybıyla): bakım penceresi açın, `api` ve `worker`'ı durdurun,
mevcut durumun bir yedeğini alın, veritabanlarını `postgres` üzerinden bırakıp yeniden yaratın ve Senaryo A'nın 2–4. adımlarını izleyin:

```bash
$DC stop web api worker conductor
$DC exec -T postgres psql -U postgres -d postgres -c "DROP DATABASE crm" -c "DROP DATABASE conductor"
$DC up -d db-init    # roller/veritabanları yeniden yaratılır
# 3. ve 4. adım
```

## Nesne deposu (dosya ekleri, M8C) — geri yükleme

`backup.sh/ps1` **önce** veritabanlarını, **sonra** nesne kovasını yedekler (`files-<zaman>.tar.gz`, şifreli olmalı). Sıra bilinçlidir: geri yüklemede satırı olmayan nesne
(yetim; zararsız, uzlaştırma siler) satırı olup nesnesi olmayan (`missing`) durumdan iyidir. Nesne kovası **KMS anahtarı olmadan okunamaz**: `deploy/secrets/minio-kms-key` aynı
olmalıdır (yeni anahtar üretilirse eski nesneler açılamaz).

1. Senaryo A'nın 1–3. adımlarını yapın (gizliler dâhil `minio-*` dosyaları; **aynı** `minio-kms-key`).
2. Nesne deposunu başlatın ve kovayı hazırlayın: `$DC up -d minio minio-init` (kova + şifreleme + uygulama hesabı; idempotent).
3. Arşivi açıp kovaya geri koyun (mc konteyneri, `backend` ağı; arşiv `.gpg`/`.enc` ise önce çözün):

   ```bash
   tar -xzf files-20260920-023000.tar.gz -C "$RESTORE_TMP"          # -> $RESTORE_TMP/files-20260920-023000/
   $DC run --rm --no-deps -T -v "$RESTORE_TMP/files-20260920-023000:/backup:ro" --entrypoint /bin/sh minio-init -c \
     'mc alias set local "$MINIO_ENDPOINT" "$(cat /run/secrets/minio_root_user)" "$(cat /run/secrets/minio_root_password)" >/dev/null && mc mirror --overwrite /backup "local/${MINIO_BUCKET}"'
   ```

4. Yığını başlatın (`$DC up -d`), sonra **sırayla**: `$DC run --rm migrator erase-deleted-tenants` (geri gelen imha edilmiş kiracının nesnelerini yeniden siler),
   `$DC run --rm migrator files-reconcile --dry-run` (çıktıyı gözden geçirin: `missing`, `orphans`, `guardTripped`), sonra `$DC run --rm migrator files-reconcile`.
   **Yetim silme güvenlik supabı** (`Files:Reconcile:MaxOrphanDeletePerRun` 1000 / `MaxOrphanDeleteFraction` %5) yanlış oranda (ör. eski bir nesne yedeği) toplu silmeyi durdurur; `guardTripped=True` görürseniz nedeni inceleyin.
5. Denetim: `files-reconcile --dry-run` çıktısında `missing = 0` ve `select count(*) from files.attachments where state = 'missing'` = 0 (geri yükleme provası "ek sayısı = nesne sayısı" denetimi).

## Notlar

- **Süre:** küçük veritabanında (pilot başlangıcı) yedek ve geri yükleme her biri saniyeler sürer; süre veri boyutuyla doğrusal büyür — RTO'yu
  gerçek boyutla prova edin (`docs/operations/runbook.md` §13).
- **Kapsam dışı:** `deploy/.env` ve `deploy/secrets/*` (yedekle birlikte, ama ayrı ve şifreli saklanır); Docker imajları (sürüm etiketinden yeniden kurulur).
- **Şifreli yedek (zorunlu):** yedek betikleri anahtar verilmeden **çalışmayı reddeder** (çıkış kodu 2). Anahtar: `BACKUP_GPG_RECIPIENT=<anahtar kimliği/e-posta>`
  (`backup.ps1`: `-GpgRecipient` ya da aynı ortam değişkeni; genel anahtar gpg anahtarlığında olmalı) veya yalnız `backup.sh` için
  `BACKUP_OPENSSL_PASSFILE=<parola dosyası>` (AES-256). Bilerek şifresiz almak için `backup.sh --no-encryption` / `backup.ps1 -NoEncryption`
  (yedek **açık metin** saklanır; betik yüksek sesle uyarır). Çözme: `.gpg` dosyaları `gpg --output crm-....sql.gz --decrypt crm-....sql.gz.gpg` (özel anahtar gerekir),
  `.enc` dosyaları `openssl enc -d -aes-256-cbc -pbkdf2 -pass file:<parola-dosyası> -in crm-....sql.gz.enc -out crm-....sql.gz` ile açılır.
  **Şifre çözme anahtarını/parolayı yedeklerden ayrı ve güvenli saklayın; kaybolursa yedek geri yüklenemez.**
- **Başka bir PostgreSQL ana sürümüne** geçişte düz SQL yedek uygundur (mantıksal yedek sürümler arası taşınabilir).
