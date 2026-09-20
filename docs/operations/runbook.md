# CRM — Operasyon el kitabı (pilot yayın)

Kapsam: kurum içi / kendi veri merkezinde Docker Compose ile tek sunuculu kurulum (Milestone 5). Kubernetes/Helm hedefi (K13) sonraki iştir.
Dosyalar: [`deploy/`](../../deploy) (compose, `.env.example`, sır üreticiler, yedek/geri yükleme, TLS örnekleri),
[`deploy/restore.md`](../../deploy/restore.md) (geri yükleme adımları), mimari: [backend.md](../architecture/backend.md), plan: [m5-pilot-yayin.md](../plan/m5-pilot-yayin.md).

## 1. Genel bakış

```
kullanıcı ──HTTPS──▶ [TLS sonlandırıcı: Caddy/nginx/LB]  (sizin; deploy/tls/*.example)
                          │ HTTP, X-Forwarded-For/-Proto
                          ▼
   frontend ağı ─────▶ web  (nginx-unprivileged :8080; tek yayınlanan port, varsayılan 127.0.0.1:8080; yalnız web bu ağdadır)
                          │ /api/*  (yalnız bu yol)
   edge ağı (internal: web ↔ api; dışarıya çıkışı YOK)
                          ▼
   backend ağı (internal: dışarıya çıkışı YOK; web bu ağda DEĞİL)
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

Güvenlik duruşu (varsayılan): yalnız `web` portu yayınlanır; `backend` ve `edge` ağları `internal: true` olduğundan API/Worker/Postgres/Conductor dışarıya bağlantı
başlatamaz. Ağ ayrımı (M5): `web` yalnız `frontend` (yayınlanan port; Docker internal bir ağdan port yayınlayamaz) ve `edge` (yalnız `api` ile paylaşılır) ağlarındadır;
`postgres`/`conductor`/`redis`/`worker` ile aynı ağda **değildir**, yani ele geçirilmiş bir `web` konteyneri veritabanına, Conductor'a, Redis'e ya da worker'a ulaşamaz (adları çözülmez, IP'ye TCP açılmaz).
`api` `edge` + `backend` ağlarındadır (ikisi de internal): dışarıya yolu yoktur. `api` nginx'i `edge` alt ağından görür; bu yüzden `ForwardedHeaders__KnownNetworks__0` = `EDGE_SUBNET`'tir (`.env`; varsayılan `10.213.79.0/24`); parola yok — sırlar `.env`/`secrets/` içindedir ve `:?` ile zorunludur; API ve Worker veritabanına yalnız DML yetkili `crm_app` rolüyle
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

`.env`'de gözden geçirin: `ALLOWED_HOSTS`, `WEB_BIND`/`WEB_PORT`, `TRUSTED_PROXY_CIDR`, kaynak sınırları, `CRM_VERSION`.
- **`ALLOWED_HOSTS`** = kullanıcıların tarayıcıya yazdığı **herkese açık ad(lar)** (`;` ile ayrılır; ör. `crm.sirket.local;crm.sirket.com`). API başka `Host` başlıklarını `400 Invalid Hostname` ile
  reddeder (Host başlığı sahteciliği/önbellek zehirlemesi savunması). `*` ya da joker (`*.sirket.com`) **kullanmayın**, IP adresi ve sahibi olmadığınız adı eklemeyin.
  `localhost` compose tarafından yalnız konteyner sağlık denetimi için eklenir; `.env`'ye yazmanıza gerek yok (yerel duman testi için `BASE_URL=http://localhost:...` kullanıyorsanız `localhost` zaten geçerlidir).
