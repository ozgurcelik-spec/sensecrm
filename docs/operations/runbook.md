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
| **Webhook sırrı şifreleme anahtarı (AES-256, base64/32 bayt; M8B)** | `secrets/integrations-encryption-key` → `/run/secrets/Integrations__Encryption__Keys__k1` (api, worker, migrator) | **Yoksa api/worker/migrator başlamaz.** Döndürme: yeni anahtarı `Integrations__Encryption__Keys__k2` olarak ekleyin + `Integrations__Encryption__CurrentKeyId=k2` → `migrator reencrypt-integration-secrets` (idempotent; eski anahtarla çözüp yenisiyle yazar) → eski anahtarı kaldırın. **Yedeği veritabanı yedeğinden AYRI saklayın**; kayıp = saklı sırlar okunamaz (her abonelikte sırrı döndürün, alıcılar yeni sırrı alır). `generate-secrets` bunu üretir ve **asla `FORCE=1` ile yeniden üretmez** (`FORCE_INTEGRATIONS_KEY=1` gerekir) |

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
Ayrı yedeklenecekler: `deploy/.env`, `deploy/secrets/` (şifreli). Aylık **geri yükleme provası** yapın (§13).

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
| Tümü | `docker compose -f deploy/docker-compose.prod.yml ps -a` | api/worker/web/postgres/conductor `healthy`; db-init, migrator `Exited (0)` |

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
| **API anahtarı önbelleği ve azaltma** (M8B) | Anahtar özeti + meta `Integrations:ApiKeys:CacheSeconds` (30 sn) önbelleklenir (`apikey:{kiracı}:{önek}`); iptal/güncelleme aynı süreçte **anında** geçersiz kılar. Kimlik doğrulama **başarısızlık azaltması** (IP+kiracı 20 hata/10 dk → 429, DB'siz) bellek içidir, kopya başına. | Çok kopyada iptal ≤ 30 sn (+ L2) gecikebilir; azaltma paylaşılmaz (kopya sayısıyla çarpılır). Sızan anahtarı **hemen iptal edin**. Anahtar hız sınırı `RateLimiting:ApiKey` (120/dk) ve `ApiKeyTenant` (600/dk) kopya başına sayılır. |
| **Webhook kota kovaları** (M8B) | Kiracı başına dakikada teslimat (600), eşzamanlılık (kiracı 4 / genel 32 / ana bilgisayar 2), test ping'i (5/dk) ve yeniden gönderme (20/dk) sayaçları bellek içidir. | Tek Worker varsayımı; Worker çoğaltılırsa kotalar kopya başına uygulanır. |

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
- **Giden webhook (M8B) — "veri merkezinden çıkış yok" güvencesi egress açıkken yeniden yazılır:** varsayılan `WEBHOOKS_ENABLED=false` (hiçbir dış çağrı yok). Açıldığında dış trafik **yalnız** `egress-dns` + `egress-proxy` (bkz. §17) üzerinden, yalnız kiracı yöneticisinin tanımladığı HTTPS hedeflerine, PII içermeyen zarfla (kimlik/numara/durum/tutar) gider; `backend` ağı `internal` kalır, Worker'a internet ağı verilmez. Dış çağrı kaydı `egress-proxy` erişim günlüğüdür (hedef IP/zaman; içerik yok). **Hedefi seçen yönetici (kiracı = veri sorumlusu) alıcının ülkesi ve yurt dışı aktarım (KVKK m.9) yükümlülüklerinden sorumludur**; işleten taraf `WEBHOOKS_ALLOWED_HOSTS` ile hedef kümesini daraltabilir veya `WEBHOOKS_ENABLED=false` ile çıkışı tamamen kapatabilir. Çözümleyici üst akışı (`EGRESS_DNS_UPSTREAM`) hedef **ana bilgisayar adlarını** görür → kurum içi çözümleyici önerilir. Teslimat günlüğü (PII'siz zarf) 30 gün tutulur, kiracı imhasında silinir; yedeklerde `RETENTION_DAYS` boyunca kalır. Sırlar şifreli saklanır; anahtar yedekten ayrı korunur.
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
  **Yedekler:** imha yedekleri temizlemez; silinmiş veri yedeklerde saklama süresi (`RETENTION_DAYS`) boyunca kalır — saklama ve imha politikasını buna göre yazın. **Geri yükleme sonrası** imha edilmiş kiracının yeniden görünmemesi için
  `migrator erase-deleted-tenants` çalıştırılır (tüm mezar taşları için imhayı yeniden koşar; idempotent).
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

## 15. Ek: uçtan uca tarayıcı testleri (Playwright)

