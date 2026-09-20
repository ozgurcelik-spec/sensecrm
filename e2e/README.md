# CRM uçtan uca tarayıcı testleri (C-E2E)

Playwright (TypeScript) ile **gerçek, konteynerleştirilmiş yığına** (`deploy/docker-compose.prod.yml`: nginx + API + worker + Conductor + PostgreSQL) karşı gerçek Chromium'da kritik akışları sınar.
Yerelde (Windows + Docker) ve CI'da aynı betikle çalışır. Uygulama koduna dokunmaz; ürün davranışı sorunları aşağıdaki [Bulgular](#bulgular-ve-bilinen-sorunlar) bölümünde kayıtlıdır.

## Hızlı başlangıç

Gereksinimler: Docker (Compose v2), Node 24 + corepack (`corepack pnpm`), Windows'ta PowerShell 5.1, Linux/macOS'ta `bash`, `openssl`, `curl`.

```powershell
corepack pnpm install                # kökten bir kez (e2e/ pnpm çalışma alanının parçasıdır)
.\e2e\run.ps1                        # Windows PowerShell 5.1: her şeyi yapar
```
```bash
./e2e/run.sh                         # Linux / macOS / Git Bash
```

`run.ps1` / `run.sh` şunları yapar ve **hata olsa da** temizler:

1. `deploy/generate-secrets.*` ile **geçici bir dizine** (`OUT_DIR`) yeni sırlar üretir (JWT anahtarı, parolalar, platform yöneticisi parolası).
2. İmajları derler (`crm-*:e2e` etiketi; Docker katman önbelleği sayesinde tekrar çalıştırmalar ucuzdur) ve **ayrı compose projesini** başlatır: `COMPOSE_PROJECT_NAME=crm-e2e`, web `127.0.0.1:8181`,
   `Registration__Mode=disabled` (üretim gibi), ayrı alt ağlar (`10.213.177.0/24`, `10.213.178.0/24`), ayrı `pgdata` birimi.
3. `web`, `api` ve Conductor sağlıklı olana kadar bekler, `migrator create-platform-admin` çalıştırır, API konteynerinde dokümantasyon uçlarının (`/scalar`, `/openapi/v1.json`) 404 olduğunu doğrular.
4. Paketi çalıştırır (faz **main**), sonra API'yi açık kayıtla yeniden yaratıp yalnız kayıt testlerini çalıştırır (faz **registration-open**).
5. **Yalnız** `crm-e2e` projesini `down -v --remove-orphans` ile siler, geçici sırları siler, artık kalıp kalmadığını yazdırır (`leftover containers/volumes/networks of crm-e2e: 0 0 0`).
   Başarısızlıkta önce `e2e/artifacts/` altına yığın günlüklerini yazar.

`crm-prod-*`, `crm-postgres`, `senseik-*` konteynerlerine ve 8080/5080/5173/15433/13000/18080/18081/15432/16379 portlarına **hiçbir zaman dokunulmaz** (betikler bu portlarda başlamayı reddeder; her docker komutu `-p crm-e2e` taşır).

### Komutlar ve seçenekler

| Komut | Anlamı |
|---|---|
| `run.ps1` / `run.sh` (`all`) | derle → kaldır → main paketi → open-registration paketi → temizle |
| `up` | yığını kaldırıp bırakır (hata ayıklama; durum `e2e/.stack-state`) |
| `test` | çalışan yığına karşı paketi çalıştırır. Ek Playwright argümanları: `./e2e/run.sh test -- tests/auth.spec.ts -g "wrong password"`; PowerShell: `.\e2e\run.ps1 test -PwArgs 'tests/auth.spec.ts'`; kayıt fazı: `E2E_PHASE=registration-open` / `-Phase registration-open` |
| `registration open\|disabled` | API'yi verilen `Registration:Mode` ile yeniden yaratır |
| `down` | yığını ve geçici sırları siler (çöken bir çalıştırmadan sonra da güvenle çalışır) |

