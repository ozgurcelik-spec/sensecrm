# Milestone 5 — Pilot yayın (üretime hazırlık)

Hedef (PRD): pilot grup şirketi sistemi **kendi veri merkezinde** canlı kullanır. Bu milestone özellik eklemez; paketleme, güvenli varsayılanlar, işletim
araçları ve kurulum/yedek/yükseltme prosedürlerini getirir. Operasyon rehberi: [runbook.md](../operations/runbook.md); dosyalar: [`deploy/`](../../deploy).

## Yapılanlar

**İmajlar** (hepsi gerçekten derlendi; kök dizin bağlamı, `.dockerignore`): `src/Sense.Crm.Api/Dockerfile`, `src/Sense.Crm.Worker/Dockerfile`, `src/Sense.Crm.Migrator/Dockerfile` (yeni),
`web/Dockerfile` (yeni; node:24-alpine derleme → `nginxinc/nginx-unprivileged`, `web/nginx/*`). Root olmayan kullanıcılar (`app` 1654, `nginx` 101), her imajda healthcheck
(API `/health`, Worker sinyal dosyası, web `/healthz`; Migrator tek seferlik iş). Web: SPA history fallback, gzip, `/assets` 1 yıl `immutable`, `index.html`/`locales` `no-cache`,
güvenlik başlıkları + sıkı CSP (`default-src 'self'` …), `/api` ters vekil (çalışma zamanında yeniden çözümleme, gerçek istemci IP'si), HSTS yalnız `X-Forwarded-Proto: https` ile.

**Üretim compose** (`deploy/docker-compose.prod.yml`, `deploy/.env.example`): postgres 17 (ayarlı temel parametreler, volume, kaynak sınırı) → `db-init` (roller/veritabanları/izinler, idempotent) →
`migrator` (`service_completed_successfully`) → `api`/`worker` → `web`; Conductor 3.32.4 kendi DB rolü ve veritabanıyla; isteğe bağlı `redis` profili; `restart`, `healthcheck`,
kaynak sınırı yer tutucuları, log döndürme. **Yalnız `web` portu yayınlanır** (varsayılan `127.0.0.1:8080`); diğerleri `internal: true` `backend` ağında (dışarıya çıkış yok).
**Parola varsayılanı yok** (`:?`); sırlar `.env` ve Docker secret dosyalarında; API ve Worker en az yetkili `crm_app` rolüyle, migrator `crm_owner` ile bağlanır; Conductor ayrı rolle yalnız kendi veritabanında.
TLS harici (Caddy/nginx örnekleri `deploy/tls/`). `deploy/generate-secrets.ps1` (PowerShell 5.1, RSA anahtarını kendisi üretir) ve `.sh` eşi.
**Üretim başlangıç korumaları:** imza anahtarı veya bağlantı dizesi yoksa API başlamaz (`RequireConnectionString` boş şablon değerini de reddeder; testli), geçersiz `Registration:Mode` başlatmaz.

**Kayıt kontrolü:** `Registration:Mode = open|disabled` (boş: Development/Testing = açık, Production = **kapalı**); `POST /auth/signup` kapalıyken `403 auth.signup_disabled` (TR/EN; doğrulamadan önce);
`GET /auth/config → { signupEnabled }` (anonim, 60 sn önbellek); `POST /platform/organizations` (yalnız `isPlatformAdmin`, yetki veritabanından doğrulanır; `adminPassword: null` → üretilen tek seferlik parola bir kez döner;
mevcut hesapsa yalnız üyelik eklenir; yazma yeni organizasyonun kiracı kapsamında); Migrator `create-platform-admin` (ortam değişkenleri + parola dosyası; idempotent; parola asla loglanmaz).
Web: "kaydol" bağlantısı yalnız `signupEnabled` iken görünür, `/signup` kapalıyken girişe yönlendirir.

**Sertleştirme (güvenlik incelemesi bulguları):** `ForwardedHeaders:*` (varsayılan kapalı; `KnownProxies`/`KnownNetworks` yapılandırılır; hız sınırı ve refresh-token IP'si gerçek istemciyi görür), OpenAPI/Scalar yalnız
Development/Testing veya `Docs:Enabled=true`, `AllowedHosts` üretim compose'unda zorunlu ve kısıtlı, `/api/v1/auth/*` yanıtlarında `Cache-Control: no-store` (`auth/config` hariç), Worker Dockerfile `HEALTHCHECK`, nginx CSP, geliştirme
compose'unda yayınlanan portlar yalnız `127.0.0.1`'e bağlı.

**Operasyon:** `deploy/backup.sh` + `backup.ps1` (pg_dump `crm` + `conductor`, gzip, zaman damgalı, bütünlük denetimi, saklama süresi, gpg/openssl şifreleme seçeneği), `deploy/restore.md` (gerçek yığında doğrulandı: boş örneğe ve aynı örnekte),
`deploy/smoke.sh`, `deploy/docker-compose.conductor-ui.yml` (geçici Conductor arayüzü), `docs/operations/runbook.md` (kurulum, ilk yönetici/organizasyon, sırlar, yedek/geri yükleme, yükseltme + geri alma, izleme uçları, Conductor/Worker sorun giderme, hata tablosu, KVKK notları, RPO/RTO önerisi).

**CI:** `.github/workflows/ci.yml` (varsayılan olarak duraklatılmış — yalnız `workflow_dispatch`; senseik ile aynı): backend (restore, `dotnet format --verify-no-changes`, build, test), web (tsc, eslint, vitest, build), dağıtım dosyaları (compose doğrulama + sır üretici),
main'de imaj derlemeleri (`ENABLE_IMAGE_PUSH=true` değişkeniyle GHCR'ye itme).

**Testler:** `PlatformApiTests` (bootstrap, platform ucu, kiracı izolasyonu, kayıt kapalı/açık, Production başlangıç korumaları), `HostHardeningTests` (no-store, docs, forwarded headers), web `login.test.tsx` + `signup.test.tsx` (bağlantı görünürlüğü).

## Doğrulananlar (paketli yığın, ayrı `crm-m5smoke` projesi)

Migrator (owner rolü) → api/worker (`crm_app`) sağlıklı; ilk platform yöneticisi + organizasyon (`POST /platform/organizations`); web nginx üzerinden giriş → `/me` → lead oluşturma; kayıt kapalı → 403;
Conductor'da tanımlar kayıtlı ve 5 görev kuyruğu Worker tarafından poll ediliyor, lead atama workflow'u uçtan uca `completed`; dışarı çıkış yok (api/postgres'ten DNS çözülemedi), yalnız web portu yayınlı;
`crm_app` DDL yapamıyor ve `conductor` veritabanına bağlanamıyor; gerçek istemci IP'si `X-Forwarded-For` ile kaydedildi; yedek → boş örneğe geri yükleme → giriş/veri/yeni yazma/workflow çalışıyor.

## Kalan pilot kontrol listesi (kararlar ve kurumsal işler)

- [ ] **Pilot şirketi seç**, kullanıcı sayısını ve rollerini belirle (PRD açık sorusu); `resource limits` bu sayıya göre boyutlandırılsın.
- [ ] **Sunucu/VM** (4 vCPU / 8 GB / 50 GB başlangıç), Docker + Compose kurulumu, disk ve yedek hedefi (NAS/ikinci sunucu, veri merkezi içinde).
- [ ] **DNS adı** ve **TLS sertifikası** (iç CA), TLS vekili (Caddy/nginx/LB), güvenlik duvarı (yalnız vekil → `WEB_PORT`), `ALLOWED_HOSTS`/`TRUSTED_PROXY_CIDR`.
- [ ] Sırların üretimi ve **kasaya alınması** (`.env`, `secrets/`), erişimi olan kişiler listesi.
- [ ] Yedek zamanlaması (cron/Görev Zamanlayıcı) + **şifreleme** + dışa kopya; **ilk geri yükleme provası** ve süre ölçümü.
- [ ] **RPO/RTO onayı** (öneri: RPO ≤ 4 sa, RTO ≤ 4 sa; runbook §13) ve bakım penceresi/yükseltme prosedürünün kabulü.
- [ ] **SMTP/e-posta bu sürümde yoktur** (davet, parola sıfırlama, bildirim, onay e-postaları yok); pilotta kullanıcıları yönetici ekler ve parolayı güvenli kanaldan iletir — karar verin ya da e-posta altyapısını (M6) öne çekin.
- [ ] KVKK: aydınlatma metni, VERBİS kaydı, saklama/imha politikası, veri işleyen sözleşmeleri (kurumsal); yedek saklama süresinin politikayla uyumu.
- [ ] Pilot verisi içe aktarma planı (CSV içe aktarma yok — M2 kapsam dışıydı): elle giriş mi, betikle toplu yükleme mi?
- [ ] İzleme: docker healthcheck durumunu okuyacak izleme aracı, disk/RAM uyarıları, log merkezi (isteğe bağlı).
- [ ] Kullanıcı eğitimi ve destek kanalı; kabul senaryoları (lead → dönüştür → fırsat → onay) ile canlı prova.

## Açık işler (teknik)

- Parola değiştirme/sıfırlama ve platform yöneticisi parola rotasyonu (e-posta altyapısıyla; şimdilik yalnız yeni hesap/yönetici müdahalesi), MFA, SSO.
- Kalıcı silme/anonimleştirme (KVKK imha talebi), süresi dolmuş refresh token/oturum IP kayıtlarının temizliği.
- WAL arşivleme/PITR (RPO'yu dakikalara indirmek), yüksek erişilebilirlik (Postgres replikasyonu, ikinci düğüm), Kubernetes/Helm (K13).
- Gözlemlenebilirlik yığını (metrik/iz/merkezî log), `/health` uçlarının kimlik doğrulamalı dış izleme yolu.
- Conductor kimlik doğrulaması (bugün yalnız ağ izolasyonu), imaj imzalama/tarama (Trivy) ve registry'ye itme akışının açılması (`ENABLE_IMAGE_PUSH`).
- Migrator `create-platform-admin` izin kataloğu `Sense.Crm.Migrator/PlatformAdminCommand.StaticPermissionCatalog`'da elle listelenir; yeni modül eklenirse buraya da eklenmelidir (eklenmese de API açılışında sistem rolleri eşitlenir).