Yayın öncesi doğrulama için [`e2e/`](../../e2e) paketi, üretim compose'unu **ayrı bir compose projesinde** (`crm-e2e`, `127.0.0.1:8181`, tek kullanımlık sırlar) kaldırıp gerçek tarayıcıyla kritik akışları, erişilebilirliği (axe) ve nginx güvenlik başlıklarını sınar; işi bitince yalnız o projeyi siler (`crm-prod-*` ve diğer yığınlara dokunmaz).
`.\e2e\run.ps1` (Windows) veya `./e2e/run.sh` (Linux/macOS) — ayrıntı, seçenekler ve sorun giderme: [`e2e/README.md`](../../e2e/README.md); ürün bulguları: README'nin "Bulgular ve bilinen sorunlar" bölümü. CI'da `e2e` işi (`.github/workflows/ci.yml`) aynı betiği çalıştırır ve raporu artifact olarak yükler.

## 16. Gözlemlenebilirlik: izleme ve metrikler (C-OPS1, K20)

Varsayılan `up` **hiçbir izleme bileşeni çalıştırmaz** ve hiçbir yeni port açmaz. İzleme yığını isteğe bağlı bir compose **overlay**'idir: [`deploy/docker-compose.observability.yml`](../../deploy/docker-compose.observability.yml).
Kapsam: yalnız **metrikler** (Prometheus + Grafana); iz (trace) ve merkezî günlük yığını (Seq/Loki) yoktur (K20). Günlükler §10'daki gibi konsoldan/`docker logs`'tan okunur.

### 15.1 Bileşenler ve ağ

| Bileşen | İmaj (sabit) | Ağ | Yayın |
|---|---|---|---|
| `api` metrik dinleyicisi | uygulama içinde, **ayrı port 9464** | `backend` (internal) | **Yok** (nginx bu portu bilmez) |
| `worker` metrik dinleyicisi | uygulama içinde, **port 9465** | `backend` (internal) | **Yok** |
| `prometheus` | `prom/prometheus:v3.5.1` | `backend` + `observability` (ikisi de internal) | **Yok** (yalnız `docker compose exec`) |
| `postgres-exporter` (+ tek seferlik `monitor-init`) | `quay.io/prometheuscommunity/postgres-exporter:v0.17.1` | `backend` + `observability` | **Yok** |
| `grafana` | `grafana/grafana:12.2.1` | `observability` + `observability-ui` | **`127.0.0.1:${GRAFANA_PORT:-3000}` yalnız loopback** (bind geçersiz kılma yoktur) |
| `node-exporter`, `cadvisor` (profil `host-metrics`, isteğe bağlı) | `prom/node-exporter:v1.9.1`, `gcr.io/cadvisor/cadvisor:v0.52.1` | `observability` | Yok |