Ortam değişkenleri: `E2E_SKIP_BUILD=1` (derleme yapma; `E2E_IMAGE_VERSION`, varsayılan `latest` = mevcut `crm-*:latest` imajları), `E2E_WEB_PORT` (8181), `E2E_KEEP=1` (`all` sonunda yığını silme),
`E2E_SKIP_OPEN_REGISTRATION=1`, `E2E_WORKERS` (varsayılan 4, CI'da 2), `E2E_TIMEOUT_SECONDS` (sağlık bekleme, 420).

Rapor: `e2e/playwright-report/index.html` (`corepack pnpm --filter @sense-crm/e2e report`). Başarısız testlerin **video, iz (trace) ve ekran görüntüsü** `e2e/test-results/` altındadır
(`playwright show-trace <trace.zip>`). Bilinen erişilebilirlik sorunları rapordaki test notlarında (`known-issue A11Y-n`) görünür.

## Yapı

```
e2e/
  run.ps1 · run.sh            yığın düzenleyici (yukarıdaki adımlar)
  docker-compose.e2e.yml      yalnız test yığını için: anonim auth hız sınırını yükseltir (bkz. altta)
  playwright.config.ts        iki faz (main / registration-open), video+trace yalnız hatada, HTML+JUnit rapor
  support/
    api.ts                    gerçek HTTP API istemcisi (giriş, yeniden giriş, ApiError)
    seed.ts                   benzersiz veri üreticileri: createTenant (platform API ile organizasyon + yönetici), addMember, hesap/kişi/potansiyel/fırsat
    fixtures.ts               test, tenant/makeTenant/adminPage/platform fixture'ları; signIn (oturumu tarayıcıya tohumlar)
    ui.ts                     ortak yardımcılar (form girişi, bölgeler, tutar ayrıştırma)
    global-setup.ts           yığın var mı, doğru faz mı, platform yöneticisi giriş yapabiliyor mu
  tests/*.spec.ts             aşağıdaki kapsam
```

### Nasıl bağımsız ve paralel güvenli?

- Her test **kendi organizasyonunu** platform API'siyle açar (`tenant` fixture'ı); veriler benzersiz son ekle (`uid()`) adlandırılır. Paylaşılan değişken durum yoktur (platform yöneticisi yalnız okunur/oluşturur; dili başta bir kez `en` yapılır).
- Oturum, UI'da giriş formuyla değil, **API girişiyle alınan token'ların `localStorage`'a tohumlanmasıyla** açılır (sayfa açılmadan önce; hızlıdır). Girişin kendisi konu olan testler formu kullanır.
- Kilitler ve bekleyişler **web-first**'tür (`expect(...).toBeVisible()`, `expect.poll`); sabit `sleep` yoktur (ESLint `waitForTimeout`'u yasaklar). Eşzamansız sunucu durumu (onay talebi, iş akışı tamamlanması) `expect.poll` ile beklenir.
- Yeniden deneme yoktur (`retries: 0`): bir test yeniden denemeye ihtiyaç duyuyorsa sorunludur ve düzeltilmelidir.
- Tüm metinler İngilizce etiketlerle aranır (test kullanıcıları `en` yerelinde); Türkçe yalnız dil değiştirme testinde kullanılır.

## Kapsam

