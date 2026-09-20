# CRM — Operasyon el kitabı (pilot yayın)

Kapsam: kurum içi / kendi veri merkezinde Docker Compose ile tek sunuculu kurulum (Milestone 5). Kubernetes/Helm hedefi (K13) sonraki iştir.
Dosyalar: [`deploy/`](../../deploy) (compose, `.env.example`, sır üreticiler, yedek/geri yükleme, TLS örnekleri),
[`deploy/restore.md`](../../deploy/restore.md) (geri yükleme adımları), mimari: [backend.md](../architecture/backend.md), plan: [m5-pilot-yayin.md](../plan/m5-pilot-yayin.md).

## 1. Genel bakış

```
kullanıcı ──HTTPS──▶ [TLS sonlandırıcı: Caddy/nginx/LB]  (sizin; deploy/tls/*.example)
                          │ HTTP, X-Forwarded-For/-Proto
                          ▼
   frontend ağı ─────▶ web  (nginx-unprivileged :8080; tek yayınlanan port, varsayılan 127.0.0.1:8080)
                          │ /api/*  (yalnız bu yol)
   backend ağı (internal: dışarıya çıkışı YOK)
                          ▼
                    api ──▶ postgres (crm, conductor veritabanları) ◀── conductor (workflow motoru)
                    worker ──▶ postgres, conductor            migrator, db-init (tek seferlik işler)
                    redis (isteğe bağlı, profil "redis")
```

| Servis | İmaj | Görev | Kaynak sınırı (varsayılan) |
|---|---|---|---|
| `postgres` | `postgres:17.11-alpine` | `crm` + `conductor` veritabanları, `pgdata` volume | 2 GB / 2 CPU |
| `db-init` | `postgres:17.11-alpine` | Roller (`crm_owner`, `crm_app`, `conductor`), veritabanları, izinler — **her `up`'ta**, idempotent | — |
| `conductor` | `conductoross/conductor:3.32.4` | Workflow motoru (Redis/Elasticsearch yok) | 1,5 GB / 1,5 CPU |
| `migrator` | `crm-migrator` | EF migration'ları (owner rolüyle), `create-platform-admin` | — |
| `api` | `crm-api` | REST API (`/api/v1`), sağlık uçları | 1 GB / 2 CPU |
| `worker` | `crm-worker` | Outbox işleyici, Conductor görev işleyicileri, yürütme durumu senkronu | 768 MB / 1 CPU |
| `web` | `crm-web` | SPA + `/api` ters vekil | 128 MB / 0,5 CPU |
| `redis` | `redis:7.4.11-alpine` | Yalnız birden çok `api` kopyasında (önbellek L2) | 384 MB |
| `minio` | `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z` | **M8C:** dosya ekleri nesne deposu (S3 uyumlu). **Yalnız `backend` ağı, port yayınlamaz**, konsol kapalı, KMS anahtarıyla SSE-S3; bkz. §15 | 1 GB |
| `minio-init` | `quay.io/minio/mc:RELEASE.2025-08-13T08-35-41Z` | Tek seferlik/idempotent: kova + varsayılan şifreleme + **yalnız bu kovaya** izinli uygulama hesabı (`deploy/minio/init.sh`) — **her `up`'ta** | — |