- **`TRUSTED_PROXY_CIDR`** (nginx'in `X-Forwarded-For` başlığına güvendiği vekil adresi/ağı). **Varsayılan `127.0.0.1/32` = hiçbir şeye güvenilmez**: nginx `X-Forwarded-For`'u yok sayar, istemci adresi sahtelenemez.
  Sonuç: güvenilir vekil tanımlı değilken istemci adresi, yayınlanan porta bağlanan adrestir (aynı makinedeki vekil ya da Docker Desktop'ta `frontend` ağının ağ geçidi, ör. `10.213.78.1`),
  yani IP başına hız sınırları (giriş azaltma dâhil) o **tek adrese** göre sayılır. Vekil varsa açıkça ayarlayın:
  aynı makinedeki Caddy/nginx (`WEB_BIND=127.0.0.1`) → `TRUSTED_PROXY_CIDR=<FRONTEND_SUBNET'in ilk adresi>/32` (varsayılan alt ağla `10.213.78.1/32`; `docker compose logs web` vekilin bağlandığı adresi gösterir);
  başka makinedeki LB (`WEB_BIND=<özel IP>`, güvenlik duvarıyla yalnız LB'ye açık) → `TRUSTED_PROXY_CIDR=<LB adresi>/32`.
  `WEB_BIND` `0.0.0.0`/yönlendirilebilir bir IP iken **geniş aralık** (`FRONTEND_SUBNET`, `0.0.0.0/0`) vermeyin: her istemci `X-Forwarded-For` ile adresini sahteleyebilir.
  **Yükseltme notu:** eski bir `.env` `TRUSTED_PROXY_CIDR=10.213.78.0/24` içerir (eski varsayılan; ağ geçidi bu aralıktadır → Docker Desktop'ta ya da `WEB_BIND=0.0.0.0` iken herkes sahteleyebilir). Değeri yukarıdaki gibi düzeltin;
  ayrıca yeni `EDGE_SUBNET` satırını ekleyin (`.env.example`).
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
docker compose -f deploy/docker-compose.prod.yml run --rm --user root migrator create-platform-admin
```

**Neden `--user root`:** parola dosyası ana makinede `0600`'dür (yalnız sahibi okur; `generate-secrets.sh`; Windows'ta yalnız kullanıcı ACL'si) ve konteynerde `/run/secrets/platform_admin_password` olarak **yazılabilir** bağlanır
(`docker-compose.prod.yml` › `migrator` › `volumes`; `secrets:` girdileri her zaman salt okunur bağlandığından dosya boşaltılamazdı). Migrator'ın normal kullanıcısı (`app`, uid 1654) `0600` dosyayı okuyamaz;
bu yüzden yalnız bu tek seferlik, `--rm` ile biten, dışarıya çıkışı olmayan (`backend`) komut root olarak çalışır. Her `up`'ta çalışan `migrate` uid 1654 ile kalır ve dosyayı okuyamaz.
**Başarıdan sonra Migrator parola dosyasını boşaltır.** Boşaltamazsa (uyarı yazar) dosyayı elle boşaltın: `: > deploy/secrets/platform-admin-password`.
Bootstrap yöneticisi **ilk girişte parolasını değiştirmek zorundadır** (`mustChangePassword`; değişene kadar yalnız parola değiştirme uçları çalışır): dosyadaki parola tek seferliktir, girişten sonra geçersizdir.

- Hesap yoksa: hesap + "Platform" adlı işletim organizasyonu (yalnız bu yönetici hesabı; müşteri verisi yok) + Administrator üyeliği oluşur.
- Hesap varsa: parolasına dokunulmadan platform yöneticisi yapılır (yükseltme); zaten yöneticiyse hiçbir şey değişmez (çıkış kodu 0).
- Geçersiz girdi (e-posta yok/parola < 12 karakter ya da yaygın/e-posta adını içeren parola) → çıkış kodu 2 ve neden günlükte.
- Bittikten sonra parola dosyasının **boş** olduğunu doğrulayın (Migrator boşaltır; boşaltamadıysa `: > deploy/secrets/platform-admin-password`). Parola **sıfırlama** (unutulan parola) e-posta altyapısıyla birlikte sonraki aşamadadır
  (parola değiştirme uçtan mevcuttur: `POST /me/password`, mevcut parolayı bilen kullanıcı için; unutulan parolayı yalnız sistem yöneticisi DB/`create-platform-admin` ile kurtarır) — bu yüzden platform yöneticisi için güçlü bir parola (≥ 12 karakter, yaygın parola/e-posta adı yasak) kullanın ve önce kasaya kaydedin.

### 3.5 İlk organizasyon (pilot şirket) ve yöneticisi

Platform yöneticisi olarak giriş yapıp API ile açın (`adminPassword: null` → tek seferlik parola üretilir ve **yalnızca bu yanıtta** döner; yanıt `Cache-Control: no-store`):

```bash
BASE=https://crm.sirket.local
read -rsp 'Platform yöneticisi parolası (bootstrap parolası ilk girişte değiştirilmiştir; dosya boşaltıldı): ' PLATFORM_PW; echo
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

healthz → güvenlik başlıkları → API → giriş → `/me` → lead oluştur/oku. İsteğe bağlı: `PLATFORM_ADMIN_EMAIL` **ve** `PLATFORM_ADMIN_PASSWORD` ortam değişkenleri de verilirse platform yöneticisiyle girip
`GET /api/v1/platform/organizations?pageSize=100` çağrılır ve en az bir organizasyonun `"isSystem":true` olması (platform işletim kiracısı `is_system` işaretli) **zorunlu** tutulur; yoksa betik hata verir (bootstrap parolası değiştirilmemişse de hata verir). Parolalar/token süreç listesinde (`ps`) görünmez:
gövde `--data-binary @-` ile stdin'den, `Authorization` başlığı `0600` geçici dosyadan (`-H @dosya`) gider. (Yerelde `localhost` adını kullanın; IP adresi `AllowedHosts` nedeniyle reddedilir.)
Ardından tarayıcıdan giriş yapın, bir lead açın; İş akışları > Yürütmeler'de kuralın tetiklendiğini görün.

### 3.7 TLS

TLS **konteynerde sonlandırılmaz**; kurum CA'sından sertifikalı bir vekil/LB kullanın: [`deploy/tls/Caddyfile.example`](../../deploy/tls/Caddyfile.example),
[`deploy/tls/nginx-tls.conf.example`](../../deploy/tls/nginx-tls.conf.example) (HAProxy/F5 için eşdeğer: `X-Forwarded-For` ve `X-Forwarded-Proto` gönderin, arka uç `WEB_BIND:WEB_PORT`).
Vekil başka bir makinedeyse: `WEB_BIND`'i sunucunun özel IP'sine ayarlayın (güvenlik duvarı ile yalnız vekile açın) ve `TRUSTED_PROXY_CIDR`'ı vekilin adresine (`/32`) çekin.
Vekil aynı makinedeyse (`WEB_BIND=127.0.0.1`): `TRUSTED_PROXY_CIDR=<FRONTEND_SUBNET'in ilk adresi>/32` (ör. `10.213.78.1/32`, bkz. §3.1). Ayar yapılmazsa varsayılan (`127.0.0.1/32`) `X-Forwarded-For`'a hiç güvenmez:
uygulama güvenli kalır ama tüm kullanıcılar vekilin adresi olarak görünür (IP başına hız sınırı ve denetim IP'si tek adrese düşer). `web` konteyneri `X-Forwarded-Proto: https` gördüğünde HSTS başlığını kendisi ekler.

## 4. Sırlar ve anahtarlar

| Sır | Yeri | Rotasyon |
|---|---|---|
| `POSTGRES_PASSWORD` (süper kullanıcı; yalnız db-init/yedek) | `.env` | `.env`'i değiştirip PostgreSQL'de `ALTER ROLE postgres PASSWORD` + `up -d` (postgres konteyneri ilk kurulumdaki parolayla başladı) |
| `CRM_OWNER_PASSWORD`, `CRM_APP_PASSWORD`, `CONDUCTOR_DB_PASSWORD` | `.env` | `.env`'i değiştirin → `up -d`: `db-init` rol parolalarını eşitler, servisler yeni değerle yeniden yaratılır |
| JWT imza anahtarı (RS256) | `secrets/jwt-signing-key.pem` → `/run/secrets/Auth__SigningKeyPem` | Yeni anahtar → `up -d api`: tüm access token'lar geçersiz olur, kullanıcılar (refresh token ile otomatik ya da yeniden giriş yaparak) devam eder |
| Platform yöneticisi parolası | `secrets/platform-admin-password` (yalnız bootstrap; ana makinede `0600`, migrator'a yazılabilir bağlanır) | Migrator başarıdan sonra dosyayı boşaltır (uyarırsa elle boşaltın); bootstrap parolası ilk girişte değiştirilir (§3.4) |
| `REDIS_PASSWORD` | `.env` | Redis kullanılıyorsa `REDIS_CONNECTION` ile birlikte |

Üretimde API, `Auth:SigningKeyPem` veya `ConnectionStrings:Database` yoksa **başlamaz** (testli: `PlatformApiTests.Production_RefusesToStart_*`);
geçici geliştirme anahtarı yalnız Development/Testing ortamlarındadır.

## 5. Kayıt modu (`Registration:Mode`)

`REGISTRATION_MODE` (varsayılan `disabled`): `POST /api/v1/auth/signup` → `403 auth.signup_disabled` (TR/EN metinli; girdi doğrulamasından önce),
`GET /api/v1/auth/config` → `{ "signupEnabled": false }` (anonim, 60 sn önbellek) ve web "kaydol" bağlantısını gizler / `/signup` sayfasını girişe yönlendirir.
`open` yalnız Development/Testing varsayılanıdır; kurum içi üretimde açmayın. Geçersiz değer API'yi başlatmaz. Organizasyonlar §3.5 ile açılır.

**SaaS (M7):** `open` kip, dış müşterilerin kendi kendine kaydı için **bilinçli** açılır; yeni organizasyon `Platform:Signup:PlanCode` planıyla (varsayılan `starter`, 14 gün deneme, örnek limitler) açılır ve deneme bitince **salt okunur** olur (§12, plan belgesi). Kurum içi kurulumda `disabled` kalır ve organizasyonlar §3.5 ile `internal` planda açılır. Açık kayıt **CAPTCHA/e-posta doğrulaması içermez** (yalnız IP başına auth hız sınırı vardır); dış SaaS açılışından önce ayrıca ele alınmalıdır.

## 6. Yedekleme

```bash
BACKUP_GPG_RECIPIENT=yedek@sirket.local ./deploy/backup.sh            # Linux;  BACKUP_DIR, RETENTION_DAYS (14); şifreleme ZORUNLU: BACKUP_GPG_RECIPIENT | BACKUP_OPENSSL_PASSFILE
.\deploy\backup.ps1 -BackupDir D:\yedek -GpgRecipient yedek@sirket.local   # Windows; -RetentionDays; şifreleme ZORUNLU: -GpgRecipient (veya BACKUP_GPG_RECIPIENT)
```

**Şifreleme zorunludur:** anahtar/alıcı verilmezse betikler hiçbir docker komutu çalıştırmadan, açık bir iletiyle **reddeder** (çıkış kodu 2). Bilerek açık metin isteniyorsa `./deploy/backup.sh --no-encryption` /
`.\deploy\backup.ps1 -NoEncryption` verilir; betik yedeğin **açık metin** saklandığına dair yüksek sesli `WARNING` yazar (yalnız izole prova/geçici kullanım için). Şifreleme başarısız olursa açık metin döküm silinir ve betik hatayla biter.
Anahtar sağlama: gpg için genel anahtar, yedeği alan kullanıcının anahtarlığında olmalıdır (`gpg --import`; çözmek için özel anahtar yalnız geri yükleme makinesinde/kasada); `BACKUP_OPENSSL_PASSFILE` yalnız sahibi okuyabilen (`0600`) bir parola dosyasıdır
(yalnız `backup.sh`; `.gz.enc`). Cron/Görev Zamanlayıcı'da değişkeni/parametreyi tanımlamayı unutmayın; aksi halde yedek **sessizce değil, hata koduyla** başarısız olur — izleyin. Çözme adımları: [restore.md](../../deploy/restore.md) (Notlar).

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
   **Ağ ayrımına (M5) geçişte:** `.env`'ye `EDGE_SUBNET=10.213.79.0/24` ekleyin (LAN ile çakışırsa değiştirin) ve `TRUSTED_PROXY_CIDR`'ı §3.1'e göre düzeltin; `up -d` yeni `edge` ağını yaratır, `web` ve `api` yeniden yaratılır.
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
  1. **Talep:** platform yöneticisi `POST /api/v1/platform/organizations/{id}/deletion-request` `{ reason, retentionDays?, confirmTenantName, currentPassword }` (varsayılan **30 gün**, 7–90). Kiracı **anında** `pending_deletion` olur (tüm kullanıcılar dışarıda, giriş yok).
     **Sunucu tarafı onay (C-SEC2 H2):** `confirmTenantName` sunucudaki kiracı adıyla (kırpılmış, harf duyarlı) eşleşmelidir (`422 platform.confirmation_mismatch`); `currentPassword` çağıran platform yöneticisinin **kendi parolasıdır** (step-up, §12.1;
     eksik `422 platform.step_up_required`, yanlış `422 platform.step_up_failed`, çok deneme `429 platform.step_up_rate_limited`). Başarılı talep aynı işlemde `deletion.requested` denetim satırı ve `TenantDeletionRequested` entegrasyon olayı yazar
     (bildirim modülü bu olayı tüketecek). **`reason` serbest metindir: kişisel veri (ad, e-posta, telefon) yazmayın** — konsol bunu uyarır; imhada `[redacted]` yapılır (L1).
  2. **Bekleme:** talep bekleme süresi içinde `.../deletion-request/cancel` ile iptal edilir (kiracı önceki duruma döner).
  3. **İmha:** süre dolunca Worker (`TenantErasureService`, `Platform:Deletion:PollMinutes` = 10 dk, tek örnek) **kalıcı** imha yapar: kiracının tüm iş verisi (yumuşak silinenler dahil; yedi modül), outbox, denetim kaydı (`audit.audit_log_entries`), Conductor'daki yürütme kayıtları,
     yalnız bu kiracıya ait hesaplar ve oturumları; başka kiracıda üyeliği olan (ortak) hesaplar ve platform yöneticisi hesapları **kalır** (yalnız bu kiracıdaki üyelik/rol gider). Adımlar idempotenttir ve `erased_steps` ile yeniden başlatılır; hata → `failed`, üstel bekleme, `Platform:Deletion:MaxAttempts` (10) sonrası `deletion.failed` denetimi + günlük uyarısı (konsolda kırmızı).
  4. **Mezar taşı:** `platform.tenant_accounts` satırı `deleted` (ad `[deleted]`, slug `deleted-xxxxxxxx`, askı gerekçesi/kipi temizlenir), `platform.deletion_requests` satırı imha raporuyla (kişisel veri yok; `reason` = `[redacted]`) ve `platform.platform_audit_entries`
  (hedef adı `[deleted]`, `details.reason` silinir) **bilerek kalır** (hesap verebilirlik; `Platform:Audit:RetentionDays` = 1825 gün).
  Sistem (işletim) organizasyonu ve **aktif platform yöneticisi üyesi olan her organizasyon** asla askıya alınamaz, engellenemez, silme sürecine alınamaz ya da imha edilemez (`422 platform.system_tenant_protected`; §12.1).
  Önbellek girdileri (kiracı önekli, TTL ≤ 10 dk) imha sonunda geçersiz kılınır.
  **Yedekler:** imha yedekleri temizlemez; silinmiş veri yedeklerde saklama süresi (`RETENTION_DAYS`) boyunca kalır — saklama ve imha politikasını buna göre yazın. **Geri yükleme sonrası** imha edilmiş kiracının yeniden görünmemesi için
  `migrator erase-deleted-tenants` çalıştırılır (tüm mezar taşları için imhayı yeniden koşar; idempotent).
- **Erişim:** kiracı ayrımı satır bazlıdır (`TenantId` + global filtre; testli); platform yöneticisi organizasyon açar, plan/deneme/askı/silme yönetir ve sayaç (kullanım) görür ama kiracı iş verisini **okuyamaz** (taklit yok; M7). Yönetici hesapları ve yedek erişimini kısıtlayın; `.env`/`secrets/` dosya izinleri sıkı tutulmalıdır.
- **İletim:** dış trafik TLS ile (§3.7); iç ağ (backend) yalnız konteynerler arasıdır.
- **VERBİS / aydınlatma metni / veri işleyen sözleşmeleri** teknik değil, kurumsal iştir (kontrol listesi: [m5-pilot-yayin.md](../plan/m5-pilot-yayin.md)).

### 12.1 Yıkıcı platform komutları: step-up, korunan kiracı, platform yöneticisi yaşam döngüsü (C-SEC2)

**Step-up (yeniden kimlik doğrulama).** Şu komutlar çağıran platform yöneticisinin **kendi parolasını** gövdede `currentPassword` olarak ister ve sunucuda doğrular: silme talebi (`deletion-request`), **`blocked`** askı
(`suspend` + `mode = blocked`; `readOnly` istemez), başarısız imhayı yeniden deneme (`deletion-request/retry`), platform yöneticisi yetkisini geri alma (`admins/{id}/revoke`). Çalınmış bir access token (localStorage'daki 15 dk'lık jeton) tek başına yetmez.
Hatalar 401 **değildir** (istemci oturumu düşmez): eksik parola `422 platform.step_up_required`, yanlış `422 platform.step_up_failed`, deneme sınırı `429 platform.step_up_rate_limited`. Yanlış parola giriş ile **aynı** korumalara yazar
(hesabın kalıcı hatalı-deneme sayacı → eşikte hesap kilidi; (IP, hesap) çifti başına 5 hata/15 dk). Parola ve hash asla loglanmaz. Kiracı adı onayı (`confirmTenantName`) ayrıca sunucuda doğrulanır (§12 adım 1).

> **Karar notu (ADR tarzı) — MFA / ikinci onaylayıcı yok (kabul edilen risk).** Durum: kabul. Bağlam: e-posta altyapısı ve TOTP/WebAuthn yoktur; platform ekibi küçüktür. Karar: yıkıcı komutlar bir **tek** yöneticinin parolasını yeniden doğrulamasıyla
> sınırlanır; ikinci onaylayıcı (four-eyes) ve MFA sonraki aşamaya bırakılır. Sonuçlar: parolası ele geçirilmiş bir platform yöneticisi hesabı yıkıcı komut verebilir; hafifletmeler — silme bekleme süresi (7–90 gün; iptal edilebilir), her komutun denetim satırı
> ve olayı (`TenantDeletionRequested`), step-up hız sınırı/kilit, platform yöneticisi oturumlarının kısa ömrü (8 saat), korunan kiracı kuralı. Yeniden değerlendirme: MFA/e-posta altyapısı geldiğinde veya ikinci platform yöneticisi sayısı ≥ 2 iken.

**Korunan kiracı (H1).** Sistem (işletim) organizasyonu **ya da aktif platform yöneticisi üyesi olan** (hesap aktif + bayrak var + üyelik aktif) her organizasyon askıya alınamaz, engellenemez, silme sürecine alınamaz ve imha edilemez; kural alan modelinde
(`TenantAccount`), komut işleyicilerinde ve imha işinde (her talep başında ve **her yıkıcı adımdan önce**) ayrı ayrı uygulanır. Yükseltilen (M7 öncesi) kurulumlarda `is_system` bayrağı eksik olabilir: `migrate` (ve `backfill`) aktif platform yöneticisi üyesi olan her
kiracı hesabını `is_system = true` işaretler; `create-platform-admin` de `Created` yanında `Promoted`/`Unchanged` için hesabın aktif üyeliği olan kiracıları işaretler. Sonuç: bir müşteri hesabı platform yöneticisi yapılırsa (ya da bir platform yöneticisi
müşteri organizasyonuna davetle katılırsa) o organizasyon **korunan** olur; silinmesi gerekiyorsa önce yönetici yetkisi geri alınır (aşağıda) ya da üyeliği pasifleştirilir. Talep sonradan korunan hale gelen (aktif platform yöneticisi üyesi olan) bir kiracı için imha işi talebi **sistemce iptal eder**
(hesap eski durumuna döner, `deletion.cancelled` denetimi `by = system`, `TenantDeletionCancelled` olayı); `is_system` işaretli kiracıda doğrudan SQL ile zorlanmış bir talep ise işlenmeden olduğu gibi bırakılır (M7 davranışı).

**Platform yöneticisi yaşam döngüsü (M6).**
- Liste: `GET /api/v1/platform/admins` (pasif olanlar dahil). Konsol: **Platform yöneticileri** sayfası.
- Geri alma: `POST /api/v1/platform/admins/{userId}/revoke` `{ currentPassword, deactivate? }` (step-up) veya Migrator: `PLATFORM_ADMIN_EMAIL=… [PLATFORM_ADMIN_DEACTIVATE=true] docker compose … run --rm migrator revoke-platform-admin`.
  Bayrak kaldırılır, `deactivate` ile hesap da pasifleşir ve **tüm refresh token aileleri iptal edilir** (yetki her istekte veritabanından doğrulandığından access token'lar da hemen işlevsiz kalır). **Son aktif platform yöneticisi geri alınamaz/pasifleştirilemez**
  (`409 platform.last_platform_admin`; Migrator çıkış kodu 4) — önce bir başkasını oluşturun. `platform_admin.revoked` denetimi yazılır.
- Bootstrap yöneticisi `MustChangePassword = true` ile açılır; `create-platform-admin` başarıdan sonra parola dosyasını boşaltır (§3.4).
- **Oturum süreleri (M4, kısmi):** platform yöneticisi oturumları (refresh token ailesi) ilk girişten itibaren **8 saatte** mutlaken biter (`Identity__PlatformAdminRefreshFamilyHours`, varsayılan 8; normal kullanıcı 30 gün) ve **60 dk boşta** kalınca kapanır
  (`Identity__PlatformAdminRefreshIdleMinutes`, varsayılan 60 = platform yöneticisi refresh token ömrü; istemci açıkken yenileme onu uzatır, aile ömrünü aşamaz). Yapılandırma yalnız yeni oturumlar (aileler) için geçerlidir.
  **Sonraki iş:** jetonlar hâlâ tarayıcı `localStorage`'ındadır (XSS'e açık). Hedef: refresh token'ı `HttpOnly; Secure; SameSite=Strict` çerezine, access token'ı bellekte tutmak (CSRF için çift gönderim/`Origin` denetimi; nginx'te aynı-köken). Bu kartta uygulanmadı.
- **İzleme (L4):** `/api/v1/platform/**` üzerinde kimliği doğrulanmış **ama platform yöneticisi olmayan** kullanıcının reddedilen (403) istekleri kaba düzeyde sayılır: metrik `crm.platform.rejected_requests` (`Sense.Crm.Security` meter'ı, etiket: HTTP yöntemi) ve
  kişisel veri içermeyen `Warning` günlüğü ("Rejected a … request to the platform console…", olay 4301). Ani artış = keşif/sızma denemesi; `platform.audit` satırı **yazılmaz** (yalnız yetkili yönetici eylemleri denetlenir).
- **`organization.created` denetimi (L4):** satır Identity işlemi commit edildikten sonra doğrudan yazılır; yazılamazsa günlüğe `Error` düşer ve `OrganizationCreated` olayı (aynı Identity işleminde outbox'a yazılır, `ActorUserId` taşır) Platform işleyicisinde satırı **tamamlar** (idempotent).
  İki farklı DbContext arasında dağıtık işlem yoktur; tek işlem mümkün olmadığından "olay + tamamlama" seçildi.

### 12.2 İmha doğrulaması, adım kaydı ve yeniden deneme (M1, M2, L3)

- **Yıkıcı işlemden önce yeniden doğrulama (M1):** işin her turunda talep `SELECT … FOR UPDATE SKIP LOCKED` ile kilitlenir (eşzamanlı iptal ya da başka Worker varsa atlanır) ve aynı işlemde durum (`scheduled|running|failed`), `scheduled_for ≤ now`, hesap `pending_deletion`,
  sistem değil, aktif platform yöneticisi üyesi yok doğrulanır. `deletion_requests` `xmin` eşzamanlılık belirteci taşır: iptal ↔ başlatma yarışını kaybeden yazma `409` alır. İş kilidi işlem düzeyindedir (`pg_try_advisory_xact_lock`). Ön koşul bozuksa: korunan kiracı → talep sistemce iptal;
  hesap `pending_deletion` değil → `erasure.precondition_failed` ile kalıcı `failed` (otomatik yeniden deneme durur).
- **Tombstone öncesi doğrulama (M2):** mezar taşı yazılmadan önce (a) her `ITenantDataEraser`'ın isteğe bağlı `VerifyErasedAsync` kancası ve (b) `information_schema.columns`'ta **`tenant_id` kolonu olan tüm taban tablolar** (her şema; yalnız `platform.tenant_accounts` ve `platform.deletion_requests` bilerek hariç) taranır. Kiracıya ait satır kalmışsa adım
  `erasure.verification_failed: <şema.tablo>=<sayı>` ile başarısız olur, talep `failed` kalır, **tombstone yazılmaz** (hata iletisi satır içeriği taşımaz). Ne kalmışsa giderilip yeniden denenir.
- **Kayıt bütünlüğü:** yeni bir modülün DbContext'i `ModuleCatalog`'a eklenince Worker ve Migrator `Program.cs`'e de `AddModuleDbContext<…>` ile eklenmelidir; `ErasureRegistrationArchitectureTests` unutulursa kırmızı olur (ve tüm kiracı varlıklarının imha planında olduğunu doğrular).
  Veritabanı dışı depolar (M8C nesne depolama, webhook/bildirim tabloları) `ITenantDataEraser` uygulayıp `Order` ile kaydolarak aynı akışa katılır (`Name` benzersiz, `TenantErasureSteps` doğrular); `VerifyErasedAsync` ile kalıntıyı bildirir.
- **Yetim Conductor yürütmeleri (L2):** `engine_workflow_id` yazılamamış ama motorda başlatılmış yürütmeler, CRM yürütme kimliğiyle (`correlationId`) Conductor `GET workflow/{ad}/correlated/{id}` ile bulunup imhada silinir (rapor: `conductor.orphan_executions`).
- **Başarısız imhayı yeniden deneme (L3):** konsolda kiracı detayı `lastError` (kod + tablo/sayı; kişisel veri yok) ve deneme sayısını gösterir; **Yeniden dene** düğmesi (`POST …/deletion-request/retry`, step-up) `attempts`'i sıfırlar ve `deletion.retried` denetimi yazar.
  Yalnız `failed` talep için (aksi `409 platform.deletion_not_retryable`).

### 12.3 Denetim tablolarının salt-eklemeli koruması (M3)

`audit.audit_log_entries` ve `platform.platform_audit_entries` veritabanı **tetikleyicileriyle** korunur (`crm_app` tam DML yetkili olsa bile): `UPDATE`/`DELETE`/`TRUNCATE` reddedilir (`audit_immutable`, SQLSTATE 42501). İstisnalar yalnız işlem başına `SET LOCAL crm.audit_maintenance = '…'` işaretiyle **ve** koşullarıyla:
`erasure` (KVKK imhası) — `audit_log_entries` için yalnız DELETE ve yalnız kiracı `platform.tenant_accounts`'ta `pending_deletion|deleted` iken; `platform_audit_entries` için yalnız `target_tenant_name`/`details` sütunlarının redaksiyonu (UPDATE) ve aynı kiracı koşulu;
`retention` (saklama işi) — yalnız `platform_audit_entries`, yalnız **30 günden eski** satırlar (yapılandırılan `Platform:Audit:RetentionDays` < 30 ise 30 uygulanır). Kırılma-cam: **süper kullanıcı** oturumunda `SET crm.audit_maintenance = 'superuser'` (uygulama rolü süper kullanıcı değildir; işaret onda işlemez) —
DBA yalnız olağanüstü durumda kullanır ve yaptığını kayda geçirir. **Kalan risk:** uygulama süreci ele geçirilirse saldırgan `erasure` işaretini koyabilir, ama yalnızca silme sürecindeki bir kiracının satırlarını silebilir; tam koruma için denetimi ayrı bir salt-ekleme deposuna/WAL arşivine akıtmak sonraki iştir.

### 12.4 Askıdayken düşen olaylar ve `TenantReactivated` (L5)

Askıdaki/salt okunur/silme bekleyen kiracıda kullanıcı kaynaklı yazma işlemleri (403 `tenant.suspended`) **hiç gerçekleşmez**, dolayısıyla olay üretmez; ancak askı sırasında **halihazırda kuyruğa alınmış** iş (Worker outbox, Conductor görevleri, olay
tüketicileri) kiracı kapısından geçemeyebilir: bu olaylar **atılır (yeniden oynatılmaz)** — politika budur (askı = kiracıya ait iş durur, geri gelince yeni olaylar akar). `TenantReactivated` olayı (askı kalkınca Platform outbox'ına yazılır) bu yüzden bir **uzlaştırma işaretidir**:
gelecekteki tüketiciler (bildirim/faturalama/webhook) askı süresince kaçırılmış durumu bu olayda kaynağından yeniden okuyarak kapatmalıdır (kod eklenmedi; olay `TenantReactivated(TenantId, ActorUserId)` her yeniden açmada tam bir kez yazılır: `PlatformConsoleApiTests`, `PlatformAuditApiTests`).
Davet kabul/red (L7) askı/silme bekleyen kiracıya **yazmaz** (`403 tenant.suspended`); askı kalkınca davet hâlâ bekler ve kabul edilebilir.

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
`ForwardedHeaders__Enabled|KnownProxies__n|KnownNetworks__n` (varsayılan kapalı; compose yalnız `edge` alt ağını (`EDGE_SUBNET`) güvenilir sayar; nginx'e ulaşan tek ağ budur), `Docs__Enabled`, `Conductor__BaseUrl`,
`ConnectionStrings__Database|Redis`, `Auth__SigningKeyPem` (dosyadan; `/run/secrets/<Ad>` dosyaları `Ad`'daki `__` → `:` ile yapılandırma anahtarı olur), `Worker__HeartbeatFile`.
**Platform (M7):** `Platform__Signup__PlanCode` (`starter`), `Platform__Provisioning__DefaultPlanCode` (`internal`), `Platform__Plans__<n>__…` (plan kataloğu; yalnız **Migrator** için anlamlıdır — `migrate`/`sync-plans`; örnek: `appsettings.json`), `Platform__Entitlements__CacheSeconds` (30),
`Platform__Usage__{CacheSeconds 300, SnapshotPollMinutes 30, RetentionDays 400}`, `Platform__Audit__RetentionDays` (1825), `Platform__Deletion__{RetentionDays 30, MinRetentionDays 7, MaxRetentionDays 90, PollMinutes 10, MaxAttempts 10, ChunkSize 10000}`.
**Platform yöneticisi oturumu (C-SEC2 M4):** `Identity__PlatformAdminRefreshFamilyHours` (8), `Identity__PlatformAdminRefreshIdleMinutes` (60). **Migrator komutları:** `create-platform-admin`, `revoke-platform-admin` (`PLATFORM_ADMIN_EMAIL`, isteğe bağlı `PLATFORM_ADMIN_DEACTIVATE=true`), `backfill` (`is_system` işaretlemesi dahil).
Bilinmeyen plan kodu ve tutarsız aralıklar açılışta reddedilir (Migrator ≠ 0 çıkış; API/Worker başlamaz). Compose `environment` ve `.env.example` satırlarını DevOps ekler.