| Dosya | Akış |
|---|---|
| `auth.spec.ts` | giriş/çıkış, yanlış parola iletisi, zorunlu alanlar; yöneticinin yeni üye eklemesi → tek seferlik geçici parola penceresi → üyenin zorunlu parola değişimi → yeni parolayla giriş, eskisi geçersiz |
| `platform.spec.ts` | platform yöneticisi organizasyon oluşturur (UI) → yönetici ilk girişte parola değiştirmeye zorlanır; **askıya alma** → kiracıda salt-okunur bandı, yazma eylemleri yok, API reddeder → **yeniden açma** |
| `language.spec.ts` | giriş ekranı TR varsayılan, EN'e geçiş kalıcı; oturum açmış kullanıcıda TR↔EN, sunucuda kullanıcı yereli olarak saklanır |
| `crm.spec.ts` | potansiyel müşteri oluştur → hesap + kişi + fırsata dönüştür; kanban'da fırsatı gerçek fare sürüklemesiyle başka aşamaya taşı (kalıcılık dahil) |
| `commerce.spec.ts` | kalemli teklif (iskonto + KDV, canlı önizleme) → gönder → kabul → siparişe dönüştür; toplamlar önizlemede, teklifte ve siparişte kuruşu kuruşuna doğrulanır |
| `service.spec.ts` | destek talebi aç, SLA rozeti ("On track") ve hedefler, herkese açık yanıt + iç not (tür seçimi zorunlu), ilk yanıt SLA'sı |
| `campaigns.spec.ts` | kampanya oluştur, potansiyel müşteriyi üye olarak ekle |
| `workflow.spec.ts` | kural (UI) + büyük fırsat "Closed Won" → gerçek Conductor/worker ile onay talebi → onaylayıcı karar verir → yürütme tamamlanır; küçük fırsat onay üretmez |
| `plans.spec.ts` | Starter planda ücretli modüller menüde yok ve URL'de "planınıza dahil değil" sayfası, API 403; Enterprise hepsi açık; **5 kullanıcı sınırı** → 6. kullanıcıda plan-sınırı toast'ı |
| `permissions.spec.ts` | Standard kullanıcı: Workflows/SLA/Plan/Denetim/Platform sayfaları yok ve "Erişim reddedildi"; sunucu 403; yönetici karşılaştırması |
| `a11y.spec.ts` | axe (WCAG A+AA) ~35 sayfa/durum; serious/critical ihlal testi düşürür, belgeli bilinenler (A11Y-1..4) not olarak raporlanır |
| `security.spec.ts` | CSP (`default-src 'self'`, `frame-ancestors 'none'`, `script-src` inline/eval yok), nosniff, X-Frame-Options, Referrer/Permissions-Policy, COOP, sunucu sürüm sızıntısı yok, hashed varlık önbelleği; `/metrics`, `/scalar`, `/openapi`, `/health*` yalnız SPA kabuğu; `/api/...` karşılıkları 404; anonim 401; yabancı Host 400; kapalı kayıt; gerçek oturumda CSP ihlali yok |
| `registration-open.spec.ts` | (yalnız faz `registration-open`) kayıt bağlantısı, kendi kendine organizasyon oluşturma, zayıf parola reddi |

## Test yığını neden üretimle aynı değil? (tek fark)

`docker-compose.e2e.yml`, API için yalnızca `RateLimiting__Auth__PermitLimit` ve `RateLimiting__LoginEmail__PermitLimit` değerlerini 2000'e çıkarır. Üretimde IP başına dakikada 20 anonim auth çağrısı sınırı
vardır; tüm test işçileri aynı istemci adresinden (docker geçidi) geldiği ve her test kendi kullanıcısıyla giriş yaptığı için sınır paketin kendisini kısıtlardı. Başka hiçbir ayar farklı değildir
(`Production` ortamı, dokümantasyon kapalı, kayıt kapalı, `internal: true` arka uç ağı, root olmayan kullanıcılar, nginx başlıkları).

## CI

`.github/workflows/ci.yml` içindeki `e2e` işi: kilitli kurulum → `tsc --noEmit` → `eslint` → `playwright install --with-deps chromium` → `bash e2e/run.sh` → `e2e-report` artifact'ı
(HTML rapor, trace/video, yığın günlükleri; hata olsa da yüklenir). Şu an CI otomatik tetiklenmiyor (yalnız `workflow_dispatch`), iş yine de doğrulanmıştır (`actionlint`).

## Sorun giderme