- **Çıkış yok (egress = none):** Prometheus, exporter'lar ve uygulama yalnız `internal: true` ağlardadır; dışarıya çıkış yolu yoktur. **Tek istisna Grafana:** Docker, internal bir ağdan port yayınlayamadığı için Grafana ayrıca normal bir köprü ağa (`observability-ui`) bağlıdır. Grafana dışarı çağrı yapmayacak şekilde ayarlıdır (güncelleme/eklenti/haber/telemetri denetimleri kapalı, kayıt ve anonim erişim kapalı, eklenti yönetimi kapalı), ancak ağ katmanında çıkış **yetkisi teknik olarak vardır**. Katı veri yerleşimi (KVKK) gerekiyorsa ana bilgisayar güvenlik duvarında `observability-ui` alt ağından (`docker network inspect <proje>_observability-ui`) çıkışı engelleyin (Linux: `DOCKER-USER` zincirinde `-s <alt ağ> -j DROP`; yayınlanan `127.0.0.1` portu bundan etkilenmez).
- **İç ağ notu:** Prometheus `backend` ağındadır (api/worker'ı kazımak için); dolayısıyla `backend` ağındaki her konteyner (web/nginx dahil) `prometheus:9090`'a erişebilir. nginx yalnız `/api/` yolunu `api:8080`'e vekiller, Prometheus'a yol vermez; `backend`'e yeni bir servis eklerken bunu hesaba katın (Prometheus arayüzü kimlik doğrulamasızdır ve salt-okur metrik/kural verisi gösterir, yönetim uçları — `--web.enable-lifecycle`/admin API — kapalıdır).
- **Uygulama tarafı:** `Observability:Metrics:Enabled` **varsayılan kapalıdır**; yalnız overlay `api`/`worker`'a `Observability__Metrics__Enabled=true`, `BindAddress=0.0.0.0` ve belirteç dosyasını verir. Dinleyici ana uygulama hattından **bağımsız** küçük bir Kestrel'dir: yalnız `GET|HEAD /metrics` sunar, başka her yol 404, `AllowedHosts`/hız sınırı/kimlik doğrulama ana uygulamaya aittir ve burayı etkilemez. Dinleyici açılamazsa uygulama çalışmaya devam eder (günlükte `Metrics endpoint could not start`).
- **Koruma katmanları:** (1) ağ yalıtımı (`backend` internal, port yayınlanmaz), (2) bearer belirteç (`secrets/metrics-bearer-token`; uygulama ve Prometheus aynı Docker secret'ını okur; sabit zamanlı karşılaştırma; dosya boşsa uygulama belirteç sormaz), (3) yalnız `/metrics` yolu.

### 15.2 Etkinleştirme

```bash
# 1) Yeni sırları üret (mevcut dosyalar korunur; .env'e dokunulmaz): metrics-bearer-token, grafana-admin-password, pg-monitor-password
./deploy/generate-secrets.sh            # Windows: .\deploy\generate-secrets.ps1
# 2) (İsteğe bağlı) deploy/.env: GRAFANA_PORT, PROM_RETENTION_TIME, PROM_RETENTION_SIZE ... (bkz. .env.example'ın sonu)
# 3) Overlay ile başlat (api/worker yeni ortam değişkenleriyle yeniden oluşturulur)
docker compose -f deploy/docker-compose.prod.yml -f deploy/docker-compose.observability.yml up -d
# ana bilgisayar metrikleri (Linux; node-exporter + cAdvisor):
docker compose -f deploy/docker-compose.prod.yml -f deploy/docker-compose.observability.yml --profile host-metrics up -d
```

Doğrulama:

```bash
# Hedefler UP mı? (Prometheus yayınlanmadığı için konteynerin içinden)
docker compose -f deploy/docker-compose.prod.yml -f deploy/docker-compose.observability.yml exec prometheus \
  wget -qO- 'http://localhost:9090/api/v1/query?query=up'
# /metrics yalnız iç ağda: web portundan ulaşılamaz (SPA sayfası döner, metrik dönmez)
curl -s http://127.0.0.1:${WEB_PORT:-8080}/metrics | head -c 200 ; curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:${WEB_PORT:-8080}/api/metrics   # 404
# Grafana: http://127.0.0.1:3000 (kullanıcı GRAFANA_ADMIN_USER=admin; parola: deploy/secrets/grafana-admin-password)
```

Uzak sunucuda Grafana'ya SSH tüneli ile bağlanın (`ssh -L 3000:127.0.0.1:3000 sunucu`) ya da aynı makinedeki TLS vekilinizle `127.0.0.1:3000`'i yayınlayın (Grafana kendi oturum açma ekranını kullanır; anonim erişim kapalıdır).
Kapatmak için overlay'siz `up -d` yeterlidir (api/worker metrik ortam değişkenleri olmadan yeniden oluşur; Prometheus/Grafana verileri `promdata`/`grafanadata` birimlerinde kalır, `--remove-orphans` ile konteynerler silinir).

### 15.3 Ne açığa ÇIKARILMAMALI

- `9464`/`9465` (uygulama metrikleri), `9090` (Prometheus), `9187` (postgres-exporter), `9100`/`8080` (node-exporter/cAdvisor) **asla** `ports:` ile yayınlanmaz, nginx'te `location /metrics` (veya `9464`'e `proxy_pass`) eklenmez, TLS vekiline yönlendirilmez. Mimari test (`ObservabilityArchitectureTests`) üretim compose'unda tek yayınlanan portun `web` olduğunu, overlay'de tek yayınlanan portun `127.0.0.1:…:3000` (Grafana) olduğunu ve nginx yapılandırmasında `metrics`/`9464` geçmediğini denetler.
- Grafana'yı `0.0.0.0`'a bağlamayın; internete/kurumsal ağa açmanız gerekirse önüne kimlik doğrulamalı TLS vekili koyun. Yönetici parolası `secrets/grafana-admin-password` dosyasındadır (varsayılan parola **yoktur**); `secrets/` dizinini `.env` gibi yedekleyin ve sürüm denetimine koymayın.
- Metrikler kiracı veya kullanıcı kimliği, e-posta, IP taşımaz (etiketler yalnız `module|status|reason|outcome|task_type|plan|step|background_job` ve HTTP için yol **şablonu**). Buna rağmen metrik çıktısını dış bir SaaS'a göndermeyin: kiracı sayıları, plan adları ve hata oranları ticari bilgidir.
- Prometheus/Grafana birimleri (`promdata`, `grafanadata`) iş verisi içermez ama sistem topolojisini gösterir; yedek/erişim politikanızda buna göre ele alın.

### 15.4 Panolar (Grafana klasörü "CRM"; dosyadan provizyonlanır, arayüzden düzenlenemez)

Kaynak: [`infra/observability/grafana/dashboards`](../../infra/observability/grafana/dashboards) (değişiklik = dosyayı düzenleyip commit; Grafana 30 sn içinde okur).

| Pano | İçerik |
|---|---|
| `CRM - API overview` | RED (istek hızı, hata oranı, süre p50/p95/p99, **yol şablonu** başına), doygunluk (CPU, bellek, GC, thread pool, Kestrel bağlantıları, hız sınırlayıcı), bağımlılıklar (Conductor HttpClient, Npgsql, EF Core) |
| `CRM - Background processing` | Outbox bekleyen/ölü/en eski yaş ve gönderim gecikmesi (modül başına), workflow yürütmeleri, Conductor yoklama/görev sonuçları ve süreleri, worker süreci |
| `CRM - Security signals` | Giriş sonuçları, kilitlenmeler, refresh yeniden kullanım tespiti, hız sınırlama, 401/403/429, plan/askı reddi |
| `CRM - Tenant lifecycle and deletion` | KVKK silme hattı (durum, gecikme, adım sonuçları, silinen satır), kullanım anlık görüntüsü, plan zorlaması (neden/plan/modül) |
| `CRM - PostgreSQL basics` | `pg_up`, bağlantılar, işlem hızı, önbellek isabeti, kilitler, deadlock, boyut, WAL, uygulama havuzları |

### 15.5 Metrik sözlüğü (uygulama)

Tümü tek `Sense.Crm` Meter'ından gelir (kaynak: `Sense.Crm.Shared.Contracts.Observability.CrmMetrics`); Prometheus adları noktaların `_` olduğu biçimdir. Framework metrikleri (`http_server_request_duration_seconds`, `dotnet_*`, `kestrel_*`, `aspnetcore_rate_limiting_*`, `http_client_*`, `microsoft_entityframeworkcore_*`, `db_client_*`) yerleşik Meter'lardır.

| Metrik | Etiketler | Anlamı |
|---|---|---|
| `crm_auth_logins_total` | `outcome` = success / invalid_credentials / rate_limited / locked_out / inactive / no_organization / suspended | giriş sonuçları |
| `crm_auth_lockouts_total`, `crm_auth_refresh_reuse_detected_total` | – | hesap kilitleme; iptal edilmiş refresh token'ın süre aşımı dışında tekrar kullanımı (aile kapatılır) |
| `crm_auth_refresh_rejected_total` | `reason` = unknown / expired / reuse / concurrent / user_inactive | reddedilen yenilemeler |
| `crm_entitlement_rejections_total` | `reason` (etkin durum: trial_expired, suspended, pending_deletion, deleted \| module_disabled \| limit_exceeded), `module`, `plan` | plan/yaşam döngüsü zorlaması reddi |
| `crm_event_handlers_skipped_total` | `handler` (kod sınıf adı) | plan/askı nedeniyle atlanan olay işleyicileri |
| `crm_outbox_messages_total`, `crm_outbox_dispatch_lag_seconds`, `crm_outbox_poll_failures_total` | `module`, `outcome` = dispatched / retry / dead | outbox işleme |
| `crm_outbox_pending`, `crm_outbox_dead`, `crm_outbox_oldest_pending_age_seconds` | `module` | **Worker** örnekleyicisi (15 sn; `processed_at IS NULL` kısmi indeksi) |
| `crm_conductor_polls_total`, `crm_conductor_tasks_total`, `crm_conductor_task_duration_seconds` | `outcome` (tasks/empty/error; completed/failed/failed_terminal/blocked/report_failed), `task_type` | Conductor yoklama ve görevler |
| `crm_workflow_executions_total`, `crm_workflow_executions_running` | `status` (completed/failed/terminated) | workflow yürütmeleri |
| `crm_tenant_deletion_runs_total`, `_steps_total`, `_rows_deleted_total`, `crm_tenant_deletion_requests`, `crm_tenant_deletion_oldest_due_age_seconds` | `outcome`, `step`, `status` | KVKK imha hattı |
| `crm_usage_snapshot_tenants_total`, `crm_background_failures_total` | `outcome`; `background_job` | günlük kullanım işi; arka plan turu hataları |

Nadir olay sayaçları (refresh yeniden kullanımı, kilitlenme, ölü mesaj, başarısız görev …) süreç açılırken **0 ile önceden oluşturulur**; böylece Prometheus'ta ilk olay bile `increase()`/`rate()` ile görülür (gerçek yığında doğrulandı: ilk `crm_auth_refresh_reuse_detected_total` artışı `CrmRefreshTokenReuse` alarmını tetikledi).

Yeni bir metrik eklerken: etiket olarak **asla** kiracı/kullanıcı/kayıt kimliği, e-posta, IP koymayın; yalnız `CrmMetrics.Tag` sabitlerini kullanın (test kapısı çıktıdaki etiket adlarını denetler).

### 15.6 Alarmlar

Kurallar: [`infra/observability/prometheus/rules/crm-alerts.yml`](../../infra/observability/prometheus/rules/crm-alerts.yml) (`promtool check rules` ile doğrulanır). Tetiklenenleri şu ikisi gösterir: Prometheus'un `/api/v1/alerts` ucu (`docker compose … exec prometheus wget -qO- http://localhost:9090/api/v1/alerts`; Prometheus yayınlanmadığı için konteynerin içinden) ve panolardaki "Firing alerts" kutusu (`ALERTS` serisi).
**Varsayılan olarak bildirim kanalı yoktur** (Alertmanager/e-posta yok: yığının çıkışı yoktur ve SMTP rölesi kurum politikasına bağlıdır). Bildirim istenirse: kurum içi SMTP rölesine erişen bir Alertmanager konteynerini yalnız `observability` ağına ekleyin ve `prometheus.yml`'e `alerting:` bloğu ekleyin; ya da Prometheus'u kurum içi mevcut izleme aracınıza federasyonla bağlayın. Eşikler pilot içindir (≈ 50 eşzamanlı kullanıcı, tek api + tek worker); gerçek trafikle ayarlayın.

Her alarmın `runbook_url` etiketi aşağıdaki başlığa (`#### <AlarmAdı>`) gider; başlık ve bağlantı bir mimari testle senkron tutulur.

### 15.7 Alarm müdahale rehberi

#### CrmTargetDown
`up == 0`: ilgili süreç kapalı/yeniden başlıyor ya da metrik dinleyicisi/belirteç yanlış. `docker compose … ps`, `logs --since 15m <servis>`; `Metrics endpoint listening on …` satırını arayın. Belirteç uyuşmazlığı: Prometheus `HTTP 401` görür (`secrets/metrics-bearer-token` her iki tarafta aynı dosya olmalı). `postgres` hedefi düşükse `monitor-init` çıkışı ve `pg-monitor-password` dosyası.

#### CrmScrapeTargetMissing
DNS keşfi `api`/`worker` adresi bulamıyor: konteynerler çalışmıyor ya da `backend` ağında değil. `docker compose … up -d api worker`.

#### CrmApiHighErrorRate
5xx > %5 (10 dk). `logs api` (`Error` düzeyi, `X-Correlation-Id`), `CRM - API overview` → "5xx by route template" ile sorunlu yolu bulun; `/health/ready` (Postgres?), Conductor bağlantısı, son dağıtım. Kalıcıysa son sürüme geri dönün (§8).

#### CrmApiSlowRoute
Bir yol şablonunun p95'i > 1 sn. Panoda o yolun süresini ve `DB command p95` / havuz kullanımını karşılaştırın; yavaş sorgu günlüğü (`PG_LOG_MIN_DURATION_MS`) ve eksik indeks kontrolü. Yalnız o yol yavaşsa sorgu/indeks, hepsi yavaşsa kaynak (CPU/thread pool/DB).

#### CrmApiRateLimiting
Hız sınırlayıcı sürekli 429 veriyor. Hangi ilkenin (Auth/User/Tenant/LoginEmail) reddettiğini `CRM - Security signals` panosundan görün. Kötü niyetli tek kaynak ise vekil/güvenlik duvarında engelleyin; meşru yük ise `RateLimiting__<İlke>__PermitLimit` değerini yükseltin (§9.1).

#### CrmRuntimeThreadPoolStarvation
Thread pool kuyruğu büyüyor: engelleyici çağrı veya aşırı yük. Aynı anda CPU/aktif istek grafiklerine bakın; `logs` içinde zaman aşımı; geçiciyse yükü azaltın, kalıcıysa `API_CPUS`/kopya sayısı ve kodda senkron bekleme araştırılır.

#### CrmRuntimeHighMemory
Çalışma kümesi konteyner sınırının %85'i üstünde. Sızıntı şüphesinde GC grafiklerini (heap büyüyor mu?) izleyin; sınır aşılırsa konteyner OOM ile yeniden başlar (`docker inspect … OOMKilled`). Geçici çözüm: `API_MEM_LIMIT`/`WORKER_MEM_LIMIT`'i yükseltin ve kural eşiğini güncelleyin.

#### CrmOutboxBacklog
Bir modülün outbox'ında > 500 bekleyen mesaj. Worker ayakta mı (`ps`), `CRM - Background processing` → "Messages handled/s" akıyor mu? Yavaşsa Postgres/handler süreleri; ölüyse yeniden başlatın. Bekleyen sayısı: `docker compose … exec postgres psql -U postgres -d crm -c "SELECT count(*) FROM sales.outbox_messages WHERE processed_at IS NULL AND NOT is_dead"` (şema adını değiştirin).

#### CrmOutboxStuck
En eski bekleyen mesaj > 10 dk. Worker düşmüş, işleyici hata veriyor (üstel geri çekilme en çok `Outbox:MaxBackoffSeconds` = 1 sa) ya da veritabanı erişilemez. `logs worker` içinde `Outbox message … failed; retrying` (EventId 1101, günlük kapsamında `CorrelationId` ile isteğe kadar izlenir). Nedeni giderince mesajlar kendiliğinden işlenir; beklemek istemiyorsanız `UPDATE <şema>.outbox_messages SET next_attempt_at = NULL WHERE processed_at IS NULL AND NOT is_dead`.

#### CrmOutboxDeadLetters
Mesajlar `Outbox:MaxAttempts` sonrası ölü. Hata metni: `SELECT type, attempts, left(error, 300) FROM <şema>.outbox_messages WHERE is_dead AND processed_at IS NULL ORDER BY occurred_at` (hata metni istisna yığını içerir; kişisel veri içermemelidir, yine de paylaşırken kırpın). Nedeni giderdikten sonra yeniden kuyruğa alın: `UPDATE <şema>.outbox_messages SET is_dead = false, attempts = 0, next_attempt_at = NULL, error = NULL WHERE is_dead AND processed_at IS NULL` (yedek almadan toplu güncelleme yapmayın; kimin/neyin etkilendiğini önce SELECT ile görün).

#### CrmOutboxPollFailing
Tur tümüyle başarısız (`Outbox polling failed`, EventId 4000): veritabanı erişilemez, şema migrate edilmemiş ya da `crm_app` yetkisi eksik. `logs worker`, `pg_isready`, migrator çıkış kodu (§8).

#### CrmWorkerSamplerMissing
Worker ayakta ama outbox/silme göstergeleri yok: örnekleyici veritabanını okuyamıyor (`Metrics sampling failed` uyarısı, EventId 4300). Bu durumda birikim/silme alarmları **kördür**; bağlantı ve izinleri (`crm_app` SELECT) düzeltin.

#### CrmConductorUnreachable
Worker Conductor'a ulaşamıyor (workflow görevleri çalışmıyor). `conductor` konteyneri `healthy` mi, veritabanı `conductor` erişilebilir mi (§11.1). Geri gelince bekleyen görevler işlenir.

#### CrmConductorTaskFailures
Görevlerin > %20'si başarısız (askıdaki kiracıların `blocked` görevleri sayılmaz). Hangi `task_type` başarısız: pano "Tasks handled by task type and outcome". `logs worker` içinde `Workflow task … failed` (EventId 5000) ve yürütme listesindeki hata nedeni; `failed_terminal` iş kuralı hatasıdır (yeniden denenmez), `failed` geçici hatadır.

#### CrmWorkflowExecutionsFailing
30 dk'da > 5 yürütme `failed` oldu. Workflow modülünde yürütme listesi/hata nedeni; kural tanımı hatası mı, Conductor kesintisi mi (`engine_unavailable`, `engine_workflow_not_found`)?

#### CrmBackgroundJobFailing
Platform işleri (kullanım anlık görüntüsü / KVKK imha) turları hata veriyor (`Platform job … failed`, EventId 4200). Veritabanı bağlantısı, `pg_try_advisory_lock` ve hata ayrıntısı için `logs worker`.

#### CrmLoginFailureSpike
Uzun süre saniyede > 1 başarısız giriş. Tek kaynak mı çok hesap mı? `CRM - Security signals` → "Login attempts by outcome". Çok hesap + `rate_limited` = parola püskürtme/credential stuffing: vekilde kaynak IP engelleyin, auth hız sınırlarını sıkılaştırın (`RateLimiting__Auth__PermitLimit`), hedef hesapları denetim kaydından inceleyin. (Metriklerde IP/hesap yoktur; ayrıntı için API günlüğüne ve `audit.audit_log_entries`'e bakın.)

#### CrmAccountLockouts
15 dk'da > 5 hesap kilitlendi: toplu saldırı ya da bayat parolayla sürekli yeniden deneyen bir istemci/entegrasyon. Kilit 15 dk sürer (`Identity__LockoutMinutes`); kullanıcı parola sıfırlama yapabilir.

#### CrmRefreshTokenReuse
İptal edilmiş bir refresh token süre aşımı dışında yeniden sunuldu: olası token hırsızlığı. Aile iptal edildi (kullanıcı yeniden giriş yapar). Tekrarlıyorsa etkilenen kullanıcıyı denetim kaydından bulun, parolasını sıfırlatın, paylaşılan/güvensiz cihaz olup olmadığını sorun. Tek seferlik ve zararsız durumlar (sekme çakışması) `concurrent` olarak ayrı sayılır ve bu alarmı tetiklemez.

#### CrmTenantDeletionOverdue
Zamanı gelmiş KVKK silme talebi > 1 sa ilerlemiyor. Worker'da `TenantErasureService` (Platform işleri) çalışıyor mu, başka bir kopya `pg_try_advisory_lock`'u tutuyor mu; `Tenant erasure failed for …` (EventId 5010). Yasal süre sorumluluğu: gecikmeyi kaydedin.

#### CrmTenantDeletionFailed
Bir silme talebi `failed`. Adım hatası `platform.deletion_requests.last_error`'da (kişisel veri içermez); `MaxAttempts` sonrası talep failed kalır ve `deletion.failed` denetimi yazılır: nedeni giderip talebi platform yöneticisi arayüzünden yeniden başlatın ya da Migrator `erase-deleted-tenants` ile tamamlayın (§12).

#### CrmUsageSnapshotFailing
Bazı kiracılar için günlük kullanım anlık görüntüsü yazılamadı; sonraki turda yeniden denenir. Süreklilik varsa `logs worker` (`Usage snapshot failed for tenant …`, EventId 5000). Kullanım geçmişinde boşluk oluşur (geriye dönük üretilemez).

#### CrmPostgresDown
`pg_up == 0`: tüm uygulama etkilenir. `docker compose … ps postgres`, `logs postgres`, disk doluluğu (`docker system df`, `pgdata`), bellek sınırı (OOM). Geri yükleme gerekiyorsa §7.

#### CrmPostgresConnectionsHigh
Bağlantılar `max_connections`'ın > %80'i. Havuz boyutlarını (api/worker/conductor) ve `idle in transaction` oturumlarını inceleyin: `SELECT state, count(*) FROM pg_stat_activity GROUP BY 1`. Geçici çözüm `PG_MAX_CONNECTIONS` (bellek etkisine dikkat).

#### CrmPostgresDeadlocks
`crm` veritabanında deadlock. Ayrıntı PostgreSQL günlüğündedir (`deadlock detected`; sorgular). Genellikle aynı satırlara ters sırada yazan eşzamanlı işler; uygulama yeniden dener (EF yeniden deneme). Tekrarlıyorsa ilgili use-case'i geliştirmeye bildirin.

#### CrmPostgresCacheHitLow
Tampon önbelleği isabet oranı < %90 (30 dk, anlamlı disk okumasıyla). Çalışma kümesi `shared_buffers`/sayfa önbelleğine sığmıyor: `PG_SHARED_BUFFERS` ve `PG_MEM_LIMIT`'i artırmayı, eksik indeksleri değerlendirin.

#### CrmPrometheusStorageNearLimit
Prometheus depolaması `PROM_RETENTION_SIZE` sınırının > %90'ında; sınıra ulaşınca en eski veri silinir. `PROM_RETENTION_SIZE`'ı artırın ya da `PROM_RETENTION_TIME`'ı kısaltın (bkz. §15.8).

### 15.8 Saklama ve boyutlandırma

- **Saklama:** `PROM_RETENTION_TIME` (varsayılan **30 gün**) ve `PROM_RETENTION_SIZE` (varsayılan **5 GB**); hangisi önce dolarsa eski bloklar silinir. `promdata` birimi `docker volume ls`'te `<proje>_promdata`; yedeklenmesi gerekmez (metrikler yeniden üretilebilir tanı verisidir).
- **Ölçek (ölçülen; pilot topolojisi: 1 api + 1 worker + postgres-exporter, kazıma 15 sn):** ≈ **3 000 aktif seri** (`prometheus_tsdb_head_series`: api ≈ 500, worker ≈ 280, postgres-exporter ≈ 620, Prometheus kendisi ≈ 880) ve ≈ **150 örnek/sn** (≈ 13 milyon örnek/gün). Prometheus örnek başına ortalama ~1–2 bayt harcar ⇒ **≈ 15–30 MB/gün, 30 günde < 1 GB** (5 GB sınırı bolca yeter). Hedef sayısı arttıkça (api/worker kopyaları, node-exporter/cAdvisor) seri sayısı doğrusal artar; en çok seri `postgres-exporter` (`pg_settings_*`) ve `cadvisor` üretir. Seri sayısı, yeni kiracı/kullanıcı eklenmesiyle **artmaz** (kimlik etiketi yok); yalnız yeni yol şablonu, plan, modül veya görev türü eklenince büyür.
- **Kaynak sınırları:** Prometheus 512 MB / 0.5 CPU, Grafana 512 MB / 0.5 CPU, postgres-exporter 128 MB (`PROM_MEM_LIMIT`, `GRAFANA_MEM_LIMIT` …). Uygulamaya ek yük ihmal edilebilir: kazıma yanıtı 1 sn önbelleklenir, örnekleyici 15 sn'de birkaç indeksli sorgu çalıştırır.
- **Kardinalite kuralı:** seri sayısı sabit kalmalıdır: kiracı/kullanıcı/kayıt kimliği etiketi yoktur, HTTP yol **şablonu** (ham URL değil), Npgsql havuz adı etiketi düşürülür. Seri sayısı beklenmedik büyürse `topk(10, count by (__name__)({__name__=~".+"}))` ile hangi metriğin büyüdüğüne bakın.

### 15.9 Sır döndürme ve bakım

- `secrets/metrics-bearer-token`: dosyayı değiştirip `up -d api worker prometheus` (üçü de yeniden oluşur; kısa süre `CrmTargetDown` görülebilir).
- `secrets/grafana-admin-password`: yalnız ilk açılışta uygulanır (Grafana veritabanında saklanır); sonradan değiştirmek için `docker compose … exec grafana grafana cli admin reset-admin-password <yeni>`, ardından dosyayı da güncelleyin (kayıt için).
- `secrets/pg-monitor-password`: dosyayı değiştirip `up -d monitor-init postgres-exporter` (rol parolası her `up`'ta yeniden eşitlenir).
- Panoyu değiştirmek: `infra/observability/grafana/dashboards/*.json` dosyasını düzenleyin (arayüzden kaydedilemez, `allowUiUpdates: false`).

## 17. Entegrasyonlar (Milestone 8B): giden webhook, API anahtarı, OpenAPI — işletim notları

Kaynak: [m8b-entegrasyonlar.md](../plan/m8b-entegrasyonlar.md), karar K19. **Webhook gönderimi varsayılan olarak kapalıdır.**

- **Ağ (§1'e ek):** `backend` ağı `internal` kalır. Çıkış yalnız isteğe bağlı `egress` profilindeki iki servisten geçer (`deploy/docker-compose.egress.yml` katmanı): `egress-dns` (CoreDNS; Worker'ın tek çözümleyicisi, üst akış `EGRESS_DNS_UPSTREAM` **zorunlu**) ve `egress-proxy` (Squid; yalnız `CONNECT <IP>:443|8443`, hedef IP ACL'i: loopback/link-local (bulut metadata `169.254.169.254`)/CGNAT/özel ağ/rezerve reddedilir; yalnız `backend` alt ağından kabul eder; işleten `WEBHOOKS_ALLOWED_PRIVATE_CIDRS` ile yalnız **özel ağ** aralıklarını açabilir, loopback/metadata asla). API/Worker hiçbir zaman internet ağına bağlanmaz. İmaj etiketleri sabittir (`coredns/coredns:1.12.1`, `ubuntu/squid:6.10-24.10_edge`; `docker manifest inspect` ile doğrulandı).
- **Açma:** `.env`: `WEBHOOKS_ENABLED=true`, `EGRESS_DNS_UPSTREAM=<çözümleyici>` (+ isteğe bağlı `WEBHOOKS_ALLOWED_HOSTS`, `WEBHOOKS_ALLOWED_PRIVATE_CIDRS`); `docker compose -f deploy/docker-compose.prod.yml -f deploy/docker-compose.egress.yml --profile egress up -d`. Production'da `Enabled=true` iken `EgressProxy`/`DnsServer` yoksa API/Worker **başlamaz**; doğrudan çıkış yalnız Development/Testing. Kapatma: `WEBHOOKS_ENABLED=false` (fan-out satır yazmaz; kapalıyken üretilen olaylar sonradan teslim edilmez).
- **Doğrulama provası (DevOps):** (1) API/Worker konteynerinden doğrudan dış çıkış **yok**; (2) Worker yalnız egress-dns/proxy üzerinden çıkıyor; (3) proxy `CONNECT 169.254.169.254:443` ve `CONNECT 10.213.77.1:443`'ü **reddediyor** (`docker compose exec egress-proxy sh -c 'echo -e "CONNECT 169.254.169.254:443 HTTP/1.1\r\nHost: x\r\n\r\n" | nc 127.0.0.1 3128'` → 403); (4) `internal` ağdaki Worker'ın `egress-dns`'e ulaştığı ispatlanır (olmazsa uygulama `DnsServer` ile doğrudan UDP sorgular).
- **Yükseltme (§8 adım 2'ye ek):** `deploy/secrets/integrations-encryption-key` dosyası **zorunludur** (`generate-secrets.*` mevcut kurulumda yalnızca bu dosyayı üretir; mevcut sırlara dokunmaz). Yoksa api/worker/migrator başlamaz.
- **İzleme (§9):** `crm.api_keys.auth_failed` sayacı (yalnız önek loglanır), Worker günlüğünde `Webhook delivery is disabled/dispatcher will NOT start` iletileri, kuyruk gecikmesi: `SELECT count(*) FROM integrations.delivery_queue WHERE due_at < now() - interval '10 minutes'` (0'a yakın olmalı). `WEBHOOKS_ENABLED` doğrulaması: `GET /api/v1/integrations/status` → `webhooksEnabled`.
- **Sorun giderme (§11):** teslimat günlüğünde `blocked_destination` (hedef çözümlemesi iç/özel adres veya URL politikası; `AllowedHosts`/`AllowedPrivateCidrs` ve proxy ACL'ini denetleyin), `tls_error` (sertifika/ad uyuşmazlığı; sertifika doğrulaması kapatılamaz), `dns_error` (3 deneme sonra terminal), `retries_exhausted` (≈ 20,6 sa sonra ölü mektup; arayüzden yeniden gönderilir), kuyruk birikimi (yukarıdaki sorgu; Worker durumu ve askıdaki kiracılar: askıda teslimat bekler, `due_at +5 dk`). Abonelik art arda 10 terminal hatada `failing` nedeniyle pasifleşir (arayüzde rozet); düzeltip yeniden etkinleştirin.
- **Geri yükleme (§7) ve prova (§13):** anahtar yedeği geri yüklemeyle birlikte gerekir; **imha edilmiş bir kiracının API anahtarı çalışmaz** (satır yok → 401; `migrator erase-deleted-tenants` sonrası doğrulayın). Anahtar kaybında: aboneliklerde sır döndürülür.
- **Ortam değişkenleri (§14'e ek):** `Integrations__Webhooks__{Enabled,EgressProxy,DnsServer,AllowedHosts,AllowedPrivateCidrs,AllowedPorts,TimeoutSeconds,MaxAttempts,PerTenantPerMinute,…}`, `Integrations__ApiKeys__{DefaultLifetimeDays,MaxLifetimeDays,CacheSeconds,FailureThrottle__MaxFailures,…}`, `Integrations__Encryption__{CurrentKeyId,Keys__<id>}`, `Integrations__OpenApi__CacheMinutes`, `RateLimiting__ApiKey__PermitLimit`, `RateLimiting__ApiKeyTenant__PermitLimit`. Aralık tutarsızlığı açılışta reddedilir (`ValidateOnStart`).
- **OpenAPI:** `GET /api/v1/integrations/openapi.json` yalnız kimliği doğrulanmış `org.integrations.manage` sahibine sunulur; anonim `/openapi/v1.json` ve `/scalar` Production'da kapalıdır (`Docs:Enabled`, §9).
