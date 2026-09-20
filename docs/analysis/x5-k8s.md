# C-X5 — Kubernetes / Helm dağıtımı analizi

Tarih: 2026-09-20 · Yazar: Spec (analist; kod yazmaz) · Durum: **karar önerisi** (Product Owner onayı bekliyor) · Kapsam: K13 ("üretimde Kubernetes/Helm hedefi") ile K17 (bugünkü Compose pilotu) arasındaki boşluğun ölçülmesi.

Kanıt etiketleri: **[R]** depoda okundu (dosya belirtilir) · **[W]** web araştırması, 2026-09-20 (kaynak §9) · **[?]** doğrulanmadı / bilgi tabanlı, spike ile teyit edilmeli.

---

## 0. Özet (5 satır)

1. **Kubernetes bugün her müşteri için gerekçeli değil.** Tek şirket / ≤ 50 eşzamanlı kullanıcı / RTO 4 sa kabul edilen kurulumda sertleştirilmiş Compose daha ucuz, air-gap'te daha kolay ve ekip becerisine daha uygun (§1). Kubernetes; RTO/RPO dakika mertebesi, müşteride hazır platform ekibi + küme, ya da çok müşterili işletim olduğunda gerekçelidir.
2. **İşin ~%70'i orkestratörden bağımsızdır** ve "iki `api`/`worker` kopyası" isteyen her yol (Compose `--scale` dâhil) için gerekir: paylaşımlı azaltma sayaçları, Conductor kilidi, Worker rolleri/kilit hijyeni, expand/contract migration, MinIO'nun yerine geçecek S3 (§2). Bunlar önce yapılmalı; Kubernetes kararı bunlardan sonra, keşif (spike) çıktısına göre verilir.
3. **Paketleme önerisi: tek birinci-taraf Helm chart (seçenek B)**, GitOps uyumlu (Argo/Flux'a ürün olarak bağımlı değil), operatör yok; Postgres ve S3 chart'a gömülmez (harici/müşteri sağlar; CloudNativePG yalnız referans mimari). Compose "S profili" olarak desteklenmeye devam eder (§3).
4. **Yeni, acil bulgu (K8s'ten bağımsız):** MinIO topluluk deposu 2026-04-25'te arşivlendi, imaj/ikili dağıtımı durdu [W]; K21 ve runbook §18 "bakım modu" diyor — güncel değil. Sabitlenmiş imaj etiketi registry'den kalkabilir; ayna (mirror) ve halef S3 kararı gerekir (§2.11, PO-5).
5. **Kabaca 23–33 mühendis-haftası**, 4 aşama (§5); ilk kapı (Aşama 0+1, ≈ 6–9 hf) hem Compose hem Kubernetes'e fayda sağlar, chart yatırımı ondan sonra kararlaştırılır.

---

## 1. Kubernetes gerçekten gerekli mi? (dürüst değerlendirme)

### 1.1 Bugünkü durum [R]
- Üretim = tek sunucuda Compose (`deploy/docker-compose.prod.yml`): 1 `api`, 1 `worker`, 1 `conductor`, 1 `postgres`, `web`, isteğe bağlı `redis`, `minio`; tek noktada arıza = RTO süresi kadar kesinti (runbook §13). Hedef RPO/RTO ≤ 4 sa / ≤ 4 sa **hâlâ "onay bekliyor"** (K17).
- Compose dosyası kendi sınırını söylüyor: worker "iş işleyicileri yük testinden geçene kadar 1 kopya"; runbook §9.1 "api'yi çoğaltmadan önce (K13/Kubernetes) ele alın" diyor.
- K3 tasarım hedefi 1 000 kiracı / 10 000 kullanıcı; bu hedefe dair yük testi kanıtı depoda yok [R: bulunmadı]. Boyutlandırma tahminleri bu belgede **[?]**.

### 1.2 Müşteri profilleri ve gereken kopya sayısı

| Profil | Tipik müşteri | Kullanıcı | RTO / RPO beklentisi | Asgari topoloji (uygulama) | Kubernetes gerekçesi |
|---|---|---|---|---|---|
| **S** | Tek şirket, pilot, kurum içi | ≤ 50 eşzamanlı | 4 sa / 4 sa (K17 önerisi) | 1×api, 1×worker, 1×web, 1×conductor, Postgres tek | **Yok.** Compose + iyi yedek yeterli |
| **M** | Holding / grup şirketleri | 200–1 000 | 30–60 dk / ≤ 5 dk | 2×api, 2×web, worker rol başına 2 (bekleme), Conductor 1 (hızlı yeniden başlama), Postgres 1+1–2 replika + PITR, Redis, S3 | **Koşullu:** müşteride hazır platform varsa evet; yoksa 2 VM Compose + PG replikası da mümkün |
| **L** | Bizim işlettiğimiz çok kiracılı SaaS (K3) | 10 000 / 1 000 kiracı | ≤ 15 dk / ≤ 1 dk | 3+×api, 2+×worker/rol, Conductor 2 + Redis kilidi, Postgres 3 düğüm, dağıtık S3 | **Evet** (kendi operasyonumuz, otomatik dağıtım/ölçek) |

Kopya sayısı gerçeği: outbox ve zamanlayıcılar düşük hacimlidir (CRM), Worker kopyası **ölçek için değil yedeklilik için** 2'dir; API 2–4; yani "yüzlerce pod" senaryosu yoktur. Kubernetes'in ölçekleme üstünlüğü bu ürün için ikincildir; asıl kazanç **kendi kendini onarma, sıralı güncelleme, ağ politikası ve tek deklaratif paket**tir.

### 1.3 Orkestratör karar matrisi (1 = zayıf, 5 = güçlü)

| Kriter | A0 Compose, tek VM (sertleştirilmiş) | A1 Compose, 2 VM (aktif/pasif + PG replikası + keepalived) | Docker Swarm | Nomad | systemd / Podman Quadlet | Kubernetes (Helm) |
|---|---|---|---|---|---|---|
| RTO/RPO ulaşılabilir | 2 (saatler) | 3 (dk–saat, elle geçiş) | 3 | 4 | 2 | 5 (otomatik) |
| Operasyon karmaşıklığı (düşük = iyi) | 5 | 3 | 3 | 3 | 4 | 2 |
| Air-gap kurulum kolaylığı | 5 | 4 | 4 | 3 | 5 | 3 (imaj listesi + registry + araç zinciri) |
| Müşteri ekip becerisi (Türkiye kurumları; [?]) | 4 | 3 | 2 | 1 | 3 | 4 (OpenShift/RKE2 yaygın [W]) |
| Ağ yalıtımı (C-SEC2 eşdeğeri) | 4 (`internal` ağ, yerleşik) | 4 | 3 | 3 | 2 | 4 (NetworkPolicy; **CNI'ye bağlı**) |
| Ürün ekibi bakım yükü | 5 (mevcut) | 3 | 2 | 2 | 3 | 2 (chart + CI + destek matrisi) |
| Ekosistem / gelecek güvencesi | 4 | 4 | 2 — bakım modunda, Mirantis 2028+ [W] | 2 — BUSL 1.1 [W] | 3 | 5 |
| Mimari resimle uyum ("Konteyner Docker/Kubernetes", "IaC/Helm", Worker "yatayda ölçeklenebilir") | 3 | 3 | 2 | 2 | 1 | 5 |

**Yorum (dürüst):**
- **S profilinde A0 kazanır**; Kubernetes eklemek karmaşıklığı artırır, hiçbir kabul kriterini iyileştirmez.
- Swarm ve Nomad elenir: Swarm "bakım modunda, yol haritası yok" [W]; Nomad BUSL ve müşteri becerisi zayıf. Podman/systemd yalnız Docker yasak müşteri için not düşülür.
- A1, M profilinde Kubernetes'in **gerçek rakibidir**: Postgres HA (Patroni ya da replika + elle terfi) ve Conductor kilidi sorunu iki yolda da aynıdır; Kubernetes bunları çözmez, yalnızca pod yeniden başlatmayı ve dağıtımı otomatikleştirir.
- **Kubernetes'i gerekçelendiren tetikleyiciler (en az biri):** (1) sözleşmede RTO < 1 sa ve RPO < 15 dk; (2) müşterinin hazır platform ekibi/küme(si) var ve "uygulamayı bizim kümemize kurun" şartı koşuyor; (3) bizim çok müşterili SaaS işletimimiz (L); (4) müşteri politikası doğrudan Docker host erişimini yasaklıyor.
- **Gerekçelendirmeyen:** tek sunucu, küçük ekip, tam air-gap ve müşteri tarafında Kubernetes becerisi yok.

**Sonuç:** Kubernetes "hedef ama koşullu" kalmalı; K13'ün "üretimde Kubernetes" ifadesi "müşteri gerektirdiğinde, S profili Compose" olarak yumuşatılmalı (PO-1).

---

## 2. Uygulamada gereken delta (çok kopyalı API/Worker için)

Boyut: S ≤ 2 gün, M ≈ 1 hf, L > 1 hf. Kart kodları §5'tedir.

### 2.1 Sağlık uçları ve probe'lar
| Bileşen | Bugün [R] | Kubernetes'te | Delta |
|---|---|---|---|
| API | `/health/live` (bağımlılıksız), `/health/ready` (Npgsql), `/health` tam rapor; Redis/depo hazırlığa girmez | `startupProbe` (30 sn+), `readinessProbe`=ready, `livenessProbe`=live (httpGet). Dockerfile'daki `apt-get install curl` gereksiz olur → sil (saldırı yüzeyi) | S. Ready DB'ye bağlı: DB failover'da tüm API pod'ları NotReady olur; `failureThreshold` 3, `period` 10 sn |
| Worker | HTTP ucu yok; 15 sn'de `/tmp/crm-worker.heartbeat`, konteyner `HEALTHCHECK` | Exec probe çalışır ama kabuk/`stat`/`date` ister (Chiseled imajı engeller); rol başına ayrı Deployment'ta HTTP daha temiz | **M:** Worker'a küçük `/health` dinleyicisi (metrik dinleyicisi 9465 zaten var; ayrı `Worker:Health` portu). Heartbeat'i "son başarılı poll turu" ile bağla |
| Web | `/healthz` (nginx) | httpGet | S |

### 2.2 Zarif kapanma
- `BackgroundService` döngüleri `stoppingToken` dinliyor [R] (outbox, Conductor, status-sync); yürütülen iş tamamlanıp döngü çıkar. Conductor görevi işlenmiş ama raporlanmamışsa görev yanıt zaman aşımında yeniden kuyruğa girer (at-least-once, işleyiciler idempotent) [R: `ConductorTaskPollingService` yorumu].
- Generic Host `ShutdownTimeout` varsayılanı 30 sn [?]; Kubernetes `terminationGracePeriodSeconds` da 30. Öneri: API 60–90 sn (25 MB yükleme akışı), Worker 60 sn; `preStop: sleep 5–10` (endpoint yayılımı, 502'yi önler).
- `ApiKeyUsageBuffer` bellek içi sayaç toplu upsert ediliyor; **kapanışta son boşaltma var mı doğrulanmadı [?]** (yoksa kayıp ≤ bir aralık; kabul edilebilir ama belgelenmeli). **S.**

### 2.3 Yapılandırma ve sırlar
- API `AddKeyPerFile("/run/secrets")`, Worker/Migrator `AddDockerSecrets()` [R]: dosya adı `Ad__Alt` → `Ad:Alt`. Kubernetes Secret'ı `/run/secrets` altına **aynı anahtar adlarıyla** monte etmek kod değişikliği istemez. İki tuzak: (a) K8s Secret birimi `..data` sembolik bağlantıları kullanır → `AddDockerSecrets`'ın sembolik bağlantı/`..` önekli dosya davranışı **doğrulanmadı [?]** (spike S6); (b) değişiklik çalışan süreçte okunmaz → `rollout restart` (Helm sağlama-toplamı ek açıklaması yalnız chart'ın yönettiği Secret için işe yarar).
- Bağlantı dizesi parolayı içinde taşıyor (`ConnectionStrings__Database=Host=...;Password=...`). Kod değişmeden çözüm: parolayı `secretKeyRef` ile ortam değişkenine, dizeyi `$(CRM_APP_PASSWORD)` bağımlı ortam değişkeniyle kur. Kalıcı çözüm (S): `..._FILE`/parola-dosyası desteği.
- Zamanlar: `TZ=Europe/Istanbul` ortamı; `/tmp` tmpfs (upload) → `emptyDir{medium: Memory, sizeLimit}` **bellek sınırına sayılır** (512 Mi tmp + 1 Gi limit hesabı).

### 2.4 Arka plan servisleri: kilit / lider seçimi incelemesi (Worker'ın tamamı)
Sonuç: **Worker büyük ölçüde N kopyaya dayanıklı tasarlanmış** [R]; kalan boşluklar aşağıda **kalın**.

| Servis (dosya) | Mekanizma [R] | N kopyada | Gerekli iş |
|---|---|---|---|
| `OutboxPollingService<T>` × ~10 bağlam (`Worker/Program.cs`, `OutboxProcessor`) | `FOR UPDATE SKIP LOCKED`, batch 50, poll 2 sn | Güvenli (çift işleme yok). **Sıra garantisi kalkar:** tek kopyada bir bağlam sıralı işlenir; N kopyada aynı kümenin olayları (ör. aynı fırsatın `DealStageChanged` dizisi) farklı kopyalarda eşzamanlı/ters sırada işlenebilir | **Sıra duyarlılığı denetimi (M):** olay tüketicileri (Workflows tetikleyici, Marketing `LeadConverted`, Platform `OrganizationCreated`) sırasızlığa dayanıklı mı? Aksi hâlde outbox rolünü 1 aktif + 1 bekleyen yap (advisory lider) |
| `ConductorTaskPollingService` | Conductor poll atomik; her pod sıralı görev; `WorkerId` sabit `crm-worker` | Güvenli; Conductor görevi tek pod'a teslim eder | `Conductor__WorkerId` = pod adı (günlük/iz için; Conductor'ın `workerId` doğrulaması yapıp yapmadığı **[?]**) |
| `ExecutionStatusSyncService` (`ExecutionSyncRunner`) | **Kilit yok**; her kopya tüm `running` yürütmeleri Conductor'dan çeker | N× Conductor GET; eşzamanlı yazma çakışması olası (idempotentliği incelenmedi **[?]**) | **Advisory kilit** (`LockedJobService`) ya da `SKIP LOCKED` parçalama (S–M) |
| `WorkflowDefinitionRegistrationService` | Açılışta workflow tanımlarını Conductor'a kaydeder | N pod aynı anda kayıt → yarış (idempotent olduğu **[?]**) | Migrator'a taşı (`register-workflows` adımı) veya kilitle (S) |
| `UsageSnapshotService` (7_302_001), `TenantErasureService` (7_302_002), `FilesPurgeService` (7_303_001), `FilesReconciliationService` (7_303_002) | Oturum düzeyi `pg_try_advisory_lock`; tek örnek | Güvenli | **Kilit anahtarı kaydı:** M8A planı Activities işleri için `7_303_001..005` ayırmış; **Files işleri aynı anahtarları zaten kullanıyor** (çakışma; biri diğerini atlatır). Merkezî `AdvisoryLockKeys` + mimari test "anahtarlar benzersiz" (S) |
| `WebhookDispatcherService` | `SKIP LOCKED` + `locked_until` kirası; **kiracı/host eşzamanlılık ve dakika kotaları bellek içi** | Teslimat güvenli; kotalar kopya sayısıyla çarpılır | Kabul (≤ 2 kopya) ya da DB sayaç (M). Yalnız `integrations` rolü egress alır |
| `IntegrationsRetentionService` | **Kilit yok**, 24 sa | N kopya = N kez silme (idempotent, zararsız, gereksiz yük) | `LockedJobService` (S) |
| `HeartbeatService`, `WorkerMetricsSampler` | Pod başı | Güvenli. Örnekleyici her kopyada DB sorgular; alarmlar `max by (module)` kullanıyor (K20) → çok kopyada doğru [R: `crm-alerts.yml`] | — |
| Tohumlayıcılar (Sales/Service), limit/sıra (Commerce, Marketing, Platform) | `pg_advisory_xact_lock` işlem ömürlü | Güvenli | — |
| **M8A (planlı, henüz birleşmedi):** `NotificationDeliveryService` (SKIP LOCKED), zamanlayıcılar (`LockedJobService`) | N kopyaya uygun tasarlanmış | Güvenli | Anahtar çakışması (yukarıda) |

Ek riskler:
- **Oturum düzeyi advisory kilit + bağlantı havuzlayıcı:** PgBouncer/CNPG `Pooler` *transaction* kipinde oturum kilidi bozulur → Worker'ı doğrudan Postgres'e ya da *session* kipine bağla (S, belge). Bağlantı, iş sürerken kopsa kilit sessizce düşer ve iki kopya aynı işi yapar; işler idempotent (plan D10) olduğu için kabul, belgelenmeli.
- **Bağlantı bütçesi:** Npgsql varsayılan `Maximum Pool Size=100`; 3 API + 4 Worker pod × 100 > `max_connections=200`. Bağlantı dizesine pod başı `Maximum Pool Size` (S).
- **Rol bölme:** `Worker:Roles` kodda **yok**; M8A D4 planlıyor (`outbox,workflows,platform,scheduler,notifications`). Kubernetes'te rol başına Deployment (ve NetworkPolicy) bu kartın birleşmesine bağlıdır. Öneri: rol listesine `integrations` (webhook + saklama) ve `files` ekle; **egress yalnız `integrations` ve `notifications` pod'larına** verilir (§4.1).

### 2.5 Migrator: Job / kanca ve sıralama
- Compose sırası: `postgres → db-init (roller, DB'ler; süper kullanıcı) → migrator (çıkış 0) → api, worker → web` [R]. Helm karşılığı: `pre-install,pre-upgrade` kanca Job'ları (db-init ağırlık −10, migrator −5, `backoffLimit: 0`, `activeDeadlineSeconds`, `ttlSecondsAfterFinished`); başarısız kanca yükseltmeyi durdurur (istenen davranış).
- CNPG kullanılırsa roller `managed.roles` + `postInitApplicationSQL` ile deklaratif; db-init Job (idempotent) her hâlükârda yeniden kullanılabilir.
- **Kritik: sıralı güncellemede eski API pod'ları yeni şemayı görür.** Compose'ta durdur-başlat bunu gizliyordu. Zorunlu hâle gelecek kural: **expand/contract** (bir sürümde ekle, sonraki sürümde sil; sütun silme/yeniden adlandırma tek sürümde yasak). Kural olmadan `strategy: Recreate` (saniyelerce kesinti) kullanılır. CI'da EF migration'larında yıkıcı işlem taraması (M).
- Alt komutlar Job olarak: `create-platform-admin` (bir kerelik, parola Secret'ı sonra boşaltılır), `sync-plans` (her yükseltmede; plan kataloğu ConfigMap'ten), `files-reconcile`, `erase-deleted-tenants` (geri yükleme sonrası).

### 2.6 HybridCache L2 (Redis) ve önbellek tutarlılığı
- Redis yoksa yalnız bellek içi (izin süresi 2 dk) [R: `InfrastructureServiceCollectionExtensions`]. **>1 API kopyasında Redis fiilen zorunlu** (yoksa her kopya kendi önbelleğini tutar, kiracı/askı/izin bayatlığı 2 dk'ya çıkar).
- Redis ile bile **L1 (30 sn) diğer kopyalarda geçersiz kılınmaz**: HybridCache'in çapraz-düğüm L1 geçersiz kılma (backplane) mekanizması yok [W: dotnet/runtime#125602, dotnet/extensions#7098]. Yani runbook §9.1'in "≤ 30 sn + L2 süresi" bayatlık sınırı doğru ama **askı/izin iptali tüm kopyalarda ≤ 30 sn** demektir.
- Seçenekler: (a) kabul + `LocalExpirationSeconds` 10 sn (çok kopya profili); (b) Redis pub/sub ile küçük özel geçersiz kılma yayını (M); (c) FusionCache (yerleşik backplane; kütüphane değişimi, L). **Öneri: (a) şimdi, (b) gerekirse** (PO-7).
- Redis'i **iki ayrı örnek** yap: önbellek (`allkeys-lru`, kalıcılık yok) ve Conductor kilidi (§2.10; `noeviction`) — LRU tahliyesi kilit anahtarını silebilir.

### 2.7 Hız sınırı ve azaltma sayaçları (bellek içi)
| Sayaç [R] | Bugün | Çok kopyada | Öneri |
|---|---|---|---|
| Genel sınırlayıcı (`UserPermit 600/dk`, `Tenant 3000/dk`, API anahtarı 120/600) — ASP.NET `RateLimiter` sabit pencere | Kopya başına | Etkin sınır ≈ N× | Kopya başına sınırı N'ye böl (yapılandırma; cömert varsayılanlar) — **kabul** |
| Anonim auth IP sınırı | Kopya başına | N× | Ingress/WAF'ta da IP sınırı; kabul |
| **`LoginThrottle`** (IP+hesap 5 hata/15 dk, e-posta 20/dk) `ConcurrentDictionary` | Bellek içi | Saldırgan sınırı N× aşar. **Runbook "paylaşımlı depo (Redis) gerekir" diyor ama kodda Redis destekli uygulama yok** | **Redis (INCR+EXPIRE) tabanlı `ILoginThrottle` (M);** çok kopyada Redis yoksa başlangıçta reddet |
| API anahtarı doğrulama hata azaltması | Bellek içi | N× | Aynı Redis deposu (M'ye dahil) |
| `FilesRateLimiter` eşzamanlı yükleme | Bellek içi | N× (`/tmp` boyutuna da yansır) | Kabul; `emptyDir` boyutunu N'den bağımsız kopya başına hesapla |
| Webhook kotaları | Bellek içi | N× | §2.4 |
Kalıcı geri dayanak: hesap kilidi DB'de (10 hata → 15 dk) [R: hardening-report].

### 2.8 JWT anahtar dağıtımı
- RS256, özel anahtar yalnız API'ye Secret olarak monte edilir (Compose ile aynı: Worker'a verilmez) [R]; `kid` = parmak izi (deterministik) → tüm kopyalar aynı anahtarla imzalar/doğrular. ASP.NET Data Protection kullanılmıyor [R: arama sonucu yok] → anahtar halkası paylaşımı gerekmez.
- Anahtar döndürme: tek anahtar → sıralı güncellemede eski/yeni pod'lar birbirinin token'ını reddeder (401 dalgası ≤ 15 dk). Çözüm: `Auth:ValidationKeysPem` listesi (eski anahtar yalnız doğrulama) (S–M). Refresh token'lar DB'de opak olduğundan kullanıcılar yeniden girişe zorlanmaz.

### 2.9 nginx (web) ve statik varlıklar
- Varlıklar imajın içinde; N kopya sorunsuz. Değişecekler [R: `web/nginx/default.conf.template`, `web/Dockerfile`]:
  - `DNS_RESOLVER` varsayılanı `127.0.0.11` (Docker) → küme DNS'i (`kube-dns`; OpenShift `dns-default`); değer chart'tan/`resolv.conf`'tan.
  - `API_UPSTREAM=http://<release>-api:8080` (Service adı), 10 sn yeniden çözme sürüyor → ClusterIP ile uyumlu.
  - `TRUSTED_PROXY_CIDR` = Ingress/LB kaynak aralığı; **iki sıçrama** oluşur (LB → ingress → web nginx → api): `set_real_ip_from` Ingress pod aralığını kapsamalı ya da PROXY protokolü; `externalTrafficPolicy: Local`.
  - API'de `ForwardedHeaders__KnownNetworks` (bugün backend alt ağı) → web pod'larının aralığı (Pod CIDR); NetworkPolicy yalnız web'in API'ye ulaşmasına izin verdiği için güvenli.
  - Gövde limiti: `client_max_body_size 20m` + Ingress/Gateway gövde sınırı aynı değerde.
  - Ingress: **ingress-nginx emekli oldu (Mart 2026, güvenlik yaması yok)** [W]; chart standart `Ingress` **ve** Gateway API `HTTPRoute` (opsiyonel) ve OpenShift `Route` üretebilmeli; hiçbir denetleyici-özel not (annotation) zorunlu olmamalı. TLS: cert-manager veya müşteri PKI; `deploy/tls/*` örnekleri Ingress'e karşılık gelir.

### 2.10 Conductor: HA ve kalıcılık
- Bugün: tek örnek, tüm durum Postgres `conductor` DB'sinde [R]; yeniden başlatma iş kaybettirmez.
- **Çok örnek şartı: dağıtık kilit** (`conductor.app.workflowExecutionLockEnabled=true`); resmî belge Redis/Zookeeper sayıyor, `local_only` "çok örnekte güvenli değil" [W: conductor-oss deploy]. `postgres` kilit tipi de var ama **SUB_WORKFLOW tamamlanınca iş akışı kalıcı takılıyor (issue #1058, 2026-04-30)** [W]. Mevcut CRM tanımları yalnız `SIMPLE` + `HUMAN` görev kullanıyor [R: `crm_*.v1.json`]; **X1 (BPMN stüdyosu) SUB_WORKFLOW getirirse bu kısıt işe bulaşır.**
- Öneri: **M profilinde Conductor 1 kopya** (`Recreate`, pod yeniden başlatma ≈ 1–2 dk; API/Worker Conductor'a ulaşamazsa geri çekilip yeniden dener [R: 5 sn `ErrorDelay`]; `CrmConductorUnreachable` alarmı var); **L profilinde 2 kopya + ayrı `noeviction` Redis kilidi**. Postgres'te Conductor performansı "büyük hacimde iyi ölçeklenmiyor" [W]. Conductor imajının root olmayan çalışması **[?]** (spike S5).
- `infra/conductor/config-postgres.properties` → ConfigMap; parola `CONDUCTOR_DB_PASSWORD` Secret'tan.

### 2.11 Durumlu bileşenler: küme içi mi, dışarıda mı?
| Bileşen | Öneri | Gerekçe |
|---|---|---|
| **PostgreSQL** | **Harici / müşteri DBA'sı birinci sınıf; chart'a gömülmez.** Küme içi isteyenlere **CloudNativePG** referans mimarisi (örnek `Cluster` + `ScheduledBackup` + `Pooler`, chart dışı). | Türkiye kurumlarında DB genellikle ayrı DBA/VM işi **[?]**. CNPG dikkate değer: CNCF Sandbox, 1.29 hattı, PITR + nesne deposuna sürekli yedek, kuantum senkron replikasyon [W]; ancak operatör de yama ister (2026'da 1.29.1/1.28.3 kritik CVE-2026-44477 düzeltmesi [W]). Zalando/Crunchy **incelenmedi [?]**. Oturum kilitleri için havuzlayıcı kipi (§2.4) |
| **Nesne deposu** | **Harici S3 uyumlu (müşteri) öncelikli.** MinIO **gömülmez.** | **MinIO topluluk deposu 2026-04-25'te arşivlendi, README "artık bakımsız", yalnız kaynak kod dağıtımı; Docker/Quay imajları Ekim 2025'te durdu** [W: minio/minio README (2026-09-20 çekildi) + üçüncü taraf özetler]. K21'in "bakım modu, elle etiket yükseltmesi" dayanağı eskidir. Adaylar: Ceph RGW (Rook; kurumsal, ağır), SeaweedFS (Apache-2.0), Garage, RustFS (genç) [W: karşılaştırma blogları; olgunluk **[?]**]. **Kritik uyum sorusu:** uygulama kova varsayılan şifrelemeyi (SSE-S3) doğruluyor (`Files:Storage:Encryption=required`, statik KMS anahtarı) [R] → halef bunu desteklemek zorunda ya da `AcknowledgeUnencrypted` + disk şifreleme kararı gerekir (spike S4) |
| **Redis** | Küme içi, basit Deployment (önbellek, kalıcılık yok) + ayrı kilit örneği (yalnız Conductor HA). | Bitnami chart/imaj kataloğu 2025'te kapatıldı/ücretliye taşındı [W] → **Bitnami'ye bağımlı olma**, kendi minimal şablonumuz |
| **Egress vekil/DNS** | Küme içi (`crm-egress` ad alanı). | SSRF ikinci katmanı (hedef-IP ACL) NetworkPolicy ile ikame edilemez (L3/L4, FQDN yok) |
| **SMTP rölesi** | **Kubernetes profilinde Postfix rölesi düşer;** `notifications` pod'u NetworkPolicy ile doğrudan müşterinin SMTP röle IP'sine çıkar. | Compose'ta röle, Docker'da L3 çıkış ACL'i olmadığı için gerekliydi; Kubernetes'te aynı kısıtı NetworkPolicy verir. Postfix `restricted` PSS ile de uyumsuz (root) (M8A tasarımına dokunur; PO-13) |

### 2.12 Diğer
- Günlük: Serilog konsol [R]; pod silinince günlük kaybolur → düğüm/küme günlük toplama müşterinin işi; K16 (merkezî günlük yığını yok) korunur; günlükte IP/kullanıcı kimliği var → KVKK notu (PO-14).
- Kaynak: Compose `limits` → `requests`/`limits`; .NET cgroup sınırını okur. API: HPA (CPU); Worker: **sabit 2/rol** (KEDA/derinlik ölçeği erken; gerekirse `crm_outbox_pending` Prometheus ölçeği, DB kimliği gerektirmez; KEDA PostgreSQL ölçeği de var [W]).
- Dağıtım yerleşimi: `topologySpreadConstraints`/anti-affinity, `PodDisruptionBudget` (api/web `minAvailable: 1`), `priorityClass`.

---

## 3. Paketleme seçenekleri: puanlama

Seçenekler: **A** Compose'ta kal + sertleştir · **B** Helm chart (tek birinci-taraf chart; üçüncü taraf opsiyonel bağımlılıklar alt chart) · **C** Kustomize kaplamaları · **D** Helm + GitOps denetleyicisi (Argo CD/Flux) · **E** Operatör (özel CRD).

Puan 1–5 (yüksek = iyi). İki ağırlık profili: **P1** = S profili (küçük, tek sunucu), **P2** = M/L profili (HA, çok müşterili).

| Kriter | Ağırlık P1 | Ağırlık P2 | A | B | C | D | E |
|---|---|---|---|---|---|---|---|
| Geliştirme eforu (düşük efor = yüksek puan) | 20 | 10 | 5 | 2 | 3 | 1 | 1 |
| Air-gap kurulabilirlik (özel registry, imaj aynalama) | 20 | 15 | 5 | 4 | 4 | 3 | 3 |
| Yükseltme/geri alma | 10 | 15 | 2 | 4 | 3 | 5 | 5 |
| Değer şeması doğrulaması | 5 | 10 | 1 | 5 | 2 | 5 | 4 |
| Test (kind/k3d, lint, kubeconform, conftest, e2e yeniden kullanımı) | 15 | 10 | 5 | 4 | 4 | 4 | 2 |
| Müşteri beceri uyumu | 15 | 10 | 4 | 3 | 3 | 2 | 2 |
| Uzun vadeli bakım yükü (düşük yük = yüksek puan) | 10 | 10 | 4 | 3 | 3 | 3 | 1 |
| HA / ölçek tavanı | 5 | 20 | 2 | 5 | 5 | 5 | 5 |
| **Toplam (0–100) P1: S profili** | | | **82** | 69 | 68 | 60 | 49 |
| **Toplam (0–100) P2: M/L profili** | | | 67 | **78** | 71 | 74 | 64 |

Notlar (puanların gerekçesi):
- **A:** mevcut, e2e hazır, air-gap'te `docker load` yeter; şema doğrulaması yok (yalnız `:?`), yükseltme elle, HA tavanı düşük.
- **B:** `values.schema.json` (kendi içinde eksiksiz olmalı: dışa `$ref` air-gap'te kırılır — Flux issue #4992 [W]); `helm rollback` manifestleri geri alır, **DB migration'ı geri almaz** (expand/contract şart, §2.5). Air-gap: zincir = `helm package` + OCI/`tgz` paket + `skopeo/crane` ile imaj aynalama + `images.txt` üretimi (`helm template`'ten). Helm 4 Kasım 2025'te çıktı [W]; hedef Helm ≥ 3.16 ve 4.x uyumu [?].
- **C:** düz YAML çıktısı air-gap için en sade, ama koşul/şema yok, müşteri başına kaplama sapması (drift), gizli üretimi zayıf.
- **D:** B'nin üstüne denetleyici (müşteride yoksa bizden istenemez); air-gap'te git/OCI deposu + denetleyici imajları aynası gerekir. **Ürün olarak dayatılmaz; chart GitOps'a uyumlu yazılır** (kanca kullanımı, `existingSecret`, sürüm sabitleri) → müşteri Argo/Flux kullanabilir.
- **E:** operatör (Kubebuilder/.NET) 12–20 hf + kalıcı bakım; yalnız yüzlerce ayrı örnek yönetiliyorsa haklı. Bizim kiracı modelimiz tek paylaşımlı örnek (K2) → **elenir**.

**Öneri: B** (P2 kazananı), **A'yı S profili olarak korumak**. Tek chart, alt chart yalnız üçüncü taraf/opsiyonel parçalar için (redis, observability, egress); birinci-taraf bileşenler aynı chart'ta şablon (alt chart = değer iç içeliği + sürüm yükü).

### 3.1 Chart iskeleti (öneri)
`deploy/helm/crm/`: `Chart.yaml`, `values.yaml`, `values.schema.json`, `templates/` {`api`, `web`, `worker-<rol>`, `migrator-hooks`, `conductor`, `redis`, `egress`, `networkpolicies`, `ingress|gateway|route`, `pdb`, `hpa`, `servicemonitor|podmonitor`, `prometheusrule`, `grafana-dashboards`, `tests/`}, `ci/` (profil değerleri: `minimal`, `ha`, `openshift`, `egress`, `observability`). Hiçbir sır değeri `values`'a girmez (Helm sürüm kayıtları değerleri Secret olarak saklar): `existingSecret` deseni.

### 3.2 Test stratejisi
1. `helm lint --strict` + `helm template` (her profil) → **kubeconform** (yerel CRD şemaları: ServiceMonitor, CNPG, Gateway API) → **conftest/OPA** (veya Kyverno CLI): `runAsNonRoot`, `readOnlyRootFilesystem`, `latest`/etiketsiz imaj yok, kaynak sınırı var, NetworkPolicy var, `automountServiceAccountToken: false`.
2. `promtool check rules` (mevcut CI adımı) PrometheusRule kaynağı üzerinde aynen sürer.
3. **kind/k3d kurulum işi** (`chart-testing ct install`) → `helm test` (duman: `smoke.sh` mantığı, egress kanaryası: `1.1.1.1` erişilemez olmalı).
4. **e2e yeniden kullanımı:** `e2e/run.sh` Compose yaşam döngüsüne bağlı [R]; `E2E_BASE_URL` ile "dışarıdan hedef" kipi eklenir (yaşam döngüsü ile test koşusu ayrılır; `test` alt komutu zaten var). Playwright paketi değişmez.
5. **Yükseltme testi** N−1 → N (`helm upgrade` altında trafik) ve **kaos:** worker/api pod öldürme, düğüm boşaltma.
6. Not: CI'da otomatik tetik şu an kapalı (yalnız `workflow_dispatch`) [R]; yeni işler eklenir, açma kararı sahibin.

---

## 4. Güvenlik

### 4.1 NetworkPolicy: Compose ağ ayrımının karşılığı
Compose: `edge` (LB → web portu), `frontend`, **`backend` (`internal`, dış yol yok)**, `egress` (yalnız vekil+DNS), `mail` (yalnız röle) [R].
Kubernetes varsayılanı **her şeye izin**dir; bu yüzden ad alanında `default-deny` (ingress+egress) + açık izinler. C-SEC2 güvencesi ("API ve Postgres'ten dış ad çözümlenemedi") **CNI'nin NetworkPolicy uygulamasına koşulludur** (Calico/Cilium/OVN-Kubernetes uygular; düz flannel uygulamaz; k3s kube-router bileşenini paketler [?]) → kabul testi: `helm test` egress kanaryası.

| Kaynak → Hedef | Port | Not |
|---|---|---|
| Ingress denetleyici ad alanı → `web` | 8080 | tek dış giriş |
| `web` → `api` | 8080 | yalnız web `api`'ye ulaşır |
| `api` → Postgres / Redis(önbellek) / Conductor / S3 | 5432 / 6379 / 8080 / 9000|443 | harici hedefler için CIDR listesi (`externalEndpoints[].cidr`); FQDN yok (L3/L4) |
| `worker` (outbox, workflows, platform, scheduler, files) → Postgres, Redis, Conductor, S3 | idem | **internet yok** |
| `worker` (`integrations`) → `egress-proxy` 3128, `egress-dns` 53 | | yalnız bu rol etiketi |
| `worker` (`notifications`) → müşteri SMTP rölesi (CIDR:587/25) | | Postfix yok (§2.11) |
| `egress-proxy` → internet | 443/8443 | ipBlock 0.0.0.0/0 **except** özel/CGNAT/link-local/metadata (Squid ACL ile çift katman) |
| `conductor` → Postgres, (kilit Redis) | | |
| `migrator`/db-init kancaları → Postgres, S3 | | |
| Prometheus ad alanı → `api:9464`, `worker:9465`, exporter | | metrik dinleyicisi ana porttan ayrı [R: K20] |
| Tüm pod → küme DNS | 53 udp/tcp | yalnız `kube-dns`/`dns-default` |
Ek: `automountServiceAccountToken: false` (uygulama Kubernetes API'sine gitmez); bulut metadata adresi default-deny ile kapalı.

### 4.2 Pod güvenliği
- Ad alanı etiketi `pod-security.kubernetes.io/enforce: restricted` (PSA 1.25'ten beri GA [?]); `runAsNonRoot`, sayısal `runAsUser`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault`, `readOnlyRootFilesystem: true`.
- **Dockerfile'daki `USER app` adlıdır:** kubelet `runAsNonRoot` için adlı kullanıcıyı doğrulayamaz → `runAsUser` açıkça verilmeli (.NET imajlarında `app` UID'si 1654 **[?]**; nginx-unprivileged 101 **[?]**; spike S6). OpenShift'te SCC rastgele UID atar → `runAsUser` sabitlenmez (`platform: openshift` bayrağı).
- `readOnlyRootFilesystem` için `emptyDir`: API/Worker `/tmp`; nginx `/tmp` ve `/etc/nginx/conf.d` (giriş noktası şablonu buraya render eder) [R: `web/Dockerfile`].
- Squid/CoreDNS/Postfix imajları root/yazılabilir kök isteyebilir → `crm-egress` ad alanı en fazla `baseline`; Postfix zaten düşüyor.
- Kalıcı iyileştirme: `aspnet` Chiseled imajları (kabuk yok → Worker exec probe imkânsız; §2.1 HTTP sağlık ucu ön koşul).

### 4.3 Sırlar
| Seçenek | Air-gap | GitOps | Karar |
|---|---|---|---|
| Elle üretilen yerel `Secret` (`generate-secrets` K8s sürümü: `kubectl create secret … --dry-run -o yaml`) | ++ | − | **Varsayılan** (Compose ile eşdeğer akış) |
| Sealed Secrets | + | ++ | İsteğe bağlı |
| External Secrets Operator + Vault/OpenBao (müşteri) | ± (Vault müşteride varsa) | ++ | İsteğe bağlı; chart yalnız `existingSecret` adı ister |
| SOPS+age (Flux) | + | ++ | GitOps müşterisi için |
- **etcd'de gizli şifreleme (encryption at rest) müşteri platformunun ön koşuludur** (KVKK; kabul listesine yaz). Sır listesi Compose ile aynıdır (postgres süper kullanıcı **yalnız kancalarda**, `crm_owner` yalnız migrator, `crm_app` api+worker, JWT **yalnız api**, `integrations_encryption_key` api+worker+migrator, S3 uygulama anahtarları api+worker+migrator, S3 kök/KMS yalnız depo yöneticisi). **Pod başına en az sır** ilkesi korunur: her Deployment yalnız gerekeni monte eder, bileşen başına ayrı ServiceAccount.
- Yedek kuralı aynen: KMS anahtarı ve `integrations_encryption_key` veriden ayrı yedeklenir (runbook §4, K21).

### 4.4 İmaj kaynağı ve tedarik zinciri
- Her sürüm: buildx ile SBOM + provenance sertifikası, **cosign imzası (anahtar tabanlı; anahtarsız/keyless doğrulama çevrimdışı Rekor/TUF gerektirebilir [?])**, Trivy/Grype kapısı, **digest ile dağıtım** (`image.digest`; mutable etiket yasak). Mevcut iyi zemin: `packages.lock.json --locked-mode` ve sabit temel imaj etiketleri (C-SEC L10) [R]; `web/Dockerfile` etiketleri hâlâ kayan [R: hardening-report]. Temel imaj digest'leri sabitlenir.
- **Üçüncü taraf imajlar (postgres, conductor, redis, squid, coredns, mc/minio-halefi, prometheus…) müşteri sitesinde Docker Hub/Quay'dan çekilmez:** bizim registry'mizde aynalanır, taranır, **yeniden imzalanır**, paketle taşınır. (Neden: MinIO Docker Hub ad alanı silindi [W]; Bitnami sürümlü imajları kalktı [W].)
- Air-gap paketi `crm-release-<sürüm>.tar`: chart `.tgz`, imajlar (OCI dizin), SBOM'lar, imzalar, `images.txt`, sağlama toplamları, içe aktarma betiği (`skopeo/crane` → müşteri Harbor/registry [Harbor bilgisi **[?]**]). Doğrulama politikası (Kyverno `verifyImages`, OpenShift `ClusterImagePolicy`) opsiyonel örnek.
- SLSA seviyesi iddiası yok; hedef "imzalı + SBOM + taranmış + digest'le dağıtılmış" (kanıtlanabilir en az).

### 4.5 Chart RBAC'ı
Uygulama pod'ları için **hiç Role/ClusterRole yok**; chart küme kapsamlı kaynak (ClusterRole, CRD, Namespace) **oluşturmaz** (CRD'ler ön koşul: ServiceMonitor, CNPG, Gateway API). PSA etiketi ve NetworkPolicy ön koşulu ad alanı yöneticisi/küme yöneticisi işidir → "ön kurulum kontrol listesi" (S1). Helm kancaları ad alanı düzeyinde çalışır.

### 4.6 Çok kiracılı SaaS: ad alanı modeli — öneri
- **Kurulum başına tek paylaşımlı ad alanı** (`crm`) + `crm-egress`; **kiracı (organizasyon) başına ad alanı YOK.** Gerekçe: K2 tek DB, satır bazlı kiracı; kiracılar aynı pod, Conductor, Redis, DB'yi paylaşır — ad alanı ayrımı hiçbir izolasyon sağlamaz, yalnız N× Conductor/Redis/DB maliyeti getirir; kiracı izolasyonu uygulama katmanında (global filtre, testli) sürer.
- Fiziksel izolasyon isteyen müşteri (ör. bir bankanın iştiraki) için doğru birim **"müşteri başına ayrı Helm sürümü + ayrı ad alanı (veya küme)"**dir: aynı chart birden çok kez kurulabilir (ad alanı başına; küme kapsamlı kaynak olmaması bunu mümkün kılar).
- `ResourceQuota`/`LimitRange`, `topologySpread`, PDB; kiracı başına limitler zaten uygulamada (M7).

### 4.7 Yedekleme / geri yükleme (Postgres + nesne deposu)
| İhtiyaç | Küme içi (CNPG) | Harici DB |
|---|---|---|
| RPO dakikalar (PITR) | Sürekli WAL + temel yedek → S3; `ScheduledBackup` [W: CNPG özellikleri] | pgBackRest/WAL-G (runbook §13 zaten "sonraki adım") |
| Mantıksal yedek (bugünkü `backup.sh` eşdeğeri, `--exclude-schema=public`) | `CronJob` (pg_dump 17 + şifreleme gpg/openssl + S3) | aynı |
| Nesne deposu | Kova çoğaltma/`rclone` CronJob | aynı |
| Sırlar + KMS anahtarı | Velero/elle; **veriden ayrı** | aynı |
- **Tutarlılık kuralı korunmalı:** nesne yedeği DB yedeğinden **sonra** (DB'de olup nesnesi olmayan satır olmasın; nesne fazlası zararsız yetim) [R: backup.sh]. PITR'da geri yükleme anına göre nesne deposu "aynı ya da daha yeni" olmalı; `files-reconcile` Job'u sonrasında koşulur.
- **KVKK:** geri yükleme sonrası `migrator erase-deleted-tenants` Job'u zorunlu adım (imha edilen kiracı geri gelmesin) [R: runbook §12]; yedekler şifreli ve yurt içi.
- DR provası aylık; `restore.md` Senaryo A'nın Kubernetes karşılığı runbook'a eklenir.

### 4.8 Gözlemlenebilirlik (K20 yeniden kullanımı)
- Metrik dinleyicileri (Api 9464, Worker 9465) adlı konteyner portu `metrics`; **PodMonitor** (Prometheus Operator CRD; müşteride yoksa kapalı), bearer belirteci `Secret`'tan.
- `job` etiketi mevcut kurallar için `crm-api`/`crm-worker` kalmalı (relabeling / `jobLabel`); yoksa `CrmTargetDown`, `CrmScrapeTargetMissing` ve hepsi `{job="crm-worker"}` süzgeçli kurallar sessizce boşa düşer. Outbox alarmları zaten `max by (module)` (çok kopya güvenli) [R].
- **PrometheusRule**, `infra/observability/prometheus/rules/crm-alerts.yml`'den `.Files.Get` ile üretilir (tek kaynak; CI'daki `promtool` adımı sürer). Grafana panoları ConfigMap + sidecar etiketi.
- **Kubernetes'e özgü yeni alarmlar** (kube-state-metrics): CrashLoop/NotReady, PDB ihlali, PVC doluluğu, sertifika bitişi, Job (migrator) başarısızlığı, HPA tavanı.
- Seri sayısı pod başına `instance` etiketiyle doğrusal artar (runbook §15.8 ölçümü: ~3 000 seri pilotta) — ihmal edilebilir.

---

## 5. Önerilen yol ve aşamalı yol haritası

**İlke:** önce orkestratörden bağımsız "çok kopya hazırlığı" (Aşama 1) — Compose'a da fayda; **chart yatırımı (Aşama 2+) Aşama 0 çıktısı ve talebe göre kapılanır.**

| Aşama | Kartlar (Spec→sahip) | Eforlar (mühendis-hf) | Bağımlılık |
|---|---|---|---|
| **0 Keşif** | **C-K0** Spec/DevOps: müşteri platform anketi + spike S1–S6 (§6) | 1–2 | — |
| **1 Çok kopya hazırlığı** | **C-K1** Backend: `Worker:Roles` (M8A D4 ile birleşik) + `AdvisoryLockKeys` kaydı + mimari test; `ExecutionStatusSync`/`IntegrationsRetention` kilidi; workflow tanım kaydı Migrator'a; `WorkerId`=pod; Worker HTTP sağlık ucu; outbox sıra duyarlılığı denetimi (1.5–2) · **C-K2** Backend: Redis destekli `ILoginThrottle`/e-posta kovası/API anahtarı hata azaltması; çok kopyada Redis zorunlu doğrulaması; `LocalExpirationSeconds` politikası (1.5–2) · **C-K3** Backend: expand/contract politikası + CI taraması, `Auth:ValidationKeysPem`, kapanış denetimi (`ApiKeyUsageBuffer` boşaltma, yükleme drenajı), bağlantı havuzu/`..._FILE` parola (1–1.5) · **C-K4** DevOps+Backend: MinIO halefi/S3 karar spike'ı, K21/runbook §18 revizyonu, imaj aynası (1.5–2) | 5–7 | C-K1 ↔ M8A birleşimi |
| **2 Chart MVP** | **C-K5** DevOps: `deploy/helm/crm` (api/web/worker rolleri/migrator kancaları/conductor 1 kopya/redis, `values.schema.json`, PSS restricted, probe, PDB/HPA, `existingSecret`, k8s `generate-secrets`, `helm test`) (4–5) · **C-K6** DevOps+Security: NetworkPolicy seti + egress (vekil/DNS) + SMTP CIDR + kanarya testleri (2–3) · **C-K7** DevOps: CI (lint/template/kubeconform/conftest/kind), `e2e` dış hedef kipi, N−1→N yükseltme ve kaos testleri (2–3) | 8–11 | C-K1..K4, Aşama 0 |
| **3 HA ve veri düzlemi** | **C-K8** DevOps: CNPG referans mimarisi + PITR + geri yükleme/DR provası + runbook (2–3) · **C-K9** DevOps: PodMonitor/PrometheusRule/pano + K8s alarmları (1–1.5) · **C-K10** DevOps+Security: SBOM/cosign/Trivy, air-gap paketi ve içe aktarma betiği, Kyverno örneği (2–3) · (opsiyonel) Conductor HA + Redis kilidi (1–1.5) | 6–8 | Aşama 2 |
| **4 Pilot** | **C-K11** DevOps+Lead: müşteri kümesinde kurulum provası, çok kopya yük testi, arıza tatbikatları (pod/düğüm/DB/Redis/Conductor), runbook Kubernetes bölümü, kabul; OpenShift profili (SCC/Route) +1–2 | 3–5 | Aşama 3 |
| **Toplam** | | **≈ 23–33 hf** (2 kişiyle ~3–4 ay takvim) | Tahmin; ekip hızı kanıtı depoda yok **[?]** |

- **Karar kapısı G1** (Aşama 0 sonrası): platform + talep var mı? Yoksa **yalnız Aşama 1 + C-K4** yapılır (≈ 6–9 hf; Compose'a da değer katar; PO-1).
- **A hattı (Compose sertleştirme) paralel iş:** PITR (pgBackRest/WAL-G) Compose'a ≈ 1.5 hf; `--scale api=2 --profile redis` yolu Aşama 1 sonrası doğrulanır.
- Board notu: bu kartlar Spec→DevOps/Backend olarak §7 kararlarından sonra `Ready` olur; sıcak dosyalar (`Worker/Program.cs`, `ModuleCatalog`) yalnız-ekleme kuralına uyar.

---

## 6. Başlıca riskler ve spike gerektiren belirsizlikler

| # | Risk / belirsizlik | Etki | Spike / azaltma (süre) |
|---|---|---|---|
| R1 | **Müşteri veri merkezi platformu bilinmiyor** (OpenShift / RKE2 / vanilla kubeadm / Talos / k3s; Türkiye'de OpenShift ve RKE2 danışmanlık ekosistemi var [W: Sekom, ENC], dağılım **[?]**) | SCC/Route/CNI/depolama farkı chart'ı böler | **S1** (2 gün): platform anketi + "ön kurulum kontrol listesi": sürüm (1.34–1.36 destekli [W]), CNI, StorageClass, Ingress/Gateway, PKI, registry, etcd şifreleme, günlük toplama |
| R2 | CNI NetworkPolicy uygulamıyor → "API'nin interneti yok" güvencesi çöker | KVKK/C-SEC2 | **S2** (1 gün): egress kanarya + CNI tespiti; uygulamayan ortamda kurulumu reddet |
| R3 | Depolama sınıfı (RWO performansı, anlık görüntü, yerel disk) — Postgres/S3 | Gecikme, DR | **S3** (2 gün) fio/pgbench; CSI snapshot desteği |
| R4 | **S3 halefi SSE-S3 doğrulamasını karşılıyor mu**; MinIO imajının çekilebilirliği | K21 varsayımı, mevcut kurulumlar | **S4** (3 gün): halef adaylarıyla `Files:Storage` uyum testi; **hemen** mevcut imajı aynala |
| R5 | Conductor: root olmayan çalışma, çok örnek kilidi (Redis; Postgres kilidi #1058), sürüm/yama sahibi | HA sözü | **S5** (2 gün): `runAsNonRoot` denemesi, 2 örnek + Redis kilidi yük/kaos |
| R6 | Secret birimi sembolik bağlantıları, adlı `USER app`/UID, salt-okunur kök + nginx şablonu | Pod başlamaz | **S6** (1 gün): kind'da API/Worker/web restricted profilinde açılış |
| R7 | Sıralı güncellemede migration uyumsuzluğu | Kesinti/veri hatası | expand/contract kuralı (C-K3); yükseltme testi (C-K7) |
| R8 | Bellek içi sayaçlar / önbellek → güvenlik gerilemesi (giriş azaltma N×) | Hesap ele geçirme yüzeyi | C-K2; Redis yokken çok kopya başlatmayı reddet |
| R9 | Worker çok kopya davranışı test edilmemiş (Compose notu: "yük testinden geçene kadar 1 kopya"); outbox sırası, status-sync yarışı | Sessiz veri tutarsızlığı | C-K1 denetimi + Aşama 4 yük/kaos |
| R10 | Air-gap: eksik imaj listesi, Trivy DB çevrimdışı güncelleme, araç zinciri (helm/skopeo) müşteride yok | Kurulum blokajı | C-K10 paket + prova; araçları pakete koy |
| R11 | Destek yükü: iki paketleme + K8s sürüm matrisi (üçer minör sürüm; müşteriler geriden gider) | Maliyet | Destek matrisi N−2; S profili Compose sabit |
| R12 | Operatör/üçüncü taraf bileşen CVE'leri (CNPG örneği [W]) | Zincir riski | Aynala-tara-yeniden imzala; yama SLA'sı |
| R13 | KVKK: etcd şifreleme, düğüm günlükleri (IP/kullanıcı kimliği), yedek yeri, bulut K8s yurt dışı | Uyum | Ön koşul listesi; yurt dışı yönetilen K8s kapsam dışı |
| R14 | Oturum kilidi + havuzlayıcı; Helm sürüm kayıtlarında sır sızıntısı | Sessiz hata / sızıntı | §2.4, `existingSecret` |

Ek yan bulgular (K8s'ten bağımsız, ayrı karta konabilir): (a) **M8A ↔ Files advisory kilit anahtarı çakışması** (`7_303_001/002`); (b) **MinIO durumu ve K21 metni eski**; (c) runbook §9.1 "Redis çözer" diyor, `LoginThrottle` kodu Redis kullanmıyor; (d) `web/Dockerfile` kayan etiketleri; (e) CI otomatik tetiği kapalı.

---

## 7. Product Owner kararları (numaralı; önerimle)

1. **Kubernetes'in statüsü:** "Müşteri gerektirdiğinde desteklenen üretim hedefi; S profili Compose" — **Öneri: evet**; Aşama 0 + 1'i şimdi onayla, chart (Aşama 2+) G1 kapısında karar. (K13 metni buna göre güncellenir.)
2. **v1 hedef platformlar:** CNCF uyumlu vanilla/RKE2 (1.34–1.36, N−2 destek); OpenShift v1.1 profili; k3s yalnız geliştirme/test. Müşterinin gerçek platformu bilinmeden **kesinleştirme yok** (S1).
3. **HA hedefleri (K17'deki 4 sa/4 sa hâlâ onaysız):** S: 4 sa/4 sa · M: RTO 30–60 dk, RPO ≤ 5 dk (PITR) · L: RTO ≤ 15 dk, RPO ≤ 1 dk (senkron replika). **Öneri:** üç kademeyi kabul et; sözleşmeye yalnız ürünün ölçüp kanıtladığı kademe yazılır.
4. **Postgres:** chart'a gömülmez; harici/müşteri DBA'sı birinci sınıf, CNPG referans mimari (chart dışı). **Öneri: evet.**
5. **Nesne deposu:** MinIO'dan çıkış. **Öneri:** müşteri S3'ü öncelikli; referans halef S4 spike'ına göre (aday: SeaweedFS/Ceph RGW); K21/runbook §18 hemen revize; mevcut imaj hemen aynalanır; SSE-S3 zorunluluğu mu, "disk şifreleme + `AcknowledgeUnencrypted`" kabulü mü karara bağlanır.
6. **Conductor HA:** S/M'de 1 kopya + hızlı yeniden başlama; yalnız L'de 2 kopya + Redis kilidi; **X1 (BPMN) SUB_WORKFLOW'u Conductor kilidi sorunu çözülene kadar yasaklar/uyarır.** **Öneri: evet.**
7. **Önbellek tutarlılığı:** çok kopyada izin/askı iptali ≤ 30 sn (10 sn'ye indirilebilir) kabul; backplane şimdilik yok. **Öneri: kabul.**
8. **Giriş azaltma:** Redis destekli paylaşımlı depo, çok kopyada zorunlu (yoksa uygulama başlamaz). **Öneri: evet** (C-K2).
9. **Paketleme:** tek Helm chart (B), GitOps uyumlu ama denetleyici dayatılmaz, operatör yok; dağıtım biçimi OCI + air-gap tar paketi. **Öneri: evet.**
10. **Ad alanı modeli:** kurulum başına 1 ad alanı (+ `crm-egress`); kiracı başına ad alanı yok; fiziksel izolasyon = müşteri başına ayrı sürüm. **Öneri: evet.**
11. **Tedarik zinciri:** SBOM + cosign (anahtar tabanlı) + Trivy kapısı + digest ile dağıtım **bir sonraki sürümden itibaren** (K8s'ten bağımsız); imza anahtarını kim tutar (HSM/KMS/müşteri-özel)? **Öneri:** evet; anahtar sahibi PO+Security belirler.
12. **Sıralı güncelleme politikası:** expand/contract migration **zorunlu kural** (tüm Backend kartlarını etkiler); alternatif `Recreate` (kısa kesinti). **Öneri:** kural olsun (C-K3), S profili için `Recreate` serbest.
13. **SMTP:** Kubernetes profilinde Postfix rölesi yok, NetworkPolicy ile müşteri rölesine doğrudan (M8A ağ modeline dokunur; M8A birleşmeden karar verilirse maliyetsiz). **Öneri: evet.**
14. **Günlük:** K16 (merkezî günlük yığını yok) korunur; müşterinin düğüm/küme günlük toplaması KVKK saklama politikasına tabi (IP/kullanıcı kimliği içerir). **Öneri:** koru, belgele.
15. **Destek/işletim modeli:** kümeyi müşteri platform ekibi mi, biz mi işletiyoruz (yönetilen hizmet)? K8s sürüm matrisi N−2. **Öneri:** müşteri işletir; biz chart + runbook + yükseltme yolu veririz; yönetilen hizmet ayrı ticari karar (yalnız L/SaaS).
16. **Kaynak:** Aşama 0 (1–2 hf) ve C-K1..K4'ün `Ready`'ye alınması. **Öneri: onayla.**

---

## 8. Kabul kriterleri (Kubernetes desteği "hazır" sayılması için)
1. Kind + hedef platformda `helm install` + `helm test` yeşil; e2e Playwright paketi dış hedef kipiyle geçer.
2. 2×api + 2×her Worker rolü ile: pod öldürme/düğüm boşaltma altında istek hatası yok/kabul edilen sınırda; outbox birikmiyor; çift işleme/sıra ihlali yok.
3. Egress kanaryası: API/Worker(non-integrations) internete ve dış ad çözümlemeye **ulaşamıyor**; webhook yalnız vekil üzerinden.
4. N−1 → N `helm upgrade` trafik altında hatasız; migrator başarısızlığında yükseltme durur.
5. Geri yükleme provası: PITR/mantıksal yedekten yeni ad alanına; `erase-deleted-tenants` sonrası imha edilen kiracı görünmüyor; `smoke` geçiyor.
6. Air-gap: internetsiz kümede paket + betikle kurulum; tüm imajlar imza/SBOM ile doğrulanıyor.

---

## 9. Kaynaklar (web; erişim 2026-09-20; üçüncü taraf özetleri aksi belirtilmedikçe **doğrulanmalı**)
- MinIO depo durumu: https://github.com/minio/minio (README "THIS REPOSITORY IS NO LONGER MAINTAINED", arşiv 2026-04-25; doğrudan çekildi). Imaj/Docker Hub olayları: vonng.com/en/db/silo-is-coming, stormdevelopments.ca (ikincil).
- ingress-nginx emeklilik: https://www.kubernetes.io/blog/2025/11/11/ingress-nginx-retirement/ ; https://www.kubernetes.io/blog/2026/01/29/ingress-nginx-statement/ (birincil).
- Conductor çok örnek/kilit: https://conductor-oss.github.io/conductor/devguide/running/deploy.html (doğrudan çekildi); Postgres kilit + SUB_WORKFLOW hatası: https://github.com/conductor-oss/conductor/issues/1058 (arama özeti; kendiniz doğrulayın).
- HybridCache çapraz-düğüm L1: https://github.com/dotnet/runtime/issues/125602 ; https://github.com/dotnet/extensions/issues/7098 (arama özeti).
- CloudNativePG: https://cloudnative-pg.io/ ; https://github.com/cloudnative-pg/cloudnative-pg (CNCF Sandbox, 1.29 hattı, CVE-2026-44477: arama özeti).
- Kubernetes sürüm desteği (1.34–1.36 destekli, ~14 ay): https://kubernetes.io/releases/ , endoflife.date/kubernetes (arama özeti).
- Helm 4 (2025-11-12), Flux air-gap `values.schema.json` sorunu: https://github.com/fluxcd/flux2/issues/4992 (arama özeti).
- KEDA PostgreSQL ölçeği: https://keda.sh/docs/2.19/scalers/postgresql/.
- Swarm/Nomad durumu: virtualizationhowto.com (2026-03), mirantis.com blog (ikincil).
- Bitnami katalog değişikliği: https://github.com/bitnami/charts/issues/35164 (2025-08-28 / 2025-09-29).
- Türkiye ekosistemi (OpenShift/RKE2 danışmanlık): sekom.com.tr, encteknoloji.com (pazarlama sayfaları; **dağılım verisi değil**).
- **Doğrulanmadı [?] ve spike'a bağlı:** .NET Host `ShutdownTimeout` varsayılanı; `app` UID 1654 ve nginx-unprivileged UID 101; `AddDockerSecrets` sembolik bağlantı; Conductor `workerId` doğrulaması ve non-root; PSA sürümü; k3s NetworkPolicy; Harbor özellikleri; Zalando/Crunchy operatörleri; cosign çevrimdışı doğrulama; Garage/SeaweedFS/RustFS SSE-S3 desteği ve lisans ayrıntıları; Türkiye'deki dağıtım oranları.