- **Port 8181 dolu:** başka bir şey kullanıyor; `E2E_WEB_PORT=8182` verin (ayrılmış portlar reddedilir).
- **`Pool overlaps` / alt ağ çakışması:** `E2E_BACKEND_SUBNET` / `E2E_FRONTEND_SUBNET` ile başka bir `/24` seçin (varsayılanlar `crm-prod` ağlarından farklıdır).
- **Çöken çalıştırma sonrası artıklar:** `.\e2e\run.ps1 down` — durum dosyası olsa da olmasa da yalnız `crm-e2e` etiketli kaynakları siler.
- **Tek bir testi izlemek:** `./e2e/run.sh up` → `./e2e/run.sh test -- tests/crm.spec.ts --headed` → `./e2e/run.sh down`.
- **Yığın günlükleri:** başarısızlıkta `e2e/artifacts/stack.log`; canlı yığında `docker compose -p crm-e2e logs api worker`.
- Yeni test yazarken: rol/etiket/metin konum belirleyicilerini tercih edin; yalnız kararlı bir belirleyici mümkün değilse `data-testid` ekleyin (yalnız ekleme, küçük değişiklik).

## Bulgular ve bilinen sorunlar

Test sırasında görülen ve uygulama davranışı olduğu için **düzeltilmemiş** durumlar (`web/**` ve arka uç kodunda hiçbir değişiklik yapılmadı). Hiçbiri testte gizlenmedi: ya etkisi testte kaçınıldı (nedeniyle) ya da bilinen sorun olarak işaretlendi.

| Id | Alan | Önem | Durum |
|---|---|---|---|
| F-1 | Platform/Identity: organizasyon oluşturma yarışı | Orta | Açık; testte tetiklenmiyor |
| F-2 | Identity: bootstrap platform yöneticisinin rolü | Düşük | Açık |
| F-3 | Erişilebilirlik (axe) A11Y-1..4 | Orta | Bilinen sorun, `a11y.spec.ts` içinde belgeli |
| F-4 | Conductor hazır olmadan iş akışı | Orta | Açık; betikler sağlıklı olmasını bekler |
| F-5 | nginx: `/metrics`, `/scalar`, `/openapi`, `/health*` 200 (SPA kabuğu) | Bilgi | Bilinçli davranış |
| F-6 | Standard rolü Roller sayfasını salt okunur görür | Bilgi | Ürün kararı gerekir |
| F-7 | `untrusted_task` geçici uyarısı | Bilgi | Kendiliğinden düzeliyor |

**F-1 — `POST /platform/organizations` `planCode`'u hemen uygulamıyor (yarış).** `planCode: "enterprise"` ile açılan organizasyonun yöneticisi hemen giriş yapıp `/me` çağırırsa plan `starter` (deneme)
görünür ve ticaret/servis/pazarlama/iş akışı modülleri kapalıdır; ~30 sn sonra `enterprise` olur. Hesap kaydı outbox olayıyla (`OrganizationCreatedAccountHandler`) eşzamansız oluşur (normalde 1-2 sn; öncesinde
`GET /platform/organizations/{id}` 404). Yönetici bu aradan önce çağrı yaparsa kiracı varsayılan `starter` planıyla "lazy" açılır; işleyici aynı kaydı eklemeye çalışıp `DbUpdateException` ile düşer
(worker: `OrganizationCreatedAccountHandler failed ... retrying in 10s`); yetkilendirme önbelleği (`Platform__Entitlements__CacheSeconds` = 30) eski planı en fazla 30 sn daha gösterir; aradaki
`PUT .../subscription` `409 general.concurrency_conflict` verebilir. Yeniden üretme: organizasyonu `enterprise` ile aç → hemen yönetici parolasını değiştir → `GET /me` → `starter`.
Etki: müşteri ilk yarım dakikada modülsüz görünebilir + günlük gürültüsü. Testte: `support/seed.ts` `createTenant`, ilk kiracı çağrısından **önce** platform kaydının görünmesini bekler.
Öneri: işleyiciyi idempotent yapın (mevcut lazy kaydı istenen planla güncelleyin) veya hesap kaydını organizasyonla aynı işlemde yazın.