Güvenlik duruşu (varsayılan): yalnız `web` portu yayınlanır; `backend` ağı `internal: true` olduğundan API/Worker/Postgres/Conductor dışarıya bağlantı
başlatamaz; parola yok — sırlar `.env`/`secrets/` içindedir ve `:?` ile zorunludur; API ve Worker veritabanına yalnız DML yetkili `crm_app` rolüyle
bağlanır (DDL yalnız migrator'ın `crm_owner` rolündedir); Conductor kendi rolüyle yalnız `conductor` veritabanına erişir; API dokümantasyonu (Scalar/OpenAPI)
kapalıdır; herkese açık kayıt kapalıdır; tüm süreçler root olmayan kullanıcıyla çalışır.

## 2. Ön koşullar

- Linux sunucu (önerilen) veya Windows Server + Docker; **Docker Engine 24+ ve Compose v2.20+** (`docker compose version`).
- Pilot için başlangıç: 4 vCPU, 8 GB RAM, 50 GB SSD (`pgdata` ve yedekler için ayrı disk/NAS önerilir).
- DNS kaydı (ör. `crm.sirket.local`) ve iç CA'dan TLS sertifikası (bkz. §3.7).
- İmajlar: kaynak koddan derlenir (`docker compose ... build`, internet erişimi ilk derlemede NuGet/npm/base image için gerekir) **veya** kurum içi
  bir registry'den çekilir (`CRM_REGISTRY`, `CRM_VERSION`). Üretim çalışırken dışa çıkış gerekmez.

## 3. Kurulum

### 3.1 Dosyalar ve sırlar

```bash
git clone <depo> /opt/crm && cd /opt/crm
# Linux:
PUBLIC_HOSTNAME=crm.sirket.local PLATFORM_ADMIN_EMAIL=platform@sirket.local ./deploy/generate-secrets.sh
# Windows (PowerShell 5.1+):
.\deploy\generate-secrets.ps1 -PublicHostname crm.sirket.local -PlatformAdminEmail platform@sirket.local
```

Üretilenler: `deploy/.env` (5 rastgele parola, 32 karakter), `deploy/secrets/jwt-signing-key.pem` (RSA-2048, JWT imzası),
`deploy/secrets/platform-admin-password` (ilk platform yöneticisi için tek seferlik parola). Mevcut dosyalar `-Force`/`FORCE=1` olmadan ezilmez.
Elle kurulum: `cp deploy/.env.example deploy/.env` ve boş bırakılan sırları doldurun — boş sır varsa compose başlamaz (bilinen bir parolayla çalışamaz).
`.env` içinde `;`, tırnak ve boşluk kullanmayın (bağlantı dizelerine gömülüyor).

`.env`'de gözden geçirin: `ALLOWED_HOSTS` (kullanıcıların yazdığı ad; API başka `Host` başlıklarını 400 ile reddeder), `WEB_BIND`/`WEB_PORT`,
`TRUSTED_PROXY_CIDR` (TLS vekilinin adresi/ağı), kaynak sınırları, `CRM_VERSION`.
**`.env` ve `secrets/` git'e girmez (`.gitignore`), kasada/şifreli yedekte saklanır; JWT anahtarı ve parolalar geri yükleme için gereklidir.**

### 3.2 İmajlar

```bash
docker compose -f deploy/docker-compose.prod.yml build          # api, worker, migrator, web (CRM_VERSION etiketiyle)
# veya registry'den: CRM_REGISTRY=registry.sirket.local/crm/ CRM_VERSION=1.0.0 docker compose ... pull
```

### 3.3 Başlatma

```bash
docker compose -f deploy/docker-compose.prod.yml up -d
docker compose -f deploy/docker-compose.prod.yml ps -a
```

Sıra: `postgres` sağlıklı → `db-init` (Exited 0) → `migrator` (Exited 0; **başarısızsa api/worker hiç başlamaz**) → `api`, `worker` → `web`.
İlk açılışta Conductor ~60 sn'de hazır olur (`health: starting` normaldir). Migrator günlüğündeki tek seferlik
`libgssapi_krb5.so.2` uyarısı zararsızdır (Npgsql Kerberos'u dener, kullanılmaz).

### 3.4 İlk platform yöneticisi

Herkese açık kayıt kapalı olduğundan (§5) ilk hesap operasyon aracıyla açılır (idempotent; parola komut satırına/ortama yazılmaz, `secrets/platform-admin-password`
dosyasından okunur ve hiçbir yerde loglanmaz):

```bash
docker compose -f deploy/docker-compose.prod.yml run --rm migrator create-platform-admin
```

- Hesap yoksa: hesap + "Platform" adlı işletim organizasyonu (yalnız bu yönetici hesabı; müşteri verisi yok) + Administrator üyeliği oluşur.
- Hesap varsa: parolasına dokunulmadan platform yöneticisi yapılır (yükseltme); zaten yöneticiyse hiçbir şey değişmez (çıkış kodu 0).
- Geçersiz girdi (e-posta yok/parola < 12 karakter ya da yaygın/e-posta adını içeren parola) → çıkış kodu 2 ve neden günlükte.
- Bittikten sonra parola dosyasını **boşaltın** (`: > deploy/secrets/platform-admin-password`). Parola **sıfırlama** (unutulan parola) e-posta altyapısıyla birlikte sonraki aşamadadır
  (parola değiştirme uçtan mevcuttur: `POST /me/password`, mevcut parolayı bilen kullanıcı için; unutulan parolayı yalnız sistem yöneticisi DB/`create-platform-admin` ile kurtarır) — bu yüzden platform yöneticisi için güçlü bir parola (≥ 12 karakter, yaygın parola/e-posta adı yasak) kullanın ve önce kasaya kaydedin.

### 3.5 İlk organizasyon (pilot şirket) ve yöneticisi

Platform yöneticisi olarak giriş yapıp API ile açın (`adminPassword: null` → tek seferlik parola üretilir ve **yalnızca bu yanıtta** döner; yanıt `Cache-Control: no-store`):

```bash
BASE=https://crm.sirket.local
PLATFORM_PW="$(cat deploy/secrets/platform-admin-password)"
TOKEN=$(curl -s -X POST $BASE/api/v1/auth/login -H 'Content-Type: application/json' \
  -d "{\"email\":\"platform@sirket.local\",\"password\":\"$PLATFORM_PW\"}" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')
curl -s -X POST $BASE/api/v1/platform/organizations -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"organizationName":"Pilot Holding","adminDisplayName":"Ayşe Yılmaz","adminEmail":"ayse@sirket.local","adminPassword":null,"locale":"tr"}'
# -> {"organizationId":"...","slug":"pilot-holding","adminEmail":"ayse@sirket.local","adminAccountCreated":true,"generatedPassword":"..."}
```

PowerShell: `Invoke-RestMethod -Method Post -Uri "$base/api/v1/platform/organizations" -Headers @{Authorization="Bearer $token"} -ContentType 'application/json' -Body $json`.
(`curl` ile Türkçe karakterli gövde gönderirken Windows'ta Git Bash'i değil PowerShell'i kullanın; JSON'u UTF-8 gönderin.)

Kurallar: e-posta zaten bir hesaba aitse hesap değişmez ve hesap sahibinin onayı olmadan yönetici YAPILMAZ: yeni organizasyona **bekleyen** Administrator daveti eklenir (`adminAccountCreated=false`, `adminInvitationPending=true`, parola dönmez; hesap sahibi giriş yapıp Ayarlar/Davetler ekranından kabul eder, `GET /me/invitations` + `POST /me/invitations/{id}/accept`); yeni hesapta parola (üretilmiş ya da verilmiş) **geçicidir**: ilk girişte değiştirilmek zorundadır (`mustChangePassword`; değişene kadar yalnız parola değiştirme uçları çalışır);
`adminPassword` verilirse (≥ 10 karakter, yaygın parola/e-posta adı yasak) yanıtta tekrarlanmaz. Yetki her istekte veritabanından doğrulanır — token'daki bayrak yetmez. Yeni organizasyon
varsayılan satış hunisiyle (Worker) tohumlanır. Üretilen parolayı yöneticiye güvenli kanaldan iletin; ilk girişte değiştirmesini isteyin.

**Plan ve deneme (M7):** gövdeye isteğe bağlı `planCode` (ör. `business`) ve `trialEndsOn` (`YYYY-MM-DD`, dahil) eklenir; verilmezse `Platform:Provisioning:DefaultPlanCode` (varsayılan `internal`: limitsiz, tüm modüller açık) ve denemesiz açılır. Bilinmeyen/pasif plan `400` (`errors.planCode`). Plan, deneme ve limit istisnası sonradan `PUT /api/v1/platform/organizations/{id}/subscription` ile değişir (Platform konsolu); askı/silme talebi aynı yerdedir (§12). Plan hesabı `OrganizationCreated` olayıyla Worker'da açılır; olay işlenene kadar ilk erişimde `Platform:Signup:PlanCode` planıyla geçici (`lazy`) satır açılır ve olay gelince düzeltilir (saniyeler).
Sonraki kullanıcılar organizasyonun kendi yöneticisi tarafından Ayarlar > Kullanıcılar ekranından eklenir: yeni e-posta → sunucu üretimi **tek seferlik geçici parola** ekranda bir kez gösterilir (güvenli kanaldan iletin); mevcut hesap → davet (kullanıcı kabul edene kadar bekleyen).

### 3.6 Doğrulama (duman testi)

```bash
BASE_URL=https://crm.sirket.local ADMIN_EMAIL=ayse@sirket.local ADMIN_PASSWORD='...' ./deploy/smoke.sh
```

healthz → güvenlik başlıkları → API → giriş → `/me` → lead oluştur/oku. (Yerelde `localhost` adını kullanın; IP adresi `AllowedHosts` nedeniyle reddedilir.)
Ardından tarayıcıdan giriş yapın, bir lead açın; İş akışları > Yürütmeler'de kuralın tetiklendiğini görün.

### 3.7 TLS

TLS **konteynerde sonlandırılmaz**; kurum CA'sından sertifikalı bir vekil/LB kullanın: [`deploy/tls/Caddyfile.example`](../../deploy/tls/Caddyfile.example),
[`deploy/tls/nginx-tls.conf.example`](../../deploy/tls/nginx-tls.conf.example) (HAProxy/F5 için eşdeğer: `X-Forwarded-For` ve `X-Forwarded-Proto` gönderin, arka uç `WEB_BIND:WEB_PORT`).
Vekil başka bir makinedeyse: `WEB_BIND`'i sunucunun özel IP'sine ayarlayın (güvenlik duvarı ile yalnız vekile açın) ve `TRUSTED_PROXY_CIDR`'ı vekilin adresine çekin —
yoksa istemci IP'si (hız sınırı, denetim) yanlış görünür. `web` konteyneri `X-Forwarded-Proto: https` gördüğünde HSTS başlığını kendisi ekler.

## 4. Sırlar ve anahtarlar

| Sır | Yeri | Rotasyon |
|---|---|---|
| `POSTGRES_PASSWORD` (süper kullanıcı; yalnız db-init/yedek) | `.env` | `.env`'i değiştirip PostgreSQL'de `ALTER ROLE postgres PASSWORD` + `up -d` (postgres konteyneri ilk kurulumdaki parolayla başladı) |
| `CRM_OWNER_PASSWORD`, `CRM_APP_PASSWORD`, `CONDUCTOR_DB_PASSWORD` | `.env` | `.env`'i değiştirin → `up -d`: `db-init` rol parolalarını eşitler, servisler yeni değerle yeniden yaratılır |
| JWT imza anahtarı (RS256) | `secrets/jwt-signing-key.pem` → `/run/secrets/Auth__SigningKeyPem` | Yeni anahtar → `up -d api`: tüm access token'lar geçersiz olur, kullanıcılar (refresh token ile otomatik ya da yeniden giriş yaparak) devam eder |
| Platform yöneticisi parolası | `secrets/platform-admin-password` (yalnız bootstrap) | Bootstrap sonrası dosyayı boşaltın |
| `REDIS_PASSWORD` | `.env` | Redis kullanılıyorsa `REDIS_CONNECTION` ile birlikte |
| **M8C** `minio-root-user` / `minio-root-password` | `secrets/minio-root-user`, `secrets/minio-root-password` → yalnız `minio` ve `minio-init` | Yeni değer + `up -d minio minio-init`; uygulama hesabı etkilenmez. Uygulamada kök kimlik bilgisi **yoktur** |
| **M8C** `minio-app-access-key` / `minio-app-secret-key` | `secrets/minio-app-*` → `/run/secrets/Files__Storage__AccessKey|SecretKey` (api, worker, migrator) | Dosyaları yenileyip `up -d`: `minio-init` hesabın parolasını günceller (`mc admin user add` idempotent), api/worker yeniden yaratılır |
| **M8C** `minio-kms-key` (`crm-files-key:<base64 32 bayt>`) | `secrets/minio-kms-key` → yalnız `minio` | **Döndürülemez** (ilk sürüm: yeni anahtar = yeniden şifreleme işi, kapsam dışı). **Veri yedeğiyle birlikte yedeklenir; kaybı = dosyaların kaybıdır** (nesneler anahtarsız okunamaz) |

Üretimde API, `Auth:SigningKeyPem` veya `ConnectionStrings:Database` yoksa **başlamaz** (testli: `PlatformApiTests.Production_RefusesToStart_*`);
geçici geliştirme anahtarı yalnız Development/Testing ortamlarındadır.

## 5. Kayıt modu (`Registration:Mode`)

`REGISTRATION_MODE` (varsayılan `disabled`): `POST /api/v1/auth/signup` → `403 auth.signup_disabled` (TR/EN metinli; girdi doğrulamasından önce),
`GET /api/v1/auth/config` → `{ "signupEnabled": false }` (anonim, 60 sn önbellek) ve web "kaydol" bağlantısını gizler / `/signup` sayfasını girişe yönlendirir.
`open` yalnız Development/Testing varsayılanıdır; kurum içi üretimde açmayın. Geçersiz değer API'yi başlatmaz. Organizasyonlar §3.5 ile açılır.

**SaaS (M7):** `open` kip, dış müşterilerin kendi kendine kaydı için **bilinçli** açılır; yeni organizasyon `Platform:Signup:PlanCode` planıyla (varsayılan `starter`, 14 gün deneme, örnek limitler) açılır ve deneme bitince **salt okunur** olur (§12, plan belgesi). Kurum içi kurulumda `disabled` kalır ve organizasyonlar §3.5 ile `internal` planda açılır. Açık kayıt **CAPTCHA/e-posta doğrulaması içermez** (yalnız IP başına auth hız sınırı vardır); dış SaaS açılışından önce ayrıca ele alınmalıdır.

## 6. Yedekleme

```bash
./deploy/backup.sh                        # Linux;  BACKUP_DIR, RETENTION_DAYS (14), BACKUP_GPG_RECIPIENT | BACKUP_OPENSSL_PASSFILE (isteğe bağlı şifreleme)
.\deploy\backup.ps1 -BackupDir D:\yedek   # Windows; -RetentionDays, -GpgRecipient
```

`crm` ve `conductor` veritabanlarının `pg_dump` (düz SQL, sahiplik/izin yok) → gzip → `crm-YYYYmmdd-HHMMSS.sql.gz`, `gzip -t` ile bütünlük denetimi, saklama
süresini aşan (yalnız bu betiğin adlandırdığı) dosyaların silinmesi. **Zamanlama:** Linux cron `30 2 * * *` (gece 02:30) ve pilot süresince ek olarak iş saatlerinde 4 saatte bir;
Windows Görev Zamanlayıcı: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\crm\deploy\backup.ps1`.
Yedekleri **aynı diskin dışına ve veri merkezi içinde** (NAS/ikinci sunucu) kopyalayın ve şifreleyin (`gpg`/`openssl` seçenekleri veya disk/NAS şifrelemesi).
Ayrı yedeklenecekler: `deploy/.env`, `deploy/secrets/` (şifreli; **M8C: `minio-kms-key` dâhil**). Aylık **geri yükleme provası** yapın (§13).
**M8C nesne kovası:** aynı betikler veritabanı dökümlerinden **sonra** kovayı `mc mirror` ile `files-<zaman>.tar.gz` olarak yedekler (sıra bilinçli: geri yüklemede satırsız nesne = zararsız yetim, nesnesiz satır = `missing`). Nesne yedeği düz metindir (SSE-S3 okuyana şeffaf) → **şifrelenmesi zorunludur** (`BACKUP_GPG_RECIPIENT` / `BACKUP_OPENSSL_PASSFILE`; şifresizse betik uyarır). Geri yükleme: [`deploy/restore.md`](../../deploy/restore.md) "Nesne deposu".

## 7. Geri yükleme

[`deploy/restore.md`](../../deploy/restore.md): boş örneğe (felaket kurtarma/prova) ve aynı örnekte yeniden yükleme adımları — ikisi de gerçek yığında doğrulandı
(yedek → boş örnek → giriş, veri, yeni yazma, iş akışı çalıştı). Özet: `up -d postgres db-init` → `gzip -dc ... | psql -U crm_owner -d crm` (ve `conductor`) → `up -d` → `smoke.sh`.

## 8. Yükseltme (sürüm güncelleme)

1. **Yedek alın** (`backup.sh`) ve mevcut `CRM_VERSION`'ı not edin. Bakım penceresi duyurun (API yeniden başlarken saniyeler süren kesinti olur).
2. Yeni imajları hazırlayın (`CRM_VERSION=<yeni>`; `build` veya `pull`) ve `docker compose ... config -q` ile dosyayı doğrulayın.
3. **Şema önce:** `docker compose -f deploy/docker-compose.prod.yml run --rm migrator migrate` — eski api/worker çalışırken migration'lar uygulanır;
   bu yüzden migration'lar geriye uyumlu yazılır (önce ekle, sonra kullan, en son kaldır). Başarısızsa **devam etmeyin** (adım 6).
   **M7:** `migrate` şemalardan sonra **plan senkronunu** (`Platform:Plans` → `platform.plans`, geçersiz yapılandırma → çıkış 3, yayın durur) ve **backfill**'i (satırı olmayan mevcut her kiracıya `internal` plan; kısıt yok, onboarding kapalı) çalıştırır; ikisi de idempotenttir. Yalnız plan kataloğu yapılandırması değiştiyse şema gerekmez: `docker compose -f deploy/docker-compose.prod.yml run --rm migrator sync-plans` yeter (yapılandırmadan kalkan plan pasifleşir; mevcut atamalar çalışır). `smoke.sh`'a "satırı olmayan kiracı yok" denetimi eklenmelidir (`select count(*) from identity.tenants t left join platform.tenant_accounts a on a.tenant_id = t.id where a.tenant_id is null` = 0).
4. `docker compose -f deploy/docker-compose.prod.yml up -d` — api/worker/web yeni imajla yeniden yaratılır (`db-init` ve `migrator` yeniden çalışır, bekleyen bir şey yoksa işlem yapmaz).
5. `./deploy/smoke.sh` ve arayüzden kontrol; `docker compose ... ps` (hepsi healthy), `docker compose ... logs --since 10m api worker`.
6. **Geri alma:**
   - Yalnız uygulama sorunu (şema değişmedi/geriye uyumlu): `CRM_VERSION`'ı eski etikete çevirip `up -d`.
   - Migration bozduysa veya geriye uyumsuzsa: `stop web api worker`, adım 1'deki yedeği [restore.md](../../deploy/restore.md) Senaryo B ile geri yükleyin, **eski** sürümle `up -d`.
     EF migration'ları ileriye doğrudur (down betiği çalıştırılmaz); veri kaybı yedekten sonraki yazmalar kadardır.
7. Conductor sürümü (`conductoross/conductor:3.32.4`) ayrı bir karardır: sabit kalır; güncellemeden önce provada test edin (workflow tanımları API/Worker açılışında idempotent kaydedilir).

Postgres ana sürümü yükseltme (17 → 18): mantıksal yedek al → yeni sürümle boş volume'a Senaryo A ile yükle → doğrula → eskiyi at.

## 9. İzleme ve sağlık uçları

| Ne | Nasıl | Beklenen |
|---|---|---|
| Web konteyneri | `curl -fsS http://127.0.0.1:8080/healthz` | `ok` (yalnız nginx; API'ye gitmez) |
| API canlılık | `docker compose ... exec api curl -fsS localhost:8080/health/live` | 200 `Healthy` |
| API hazır (Postgres dahil) | `docker compose ... exec api curl -fsS localhost:8080/health/ready` | 200 `Healthy`; `/health` tam rapor (JSON: durum, süre, denetimler) |
| Dış (LB) sağlık kontrolü | `GET /healthz` (vekil ayakta) ve `GET /api/v1/auth/config` (API ayakta) | 200 |
| Worker | `docker inspect --format '{{.State.Health.Status}}' <proje>-worker-1` | `healthy` (15 sn'de bir yenilenen sinyal dosyası; 60 sn eskirse `unhealthy`) |
| Conductor | `docker compose ... exec conductor curl -fsS localhost:8080/health` | `{"healthy":true,...}` |
| **M8C** Nesne deposu | API `/health` tam raporunda `storage` denetimi (erişim + kova + varsayılan şifreleme); `docker compose ... ps minio` | `Healthy`. Depo kapalıyken `Degraded` (**`/health/ready`'ye girmez**: CRM'nin geri kalanı çalışır, dosya uçları `503 file.storage_unavailable`). Disk: `miniodata` doluluğu >%80 uyarısı kurun |
| **M8C** Uzlaştırma sayacı | `migrator files-reconcile --dry-run` çıktısı / `crm.files.reconcile.missing` metriği / `select count(*) from files.attachments where state = 'missing'` | 0 (`> 0` ise §15.5) |
| Tümü | `docker compose -f deploy/docker-compose.prod.yml ps -a` | api/worker/web/postgres/conductor/minio `healthy`; db-init, minio-init, migrator `Exited (0)` |

`/health*` uçları `web` üzerinden dışarı verilmez (yalnız `/api/*` vekil edilir); iç izleme aracınız docker `healthcheck` durumunu veya `exec` çıktısını okuyabilir
(ör. cron + `docker inspect`, Zabbix/Prometheus `cadvisor`). Diskler: `docker system df`, `pgdata` doluluğu ve yedek dizini için ayrıca uyarı kurun (>%80).

### 9.1 Tek örnek varsayımı, izin önbelleği ve bellek içi sayaçlar (güvenlik notu)

Pilot topolojisi **tek `api` örneğidir**. Bu varsayımdan üç davranış doğar; `api`'yi birden çok kopyaya çıkarmadan önce (K13/Kubernetes) ele alın:

| Durum | Davranış | Sınır / Ne yapmalı |
|---|---|---|
| **İzin önbelleği** (HybridCache) | Kullanıcının etkin izinleri (üyelik → rol) `Caching:PermissionExpirationMinutes` süresince önbellekte kalır. Rol güncelleme, üye rol/aktiflik değişimi, davet ekleme/kabul **aynı süreçte önbelleği hemen geçersiz kılar** (yeni izin/geri alınan izin anında etkili). | Redis **yoksa** (varsayılan) önbellek yalnız süreç belleğindedir; varsayılan süre **2 dk** (`Caching:PermissionExpirationMinutes` açıkça verilmedikçe). Birden çok `api` kopyası + Redis ile diğer kopyalarda bayatlık en çok yerel süre (`LocalExpirationSeconds`, 30 sn) + Redis süresi kadardır (10 dk); bu durumda Redis profilini açın ve süreyi işletme riskinize göre kısaltın. Access token 15 dk yaşar; JWT'de izin claim'i **yoktur**, izinler her istekte önbellekten çözülür (yani token süresi izin bayatlığını uzatmaz). Bir kullanıcının **oturumu** silinemez/kapatılamaz: pasifleştirilen üye tokeni ile izinsiz kalır (izinler boş döner) ve refresh yenilenemez; hesap kapatma için yönetici üyeliği pasifleştirir. |
| **Giriş azaltma sayaçları** (`LoginThrottle`: IP+hesap 5 hatalı deneme/15 dk, e-posta başına 20 deneme/dk) | Bellek içidir; `api` yeniden başlayınca sıfırlanır ve kopyalar arasında paylaşılmaz. | Çok kopyada saldırgan sınırı kopya sayısıyla çarpar; o zaman paylaşımlı depo (Redis) gerekir. Kalıcı kalan koruma: hesap kilidi (10 hatalı deneme → 15 dk, veritabanında) ve auth uçlarının IP başına hız sınırı. |
| **Kimliği doğrulanmış hız sınırı** (`RateLimiting:User` 600/dk, `Tenant` 3000/dk) | Kopya başına sayılır. | Cömert varsayılanlar normal kullanımı asla etkilemez; kurumsal NAT/tek vekil arkasında kullanıcı başına sınır yine kullanıcı kimliğine (`sub`) bağlıdır, IP'ye değil. `RateLimiting__User__PermitLimit` vb. ortam değişkeniyle ayarlanır. |
| **Varlık (plan/askı) önbelleği** (M7, HybridCache `ent:{tenantId}`) | Plan, deneme, askı ve modül bayrakları `Platform:Entitlements:CacheSeconds` (30 sn) önbelleklenir; **etkin durum her istekte saatten hesaplanır** (deneme bitişi önbelleğe takılmaz). Platform komutları (plan/askı/silme) aynı süreçte önbelleği **hemen** geçersiz kılar. | Çok kopyada diğer kopyalar ≤ 30 sn (+ L2/Redis süresi) bayat kalır: askı en geç bu sürede tüm kopyalarda etkili olur. Kayıt sayaçları (limit) `Platform:Usage:CacheSeconds` (300 sn) önbellekli ve **yumuşak**tır (sınır bu sürede bir miktar aşılabilir); kullanıcı sınırı sert ve önbelleksizdir (istişari kilit). |

İstek gövdesi üst sınırı `RequestLimits__MaxRequestBodyBytes` (varsayılan 1 MB; aşan istek 413). Oturum: refresh token ailesinin **mutlak** ömrü 30 gündür (`Identity__RefreshFamilyDays`); süre dolunca kullanıcı yeniden giriş yapar. Parola değişince kullanıcının tüm cihazlardaki oturumları kapanır.

## 10. Günlükler

- `docker compose ... logs -f --since 15m api worker web` ; servis başına döner kayıt (json-file, 20 MB × 5). Merkezî toplama için compose'daki `logging` bloğunu
  `syslog`/`gelf` sürücüsüne çevirin (yalnızca kurum içi log sunucusuna).
- API günlüğü Serilog konsol biçimindedir; her isteğin `X-Correlation-Id` yanıt başlığı vardır — kullanıcının bildirdiği hatayı bu kimlikle (ve ProblemDetails `traceId`) günlükte bulun.
  Yavaş istekler (>1 sn) `Warning`, 5xx `Error`; sağlık uçları günlüğe yazılmaz. **İstek gövdeleri (parolalar dâhil) günlüğe yazılmaz.**
- Worker: outbox hataları `Outbox polling failed` (EventId 4000), Conductor görev sonuçları `Workflow task ... reported as COMPLETED|FAILED` (4102), durum senkronu 4110.
- PostgreSQL: 1 sn'den yavaş sorgular günlüğe düşer (`PG_LOG_MIN_DURATION_MS`).

## 11. Sorun giderme

### 11.1 Conductor / workflow

| Belirti | Kontrol | Çözüm |
|---|---|---|
| Yürütme `failed`, hata `workflow.engine_unavailable` | `docker compose ... ps conductor`; `exec conductor curl -fsS localhost:8080/health` | Conductor'ı başlatın/yeniden başlatın (`restart conductor`), sonra arayüzde yürütmeyi **Yeniden dene** |
| Yürütme `running` kalıyor | `exec conductor curl -s localhost:8080/api/tasks/queue/polldata/all` → 5 kuyruk (`crm_assign_lead_owner`, `crm_create_followup_task`, `crm_create_approvals`, `crm_record_decision`, `crm_cancel_pending_approvals`) için `lastPollTime` **son saniyelerde** olmalı | Yoksa Worker poll etmiyor: `ps worker`, `logs worker`; `restart worker` |
| Workflow tanımı yok (`crm_*`) | `exec conductor curl -s localhost:8080/api/metadata/workflow` | API/Worker açılışta idempotent kaydeder; `restart api worker` |
| Hata `no_assignee` / `no_approver` | Kuraldaki rolde aktif üye yok | Role üye ekleyin/kuralı düzeltin |
| Conductor arayüzü gerekli | Yayınlanmaz | Geçici: `docker compose -f deploy/docker-compose.prod.yml -f deploy/docker-compose.conductor-ui.yml up -d conductor` (127.0.0.1:18090; SSH tüneli). İşiniz bitince `up -d conductor` ile kaldırın; **Conductor'da kimlik doğrulama yoktur** |
| Conductor yavaş/çöküyor | `docker stats`, `JAVA_OPTS` | `CONDUCTOR_JAVA_OPTS` ve `CONDUCTOR_MEM_LIMIT` artırın |

### 11.2 Worker

`unhealthy`: sinyal dosyası 60 sn'dir yenilenmiyor → süreç kilitli/çöktü: `logs worker --tail 100`, `restart worker`. Veritabanı henüz hazır değilse tur tur yeniden dener (çökmez).
Lead/fırsat olayları işlenmiyorsa outbox birikir: `SELECT count(*) FROM sales.outbox_messages WHERE processed_at IS NULL;` (Worker ayakta olmalı).

### 11.3 Sık görülen hatalar

| Belirti / mesaj | Neden | Çözüm |
|---|---|---|
| `required variable X is missing a value` (compose) | `.env`'de zorunlu sır boş | `generate-secrets` çalıştırın / değeri doldurun |
| `api` çıkıyor: `Auth:SigningKeyPem is required outside Development/Testing` | JWT anahtar dosyası yok/boş | `secrets/jwt-signing-key.pem` var mı, `JWT_SIGNING_KEY_FILE` doğru mu; dosya konteyner kullanıcısı (uid 1654) için okunur olmalı (dizin 0700, dosya 0644) |
| `ConnectionStrings:Database is not configured` | Bağlantı dizesi boş | `.env` parolaları ve compose ortamını kontrol edin |
| `api` başlamıyor, `migrator` `Exited (1/139)` | Migration/bağlantı hatası | `logs migrator`; `db-init` başarılı mı; sonra `run --rm migrator migrate` |
| `400 Bad Request - Invalid Hostname` | `Host` başlığı `ALLOWED_HOSTS`'ta yok (IP ile erişim dâhil) | Kullanıcıların yazdığı ad ile erişin/`ALLOWED_HOSTS`'a ekleyin |
| `403 auth.signup_disabled` | Beklenen (§5) | Organizasyonu §3.5 ile açın |
| `429 general.rate_limit_exceeded` (hep) | IP'ler tek vekil adresine toplanıyor → `TRUSTED_PROXY_CIDR` yanlış | Vekil adresini/ağını `TRUSTED_PROXY_CIDR`'a girin (`up -d web`) |
| `502 Bad Gateway` (`/api`) | api kapalı/yeniden başlıyor | `ps api`, `logs api`; nginx adresi 10 sn içinde yeniden çözer |
| `permission denied for schema ...` (api/worker günlüğü) | `crm_app` izinleri eksik (ör. elle geri yükleme sonrası) | `up -d db-init` (izinleri yeniden verir) |
| Giriş `auth.no_active_organization` | Kullanıcının aktif üyeliği yok | Organizasyon yöneticisi üyeliği etkinleştirsin |
| Giriş `auth.locked_out` | 10 hatalı deneme (tüm IP'lerden) → 15 dk kilit; yalnız parola DOĞRUYKEN gösterilir | Bekleyin (kilit süresi dolunca açılır); doğru parolayı bilmeyen biri bu mesajı görmez (genel `auth.invalid_credentials`) |
| Giriş `429 general.rate_limit_exceeded` | Aynı IP + hesap 5 hatalı deneme (15 dk) ya da e-posta başına dakikada 20 deneme; vekil arkasında `TRUSTED_PROXY_CIDR` yanlışsa tüm kullanıcılar tek IP görünür | 15 dk bekleyin; `TRUSTED_PROXY_CIDR` doğru mu bakın (bellek içi sayaçlar `up -d api` ile sıfırlanır) |
| `403 auth.password_change_required` | Geçici parolalı hesap | Kullanıcı `POST /me/password` ile parolasını değiştirmeli (web ekranı yönlendirir) |
| `port is already allocated` | `WEB_PORT` dolu | `.env`'de değiştirin |
| Kullanıcılar aniden çıkış yaptı | JWT anahtarı değişti/yeniden üretildi | Beklenen; yeniden giriş |
| Saat/tarih kayması | Konteyner saati ana makineden | Ana makinede NTP; uygulama `Europe/Istanbul` kullanır |

## 12. Veri yerleşimi ve KVKK operasyon notları

- **Veri merkezinden çıkış yok:** PostgreSQL verisi (`pgdata` volume), yedekler ve günlükler sizin sunucularınızdadır. Uygulama katmanı **kendiliğinden dışarı çağrı yapmaz**:
  kod taramasında API/Worker'ın tek HTTP istemcisi `Conductor:BaseUrl`'e (kendi `conductor` servisiniz) gidenidir; e-posta/SMS/analitik/hata izleme (Sentry vb.)/telemetri yoktur,
  web arayüzü yazı tiplerini ve tüm varlıklarını kendi kökeninden sunar (CDN yok, CSP `default-src 'self'`). Ek olarak `backend` ağı `internal` olduğundan API/Worker/Postgres/Conductor dışarıya
  bağlantı **açamaz** (doğrulandı: API ve Postgres konteynerinden dış ad çözümlenemedi). Dış çağrı yalnız sizin eklediğiniz yapılandırmayla olur
  (ileride SMTP vb. eklenirse `backend` ağı ve bu belge güncellenmelidir). Scalar/OpenAPI dokümantasyonu üretimde kapalıdır (Scalar, açıkken tarayıcıda CDN'den betik çeker).
- **Kişisel veriler:** kişi/lead/firma iletişim alanları veritabanındadır; denetim kaydında (`audit.audit_log_entries`) e-posta/telefon alanları `***` ile maskelenir; oturum kayıtları IP ve kullanıcı-aracısı tutar
  (`identity.refresh_tokens`, kişisel veri sayılır; süresi dolmuş kayıtların temizliği sonraki iş).
- **Veri yerleşimi (M7):** tek bölge / tek veri merkezi. Kiracı başına bölge seçimi **yoktur ve kapsam dışıdır**; SaaS'ta tüm kiracılar aynı yurt içi veri merkezinde durur, dış çağrı/alt işleyici yoktur
  (bu bölümdeki "veri merkezinden çıkış yok" güvenceleri aynen geçerlidir).
- **Silme ≠ imha (kayıt düzeyi):** tek kayıt silme yumuşaktır (`is_deleted`); kayıt/kullanıcı bazlı kalıcı imha ve anonimleştirme aracı **yoktur** (kısmi imha kapsam dışı). **Kiracı bütününün** KVKK imhası M7 ile vardır:
  1. **Talep:** platform yöneticisi `POST /api/v1/platform/organizations/{id}/deletion-request` `{ reason, retentionDays? }` (varsayılan **30 gün**, 7–90). Kiracı **anında** `pending_deletion` olur (tüm kullanıcılar dışarıda, giriş yok).
  2. **Bekleme:** talep bekleme süresi içinde `.../deletion-request/cancel` ile iptal edilir (kiracı önceki duruma döner).
  3. **İmha:** süre dolunca Worker (`TenantErasureService`, `Platform:Deletion:PollMinutes` = 10 dk, tek örnek) **kalıcı** imha yapar: kiracının tüm iş verisi (yumuşak silinenler dahil; yedi modül), outbox, denetim kaydı (`audit.audit_log_entries`), Conductor'daki yürütme kayıtları,
     yalnız bu kiracıya ait hesaplar ve oturumları; başka kiracıda üyeliği olan (ortak) hesaplar ve platform yöneticisi hesapları **kalır** (yalnız bu kiracıdaki üyelik/rol gider). Adımlar idempotenttir ve `erased_steps` ile yeniden başlatılır; hata → `failed`, üstel bekleme, `Platform:Deletion:MaxAttempts` (10) sonrası `deletion.failed` denetimi + günlük uyarısı (konsolda kırmızı).
  4. **Mezar taşı:** `platform.tenant_accounts` satırı `deleted` (ad `[deleted]`, slug `deleted-xxxxxxxx`), `platform.deletion_requests` satırı imha raporuyla (kişisel veri yok) ve `platform.platform_audit_entries` (hedef adı redakte) **bilerek kalır** (hesap verebilirlik; `Platform:Audit:RetentionDays` = 1825 gün).
  Sistem (işletim) organizasyonu asla imha edilmez. Önbellek girdileri (kiracı önekli, TTL ≤ 10 dk) imha sonunda geçersiz kılınır.
  **Dosya ekleri (M8C):** imha, `files-objects` adımıyla (sıra 95: `workflows-conductor` 90'dan sonra, `module:files` 100'den önce) kiracının **tüm nesnelerini** `{tenantId}/` önekiyle siler ve **doğrular** (önekte 0 nesne değilse adım `failed` olur ve yeniden denenir); satırlar ve `file_access_log` modül adımında gider; rapora `files.objects` sayısı girer (kişisel veri yok). **Dosya adları kişisel veri olabilir** (veritabanında ve denetimde; imhada gider); dosya içeriği günlüğe hiç yazılmaz.
  **Yedekler:** imha yedekleri temizlemez; silinmiş veri yedeklerde (**nesne kovası arşivleri dâhil**) saklama süresi (`RETENTION_DAYS`) boyunca kalır — saklama ve imha politikasını buna göre yazın. **Geri yükleme sonrası** imha edilmiş kiracının yeniden görünmemesi için
  `migrator erase-deleted-tenants` çalıştırılır (tüm mezar taşları için imhayı yeniden koşar; idempotent; **geri yüklenen nesneleri de yeniden siler**). Dosya **indirme izi** (kim neyi ne zaman): `select occurred_at, user_id, file_id, action from files.file_access_log where tenant_id = '<kiracı>' order by occurred_at desc limit 100;` (IP/ad yok; `Files:AccessLog:RetentionDays` = 365 gün sonra Worker siler).
- **Erişim:** kiracı ayrımı satır bazlıdır (`TenantId` + global filtre; testli); platform yöneticisi organizasyon açar, plan/deneme/askı/silme yönetir ve sayaç (kullanım) görür ama kiracı iş verisini **okuyamaz** (taklit yok; M7). Yönetici hesapları ve yedek erişimini kısıtlayın; `.env`/`secrets/` dosya izinleri sıkı tutulmalıdır.
- **İletim:** dış trafik TLS ile (§3.7); iç ağ (backend) yalnız konteynerler arasıdır.
- **VERBİS / aydınlatma metni / veri işleyen sözleşmeleri** teknik değil, kurumsal iştir (kontrol listesi: [m5-pilot-yayin.md](../plan/m5-pilot-yayin.md)).

## 13. Kurtarma hedefleri (öneri — onay gerekir)

| Hedef | Öneri (pilot) | Nasıl sağlanır |
|---|---|---|
| **RPO** (kabul edilen veri kaybı) | **≤ 4 saat** (iş saatlerinde); gece ≤ 24 saat | 4 saatte bir + gece mantıksal yedek. RPO'yu dakikalara indirmek için sonraki adım: WAL arşivleme/PITR (pgBackRest/WAL-G) |
| **RTO** (geri dönüş süresi) | **≤ 4 saat** | Doğrulanmış yeniden kurulum: sırlar + imajlar + yedek → §7. Küçük veritabanında geri yükleme saniyeler sürer; **süreyi gerçek veri boyutuyla provada ölçüp bu değeri güncelleyin** |
| Yedek saklama | 14 gün günlük + 3 aylık (ayrı arşiv) | `RETENTION_DAYS`, aylık kopya |
| Geri yükleme provası | Aylık, ayrı örneğe | [restore.md](../../deploy/restore.md) Senaryo A + `smoke.sh` + **"imha edilmiş kiracı geri gelmedi" denetimi (M7):** `migrator erase-deleted-tenants` sonrası `select count(*) from identity.tenants t join platform.tenant_accounts a on a.tenant_id = t.id where a.status = 'deleted'` = 0 |

Tek sunuculu kurulumda sunucu arızası = RTO süresi kadar kesinti; yüksek erişilebilirlik (ikinci düğüm, Postgres replikasyonu) sonraki aşamadadır (K13: Kubernetes).

## 14. Ek: ortam değişkenleri özeti

Ayrıntı ve varsayılanlar: [`deploy/.env.example`](../../deploy/.env.example). Uygulama ayarları (compose `environment` bölümünden): `Registration__Mode`, `AllowedHosts`,
`ForwardedHeaders__Enabled|KnownProxies__n|KnownNetworks__n` (varsayılan kapalı; compose yalnız `backend` alt ağını güvenilir sayar), `Docs__Enabled`, `Conductor__BaseUrl`,
`ConnectionStrings__Database|Redis`, `Auth__SigningKeyPem` (dosyadan; `/run/secrets/<Ad>` dosyaları `Ad`'daki `__` → `:` ile yapılandırma anahtarı olur), `Worker__HeartbeatFile`.
**Platform (M7):** `Platform__Signup__PlanCode` (`starter`), `Platform__Provisioning__DefaultPlanCode` (`internal`), `Platform__Plans__<n>__…` (plan kataloğu; yalnız **Migrator** için anlamlıdır — `migrate`/`sync-plans`; örnek: `appsettings.json`), `Platform__Entitlements__CacheSeconds` (30),
`Platform__Usage__{CacheSeconds 300, SnapshotPollMinutes 30, RetentionDays 400}`, `Platform__Audit__RetentionDays` (1825), `Platform__Deletion__{RetentionDays 30, MinRetentionDays 7, MaxRetentionDays 90, PollMinutes 10, MaxAttempts 10, ChunkSize 10000}`.
Bilinmeyen plan kodu ve tutarsız aralıklar açılışta reddedilir (Migrator ≠ 0 çıkış; API/Worker başlamaz). Compose `environment` ve `.env.example` satırlarını DevOps ekler.
**Dosya ekleri (M8C):** `Files__Storage__{Provider s3|filesystem|memory, Endpoint, Region, Bucket, AccessKey|SecretKey (dosyadan), ForcePathStyle, Encryption required|none, AcknowledgeUnencrypted, RootPath}`, `Files__Upload__{MaxFileMb 25, MaxFilesPerRequest 10, MaxRequestMb 110, TempDirectory, AllowedExtensions}`, `Files__Scanner__{Provider none, FailMode open|closed, OnInfected reject|quarantine}`, `Files__Purge__{SoftDeleteRetentionDays 7, PollMinutes 60, RecordMissingGraceDays 30}`, `Files__Reconcile__{PollHours 24, OrphanGraceHours 24, MaxOrphanDeleteFraction 0.05, MaxOrphanDeletePerRun 1000}`, `Files__AccessLog__RetentionDays` (365), `Files__RateLimiting__{Upload 30, Download 120, ConcurrentUploadsPerTenant 4, MaxConcurrentUploads 16}`; plan limiti `Platform__Plans__<n>__limits__maxStorageMb`. Üretimde `Provider != s3` ya da `Encryption=none` (`AcknowledgeUnencrypted=true` beyanı olmadan) API/Worker/Migrator'ı **başlatmaz**; tutarsız aralıklar (`MaxFileMb` 1–200, `MaxFileMb ≤ MaxRequestMb ≤ MaxFilesPerRequest × MaxFileMb`) açılışta reddedilir. Compose değişkenleri: `MINIO_IMAGE`, `MINIO_MC_IMAGE`, `FILES_BUCKET`, `MINIO_MEM_LIMIT`, `API_TMP_SIZE`.

## 15. Nesne deposu (dosya ekleri, M8C, karar K19)

Bağlayıcı plan: [m8c-dosya-ekleri.md](../plan/m8c-dosya-ekleri.md). Kullanıcılar kayıtlara (firma, kişi, potansiyel müşteri, fırsat, aktivite, talep, teklif, sipariş, kampanya) dosya ekler; baytlar **API üzerinden akışla** tek bir kovaya (`crm-files`) `{tenantId}/{yyyy}/{fileId}` anahtarıyla yazılır; tarayıcıya presigned URL **verilmez**.

### 15.1 Sunucu seçimi, imaj doğrulaması ve lisans notu (K19)

- **Uygulama S3-genel:** adaptör `AWSSDK.S3`tir (MinIO SDK'sı değil); sunucu değişebilir (MinIO / SeaweedFS S3 / Garage / Ceph RGW) — yalnız `Files__Storage__*` + `minio-init` betiği değişir, **uygulama kodu değişmez**.
- **Doğrulama (2026-09-20):** `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z` ve `quay.io/minio/mc:RELEASE.2025-08-13T08-35-41Z` **çekilebilir** (manifest doğrulandı; `latest` da bu sürüme çözülür — topluluk sürümünün son yayınıdır). Etiketler **sabitlenir**, `latest` kullanılmaz; kurum içi registry aynası önerilir (kurumsal ağ dışına çekim gerektirmez, kaynak kaybolursa etkilenmezsiniz).
- **Lisans:** MinIO AGPLv3'tür. Bu kurulumda MinIO **değiştirilmeden, iç bir hizmet olarak** çalışır (uygulama ağ üzerinden S3 protokolüyle konuşur, MinIO koduna bağlanmaz/dağıtmaz) — bu kullanım AGPL'nin kaynak paylaşım yükümlülüğünü doğurmaz; ancak **hukuk onayı** alınmalıdır (MinIO'yu değiştirip kullanıcılara ağ üzerinden sunarsanız değişiklikleri yayınlamanız gerekir). Ticari lisans (AIStor) ayrı bir karardır.
- **Bakım uyarısı:** MinIO topluluk sürümü **bakım modundadır** (yeni güvenlik yamaları/özellikleri garanti değildir; yönetim konsolu özellikleri de topluluk sürümünde kısıtlıdır). Bu yüzden: depo yalnız iç ağdadır (`internal` ağ, port yok), servis hesabı yalnız tek kovaya yetkilidir, şifreleme `required`. **Karar noktası:** güvenlik yamasına ihtiyaç doğarsa ya kurumsal bir ayna/derleme kaynağı belirlenir ya da **SeaweedFS S3 veya Garage**'a geçilir (compose'daki `minio`/`minio-init` servisleri ve `init.sh` politikası değişir; uygulama ve testler aynı kalır; nesneler `mc mirror`/`rclone` ile taşınır).

### 15.2 Kurulum, yapılandırma ve şifreleme

- `generate-secrets.*` beş sırrı üretir (§4). `docker compose ... up -d`: `minio` (sağlıklı) → `minio-init` (kova + `mc encrypt set sse-s3` + `crm-files-app` politikası/hesabı) → `migrator`/`api`/`worker`. **Kova/şifreleme/hesap yapılamazsa `minio-init` başarısız olur ve yığın başlamaz** (sessizce şifresiz kalınmaz). API her yüklemeden önce (30 sn önbellekli) `GetBucketEncryption` ile şifrelemeyi doğrular; yoksa yüklemeler `503 file.storage_unavailable` olur.
- **MinIO konsolu kapalıdır** (`MINIO_BROWSER=off`); yönetim geçici bir mc konteyneriyle yapılır: `docker compose -f deploy/docker-compose.prod.yml run --rm --no-deps --entrypoint /bin/sh minio-init` (kök kimlik bilgileri secret dosyalarındadır; `mc alias set local http://minio:9000 ...`). Erişim yalnız `docker compose` sahibinedir.
- **API geçici dosyaları:** yükleme önce geçici dosyaya yazılır (`Files__Upload__TempDirectory=/tmp`, **tmpfs**, `API_TMP_SIZE` = eşzamanlı yükleme × `MaxFileMb`; kiracı başına en çok 4, genel 16 eşzamanlı yükleme).
- **Web ters vekili (nginx) — DEVOPS/WEB İŞİ (bu karta `web/` dâhil değildi):** `web/nginx/default.conf.template` içinde (1) `location = /api/v1/files` (yükleme): `client_max_body_size` = `Files:Upload:MaxRequestMb` + 5 (varsayılan **115m**), `proxy_request_buffering off`, `proxy_read_timeout 300s`, `proxy_send_timeout 300s`; (2) `location ~ ^/api/v1/files/[0-9a-fA-F-]{36}/content$` (indirme): `proxy_buffering off`, 300 sn zaman aşımı; ikisinde de `/api/` bloğundaki `add_header` deseni; genel `client_max_body_size 20m` **değişmez**; (3) CSP'ye yalnız `img-src 'self' data: blob:` ve `frame-src 'self' blob:` eklenir (önizleme; `object-src 'none'`, `script-src 'self'` aynen). Bunlar eklenmeden 20 MB üstü yüklemeler vekilde 413 alır ve önizleme CSP'ye takılır.

### 15.3 Kota (`maxStorageMb`) ve planlar

`platform.plans.limits.maxStorageMb` (`null` = sınırsız, `0` = hiç yükleme yok, ≥ 0, üst sınır 1 048 576 MiB) ve kiracı istisnası `overrides.maxStorageMb` (anahtar yok = plan, `null` = açıkça sınırsız). Örnek plan sayıları (`Platform:Plans`, **ticari karar değildir**): `internal` sınırsız, `starter` 1024, `business` 25600, `enterprise` 512000. Zorlama **sert ve kesindir** (kiracı başına istişari kilit; eşzamanlı yüklemelerle aşılamaz); aşımda `402 file.quota_exceeded`. Silme kotayı anında düşürür; plan düşürme dosyayı silmez (indirme/silme açık, yeni yükleme 402). Kullanım `files.storage_bytes`/`files.files` metrikleriyle günlük anlık görüntüye ve **finans CSV'sine** (`GET /platform/usage/export`) girer. Kiracı yöneticisi: `GET /api/v1/files/usage`, `GET /api/v1/subscription`.

### 15.4 Yaşam döngüsü işleri (Worker) ve komutlar

| İş | Ne yapar | Ayar |
|---|---|---|
| Temizlik (`FilesPurgeService`, 60 dk) | Yumuşak silinen dosyaların nesnesini `SoftDeleteRetentionDays` (7) sonra fiziksel siler; erişim günlüğü saklama temizliği (365 gün) | `Files__Purge__*`, `Files__AccessLog__RetentionDays` |
| Uzlaştırma (`FilesReconciliationService`, 24 saat) | Nesne ↔ satır: nesnesiz satır `missing`, boyut uyuşmazlığı `missing`, geri gelen nesne `ready`; grace (24 sa) sonrası **yetim nesne siler (güvenlik supabıyla)**; kayıt-yok süpürmesi (kaydı silinen dosya `RecordMissingGraceDays` = 30 gün sonra yumuşak silinir) | `Files__Reconcile__*` |
| `migrator files-reconcile [--dry-run] [--tenant <id>]` | Aynı kod elle; çıktıda yalnız sayılar (kiracı başına `objects rows missing restored sizeMismatch orphans orphansDeleted guardTripped foreign`) | Geri yükleme sonrası önce `--dry-run` |

Güvenlik kuralları: **bilinmeyen kiracı önekleri ve anahtarı ayrıştırılamayan nesneler asla silinmez**; yetim sayısı `MaxOrphanDeletePerRun` (1000) ya da nesnelerin `MaxOrphanDeleteFraction` (%5) oranını aşarsa **hiçbir şey silinmez** (`guardTripped=True`, `Error` günlüğü, `crm.files.reconcile.guard_tripped` sayacı) — yanlış geri yükleme sonrası toplu silmeyi önler. İşler `pg_try_advisory_lock` ile tek örnektir; depo arızası turu erteler (Worker düşmez).

### 15.5 Sorun giderme

| Belirti | Neden | Çözüm |
|---|---|---|
| Yüklemeler `503 file.storage_unavailable` | `minio` kapalı/sağlıksız, kimlik bilgisi yanlış ya da kovada varsayılan şifreleme yok | `docker compose ... ps minio`, `logs minio`; `/health` `storage` denetimi (`Degraded` açıklaması); `up -d minio-init` (kova/şifreleme/hesabı yeniden kurar) |
| API/Worker açılışta çıkıyor: `Files:Storage:Provider must be 's3' in Production` / `Encryption 'none' ...` | Yanlış yapılandırma (üretimde `filesystem`/`memory` ya da şifresiz) | `Files__Storage__Provider=s3`, `Encryption` boş/`required`; şifresiz disk bilinçli kabulse `Files__Storage__AcknowledgeUnencrypted=true` |
| Dosya indirme `410 file.content_missing`; kullanıcıya "dosya kullanılamıyor" | Uzlaştırma nesnesi olmayan satırı `missing` işaretledi (geri yükleme sırası, elle silinmiş nesne, disk sorunu) | `files-reconcile --dry-run`; nesne yedekten geri konursa bir sonraki uzlaştırmada `ready` olur; kalıcı kayıpsa kullanıcı dosyayı silip yeniden yükler. `missing` sayacı > 0 → uyarı |
| `files-reconcile` `guardTripped=True` | Çok sayıda yetim (yanlış/eski nesne yedeği geri yüklendi?) | Nedeni inceleyin; **elle silmeden önce** veritabanı ve nesne yedeğinin aynı zaman noktasından olduğunu doğrulayın; gerçekten yetimse `Files__Reconcile__MaxOrphanDeleteFraction` ile geçici gevşetip yeniden koşun |
| Yüklemede `413 file.too_large` (vekilden) | nginx `client_max_body_size` (§15.2) | Vekil ayarını `MaxRequestMb + 5` yapın |
| Yüklemeler `429 general.rate_limit_exceeded` | Kiracı başına 4 / genel 16 eşzamanlı yükleme ya da kullanıcı başına 30 parça/dk | Beklenen; `Files__RateLimiting__*` ile ayarlanır |
| Disk dolu (`api` `/tmp`) | tmpfs küçük (yükleme geçici dosyaları) | `API_TMP_SIZE` artırın |