**F-2 — Bootstrap platform yöneticisinin `Administrator` rolü yeni modül izinlerini eksik alıyor.** `create-platform-admin`, API'nin `SystemRolePermissionSynchronizer`'ından sonra çalıştığı için işletim organizasyonunun
rolü ilk kurulumda yalnız 18 çekirdek izin taşır (`crm.campaigns/cases/orders/products/quotes.*` yok). Konsol için yeterli; bu organizasyonda CRM verisi tutulacaksa API yeniden başlayana kadar bu modüller görünmez
(sonraki başlangıçta eşitlenmesi ayrıca doğrulanmalı). Testte platform yöneticisi yalnız `/app/platform/**` için kullanılır.

**F-3 — Erişilebilirlik.** `A11Y-1` `color-contrast` (serious): Mantine `dimmed` (#868e96) beyaz/gri zeminde 3,15-3,32:1, AA 4,5:1 ister; açık yeşil rozet 3,8:1 (tüm uygulama; tema belirteci).
`A11Y-2` `aria-allowed-attr` (critical) `/app/settings/plan`: kullanım çubuklarında `aria-valuetext` var, `role="progressbar"` yok. `A11Y-3` `button-name` (critical) `/app/settings/audit`: devre dışı
"ilk/önceki" sayfalama düğmelerinin adı yok. `A11Y-4` `button-name` (critical) her modal: kapatma (X) düğmesinin adı yok (`closeButtonProps={{ "aria-label": ... }}` eksik).

**F-4 — Conductor hazır olmadan iş akışı `workflow.engine_unavailable` ile kalıcı düşüyor.** `api`/`worker` → `conductor: condition: service_started` (sağlık `start_period: 60s`). Kurulumdan hemen sonra tetiklenen
iş akışı (büyük fırsat kazanıldı) `Failed / workflow.engine_unavailable` olur ve otomatik yeniden denenmez. Testte: betikler Conductor `healthy` olana kadar bekler. Öneri: `service_healthy` veya yürütmeyi motor
erişilebilir olana dek yeniden deneme.

**F-5 — Belgeleme/metrik yolları nginx üzerinden 200.** nginx bilinmeyen yolları SPA'ya düşürdüğünden `/metrics`, `/scalar`, `/openapi`, `/health*` `200 text/html` (uygulama kabuğu) döner; gerçek uçlar erişilemez
(API konteynerinde `/scalar` ve `/openapi/v1.json` 404, betik doğrudan doğrular). Zafiyet değil; otomatik tarayıcılar 200 görebilir. Testler kabuğu ve metrik/OpenAPI içeriğinin olmadığını doğrular.

**F-6 — "Standard kullanıcı Roller sayfasını göremez" beklentisi.** `Standard` rolü `org.users.read` ("Kullanıcıları ve rolleri görüntüle") taşır; Kullanıcılar ve Roller sayfalarını **salt okunur** görür (oluşturma/düzenleme
yok, sunucu yazmaları 403). Workflows, SLA, Plan ve kullanım, Denetim Kaydı ve Platform gerçekten kapalıdır. `permissions.spec.ts` gerçek davranışı sınar; Roller'in da kapatılması isteniyorsa ürün kararı ve izin eşlemesi gerekir.

**F-7 — `untrusted_task`.** Worker günlüğünde nadiren `Workflow task crm_create_approvals ... rejected ... untrusted_task` (motor kimliği yazılmadan görev alınması; koda göre geçici hata). Sonraki denemede ilerliyor.

Ek gözlemler: askıya alınan organizasyonda yazma düğmeleri devre dışı değil **hiç çizilmiyor** (sunucu yine reddeder); fırsat sahibi kendi fırsatının onaylayıcısı olamaz (`no_approver`) — onay testi ikinci bir Administrator kullanır.
