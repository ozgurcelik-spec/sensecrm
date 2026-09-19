# Backend (.NET) — mimari özet ve HTTP sözleşmesi

Kararlar için bkz. [kararlar.md](kararlar.md). Bu belge Milestone 1 (platform temeli), Milestone 2 (satış çekirdeği) ve Milestone 3 (aktiviteler + raporlar) sonunda backend'in gerçek durumunu anlatır.
Altyapı, senseik (HR SaaS) deposundan taşınmıştır: aynı teknoloji yığını (.NET 10, EF Core + Npgsql, xUnit v3) ve aynı proje yapısı.

## 1. Proje yapısı

```
Crm.slnx
src/
  Crm.Api/              API host'u (Program.cs, JWT, güvenlik başlıkları, correlation id, health)
  Crm.Worker/           Modüllerin outbox'ını boşaltan arka plan süreci (OutboxPollingService<T>)
  Crm.Migrator/         EF migration'larını uygular (migrate | reset)
  Shared/
    Crm.Shared.Kernel/          Entity/AggregateRoot/TenantAggregateRoot, Result/Error, EmailAddress, Money, DateRange, TenantCalendar (kiracı saat dilimi takvimi)
    Crm.Shared.Contracts/       CQRS arayüzleri, ITenantContext/ICurrentUser, IModule, izinler, sayfalama, integration event
    Crm.Shared.Infrastructure/  Dispatcher + davranışlar, ModuleDbContext, interceptor'lar, outbox/inbox, denetim, yerelleştirme (resx)
    Crm.Shared.Web/             ApiControllerBase, izin yetkilendirmesi, hata yönetimi, sürümleme/OpenAPI/CORS/hız sınırı
  Modules/
    Identity/           Domain · Application · Contracts · Infrastructure · Api   (şema: identity)
    Sales/              Domain · Application · Contracts · Infrastructure · Api   (şema: sales; Milestone 2; satış raporları Milestone 3)
    Activities/         Domain · Application · Contracts · Infrastructure · Api   (şema: activities; Milestone 3)
tests/
  Crm.Tests.Architecture/      Onion/modül sınırı kuralları (NetArchTest)
  Crm.Tests.TenantIsolation/   EF modelinde kiracı filtresi/indeks denetimi (veritabanısız)
  Crm.Tests.Shared/            Testcontainers (postgres:17-alpine) + WebApplicationFactory<Program> + Respawn
  Modules/                     Crm.Modules.Identity.Tests + Crm.Modules.Sales.Tests + Crm.Modules.Activities.Tests (birim + HTTP entegrasyon), Shared.Kernel/Infrastructure testleri
infra/docker-compose.yml       postgres + redis (host portları standart + 10000)
build/new-module.ps1           Yeni modül iskeleti
```

Kurallar (mimari testlerle zorlanır): Domain yalnız Kernel'e bağlıdır; Application, Infrastructure/Api'ye bağlanamaz;
modüller birbirine yalnız `*.Contracts` üzerinden bağlanır; her modülün tek DbContext'i ve kendi şeması vardır; handler'lar `sealed` ve `{Eylem}Handler` adlıdır.

## 2. Senseik'ten alınanlar, bilerek bırakılanlar

**Alınan:** Kernel, Contracts, Infrastructure (Dispatcher; Logging/Validation/Authorization/UnitOfWork/Caching davranışları; HybridCache — Redis isteğe bağlı L2;
`ModuleDbContext` + `AuditTenantInterceptor` + snake_case; outbox/inbox + `OutboxProcessor` + `InProcessEventBus`; correlation id/enricher'lar; resx yerelleştirme tr+en),
Shared.Web, Api host'u, Migrator, Worker, Identity modülünün auth/tenant/user/role/audit çekirdeği, mimari + kiracı izolasyonu testleri, test altyapısı.

**Uyarlananlar:** Kullanıcı hesabı artık **küresel**dir; organizasyonlara `Membership` (kullanıcı ↔ organizasyon ↔ rol) ile bağlanır (K1).
Denetim kaydı ortak `audit.audit_log_entries` tablosuna, her modülde otomatik yazılır (K14). Roller izinleri tek `text[]` kolonunda taşır (basit RBAC, K7).

**Bırakılanlar ve ne zaman geri gelir:**

| Bırakılan | Geri gelme zamanı |
|---|---|
| `EmployeeId`/`eid`, çalışan aramaları, birim yetkilendirme, `DataScope`/`IScopedRequest` (veri kapsamı) | Milestone 3+: kayıt sahipliği bazlı görünürlük (Zoho rol hiyerarşisi + paylaşım kuralları) |
| Impersonation (`act`), API anahtarları, erişim ayarları, modül aç/kapa filtresi, gizlilik/anonimleştirme | SaaS hazırlığı (Milestone 7) / KVKK işleri |
| MFA, e-posta ile davet, parola sıfırlama | Sonraki aşama (e-posta altyapısıyla birlikte); bugün üye ekleme parola ile doğrudan yapılır |
| Hangfire/zamanlanmış işler | Gerektiğinde (Worker'a eklenir); workflow için Conductor (K9, Milestone 4) |
| SignalR/bildirimler, MailKit/e-posta | E-posta bildirimi (2. aşama) |
| MinIO/S3 depolama | Dosya ekleri geldiğinde (K11) |
| Dışa aktarma (QuestPDF/ClosedXML/CsvHelper), Scriban | Raporlar/CSV içe-dışa aktarma |
| Anthropic/MCP/pgvector, FeatureManagement, RabbitMQ, mailpit | Gereksinim doğarsa |
| Sentry/Seq/Loki/Jaeger/Prometheus/Grafana/OTel exporter'ları | Üretim gözlemlenebilirliği (Kubernetes/Helm, K13). Şimdilik Serilog konsol + istek logu + Npgsql health check |

## 3. Çalıştırma

```powershell
docker compose -f infra/docker-compose.yml up -d          # postgres (localhost:15432), redis (localhost:16379, isteğe bağlı)
dotnet run --project src/Crm.Migrator                     # migration'ları uygular (audit + identity + sales + activities); "-- reset" yalnız Development
dotnet run --project src/Crm.Api                          # http://localhost:5080  (Scalar: /scalar, OpenAPI: /openapi/v1.json)
dotnet run --project src/Crm.Worker                       # outbox işleyici
dotnet test Crm.slnx                                      # entegrasyon testleri için Docker gerekir
```

- Geliştirme bağlantısı `appsettings.Development.json` içindedir (yalnız yerel compose parolası). Üretimde `ConnectionStrings__Database` ve `Auth__SigningKeyPem` ortam değişkeniyle verilir; depoda gizli bilgi yoktur.
- JWT (RS256): `Auth:SigningKeyPem` boşsa yalnız Development/Testing'de süreç başına geçici anahtar üretilir; diğer ortamlarda uygulama başlamaz. Issuer `crm`, audience `crm-api`.
- Web geliştirme sunucusu `/api` isteklerini `http://localhost:5080`'e yönlendirir; CORS `http://localhost:5173` için açıktır (`Cors:AllowedOrigins`).
- Migration üretimi (yerel araç `dotnet-ef`, `dotnet-tools.json`):
  `dotnet dotnet-ef migrations add <Ad> --project src/Modules/Identity/Crm.Modules.Identity.Infrastructure --startup-project src/Crm.Migrator --context IdentityDbContext -o Persistence/Migrations`
  Ortak denetim şeması için `--project src/Shared/Crm.Shared.Infrastructure --context AuditDbContext`. Mevcut migration'lar: `InitialIdentity`, `InitialAudit`, `InitialSales` (`--project src/Modules/Sales/Crm.Modules.Sales.Infrastructure --context SalesDbContext`), `InitialActivities` (`--project src/Modules/Activities/Crm.Modules.Activities.Infrastructure --context ActivitiesDbContext`).

## 4. Yeni modül ekleme

1. `./build/new-module.ps1 -Name Sales` — 5 projeyi (Domain/Application/Contracts/Infrastructure/Api) açar, `Crm.slnx`'e ekler, derler.
2. `src/Crm.Api/ModuleCatalog.cs`'e `new SalesModule()` ekleyin.
3. `Crm.Migrator` ve `Crm.Worker`'a modülün Infrastructure projesini referans verip `AddModuleDbContext` (Worker'da ayrıca `AddModuleHandlers` + `AddHostedService<OutboxPollingService<SalesDbContext>>`) ekleyin.
4. İzinleri `Contracts/SalesPermissions.cs`'te tanımlayın (`crm.<kaynak>.<eylem>`, grup `crm`); modül `IModule.Permissions` ile katalogla paylaşır. Yeni anahtarlar mevcut organizasyonların sistem rollerine API açılışında `SystemRolePermissionSynchronizer` ile yayılır (`SystemRoleDefinitions`: Administrator = tümü, Standard = tüm `crm.*` + `org.users.read`). Milestone 2'de `crm.accounts/contacts/leads/deals.*` anahtarları Identity.Contracts'taki `CrmPermissions`'tan `SalesPermissions`'a taşındı (anahtar dizgeleri aynı); Milestone 3'te `crm.activities.*` anahtarları `Activities.Contracts.ActivitiesPermissions`'a taşındı (aynı dizgeler); `crm.reports.read` iki modülün (Sales + Activities raporları) ortak izni olduğu için `Identity.Contracts.CrmPermissions.ReportsRead`'de kaldı.
5. Kiracıya ait varlıklar `TenantAggregateRoot`, denetlenecekler `IAuditLogged` olur; global kiracı filtresi, `TenantId` indeksi ve denetim kaydı otomatik gelir. Kiracısız (küresel) bir tablo eklemek bilinçli karardır: `TenantQueryFilterConventionTests.GlobalEntities` listesine eklenir.
6. Handler'ları **Application** assembly'sinden kaydedin (`AddModuleHandlers`; Domain/Contracts assembly'leri de verilir — outbox olay tipleri buradan çözülür). Integration event'i handler içinde `IIntegrationEventOutbox.Enqueue(...)` ile yayınlayın.

## 5. HTTP sözleşmesi (uygulandığı hâliyle)

Taban yol `/api/v1` (URL sürümleme). JSON camelCase, enum'lar camelCase string, `null` alanlar yazılmaz. Tüm uçlar `Authorization: Bearer <accessToken>` ister, `auth/*` uçları hariç.
Access token ~15 dk; refresh token döner (her yenilemede yenisi verilir). Organizasyon token'daki `tid` claim'idir; URL'de taşınmaz.

**Hatalar:** `application/problem+json` (ProblemDetails) + uzantılar `code` (kararlı, noktalı anahtar), `traceId`; doğrulamada ayrıca `errors: { alan: [mesajlar] }` (alan adları camelCase).
`title/detail` `Accept-Language` (tr/en) ile çevrilir; istemci `code`'a göre çalışır.

| code | HTTP | Ne zaman |
|---|---|---|
| `validation` | 400 | Girdi geçersiz (`errors` ile) |
| `auth.unauthenticated` | 401 | Token yok/geçersiz |
| `auth.invalid_credentials` | 401 | E-posta/parola hatalı |
| `auth.invalid_refresh_token` | 401 | Refresh token bilinmiyor/süresi dolmuş/yeniden kullanılmış |
| `auth.locked_out` | 401 | Ardışık hatalı giriş (5 deneme → 15 dk kilit) |
| `auth.no_active_organization` | 403 | Hesabın aktif üyeliği olduğu organizasyon yok |
| `forbidden` | 403 | İzin yok / üyesi olunmayan organizasyona geçiş |
| `not_found` | 404 | Kayıt yok **veya başka organizasyona ait** (varlık sızdırılmaz) |
| `auth.email_taken` | 409 | Kayıtta e-posta zaten var |
| `member.exists` | 409 | Kullanıcı zaten organizasyon üyesi |
| `role.in_use` | 409 | Rol üyelere atanmış |
| `role.name_taken` | 409 | Rol adı kullanımda |
| `role.system_readonly` | 422 | Sistem rolü değiştirilemez/silinemez |
| `member.last_admin` | 422 | Son aktif Administrator pasifleştirilemez/rolü düşürülemez |
| `general.rate_limit_exceeded` | 429 | Auth uçları IP başına dakikada 20 istek (`RateLimiting:Auth`) |

### Kimlik doğrulama (anonim, hız sınırlı)
- `POST /auth/signup` `{ organizationName, displayName, email, password, locale }` → `AuthResponse`. Yeni organizasyon (slug adından türetilir, `defaultLocale = locale`, `timeZone = Europe/Istanbul`), sistem rolleri (`Administrator`, `Standard`) ve kullanıcı Administrator olarak oluşur. Parola ≥ 8 karakter; `locale` `tr|en`.
- `POST /auth/login` `{ email, password }` → `AuthResponse` (son kullanılan, yoksa ilk aktif organizasyon).
- `POST /auth/refresh` `{ refreshToken }` → `AuthResponse`. Döner (rotating) token; kullanılmış token tekrar gelirse tüm aile iptal edilir.
- `POST /auth/logout` `{ refreshToken }` → 204 (bilinmeyen token için de 204).
- `POST /auth/switch-organization` `{ organizationId }` (Bearer) → `AuthResponse`; üye olunmayan organizasyon → `forbidden`.
- `AuthResponse = { accessToken, refreshToken, expiresAt }`

### Profil ve katalog
- `GET /me` → `{ user: { id, email, displayName, locale, isPlatformAdmin }, organization: { id, name, slug, defaultLocale, timeZone }, role: { id, name }, permissions: string[], organizations: [{ id, name, slug }] }`
- `PATCH /me` `{ displayName?, locale? }` → 204
- `GET /permissions` → `[{ key, group }]` (`group`: `org` | `crm`); 16 anahtar: `org.settings.manage, org.users.read, org.users.manage, org.roles.manage, org.audit.read`, `crm.{accounts,contacts,leads,deals}.{read,write}` (Sales modülü), `crm.activities.{read,write}` (Activities modülü) ve `crm.reports.read` (Identity.Contracts, ortak).

### Organizasyon
- `GET /organization` → `{ id, name, slug, defaultLocale, timeZone }` (aktif üye); `PUT /organization` `{ name, defaultLocale, timeZone }` → 204 (`org.settings.manage`; `timeZone` geçerli IANA kimliği olmalı)
- `GET /organization/members` → `[{ userId, email, displayName, roleId, roleName, isActive, joinedAt }]` (`org.users.read`)
- `POST /organization/members` `{ email, displayName, password, roleId }` → 201 üye satırı (`org.users.manage`). Hesap varsa yalnız üyelik eklenir (ad/parola yok sayılır); yoksa hesap açılır (ad+parola zorunlu). Zaten üyeyse `member.exists`.
- `PATCH /organization/members/{userId}` `{ roleId?, isActive? }` → 204 (`org.users.manage`; `member.last_admin` kuralı)
- `GET /organization/roles` → `[{ id, name, isSystem, permissions, memberCount }]` (`org.users.read`)
- `POST /organization/roles` `{ name, permissions }` → 201 rol; `PUT /organization/roles/{id}` `{ name, permissions }` → 204; `DELETE /organization/roles/{id}` → 204 (`org.roles.manage`; bilinmeyen izin anahtarı → `validation`)
- `GET /organization/audit?page=1&pageSize=50` → `{ items: [{ id, entityType, entityId, action, userId?, userDisplayName?, changes, occurredAt }], total }` (`org.audit.read`), en yeni önce. `action`: `created|updated|deleted`; `changes`: `{ "<alan>": { "old": ..., "new": ... } }` (alan adları camelCase; oluşturmada `old: null`, silmede `new: null`).

### Sağlık ve dokümantasyon
`/health`, `/health/live`, `/health/ready` (Npgsql), `/openapi/v1.json`, `/scalar`.

## 6. Kiracı izolasyonu ve denetim

- Kiracıya ait her varlık (`ITenantEntity`) için `ModuleDbContext` global `Tenant` query filter'ı ve `TenantId` indeksi ekler; `AuditTenantInterceptor` yazmada `TenantId`'yi atar, başka kiracıya yazmayı ve `TenantId` değiştirmeyi reddeder. Filtre atlama (`IgnoreQueryFilters`) yalnız iki bilinçli yerde vardır ve yalnız çağıran kullanıcının kendi üyeliklerini döner (giriş ve organizasyon listesi).
- Küresel tablolar: `tenants`, `users`, `refresh_tokens` (yalnız hash ile bulunur), outbox/inbox.
- Denetim (`AuditLogInterceptor`): `IAuditLogged` agregatların oluşturma/güncelleme/silme işlemleri iş verisiyle aynı transaction'da `audit.audit_log_entries`'e yazılır; `SensitiveFields` maskelenir. Kayıt sırasında (anonim) `userId` boştur.
- Testler: `Crm.Tests.TenantIsolation` (model düzeyinde), `OrganizationApiTests.CrossTenantIsolation_*` (A yöneticisi B'nin üyelerini/rollerini/denetimini göremez, değiştiremez).

## 7. Açık işler
- Kayıt sahipliği bazlı görünürlük/rol hiyerarşisi (K7), organizasyon grubu ve konsolidasyon (K4), PostgreSQL RLS (K2 ikinci savunma hattı).
- E-posta ile davet, parola sıfırlama, MFA; SSO (K6).
- Üretim gözlemlenebilirliği; Dockerfile'lar taşındı ama henüz bir imaj derlemesiyle doğrulanmadı.
- Activities/rapor açık işleri: bağlı kaydı silinmiş aktivitelerin temizlik işi (ilişki bugün yumuşak: `relatedName` boş döner), tekrarlayan görevler, hatırlatma bildirimleri (bildirim altyapısı M4+); aktivite listesindeki `relatedName` bağlı kaydın okuma iznine bakmaz (`crm.activities.read` yeter); raporlar çok para birimli tutarı ayırmadan toplar (M2 sınırlaması); rapor sorguları büyük kiracılar için henüz önbelleklenmez.
- Sales açık işleri: yinelenen firma/kişi tespiti, CSV içe aktarma, özel alanlar (kapsam dışı, bkz. [m2-satis-cekirdegi.md](../plan/m2-satis-cekirdegi.md)); pano `totalAmount` alanı para birimlerini ayırmadan toplar (TRY gösterimi; çok para birimli toplam sonraki iş); `GET /organization/members` hâlâ `org.users.read` ister (Administrator ve Standard'da var; bu izni taşımayan özel rollerde sahip seçici çalışmaz).

## 8. Sales modülü (Milestone 2 — satış çekirdeği)

HTTP sözleşmesinin bağlayıcı kaynağı [m2-api-kontrat.md](../plan/m2-api-kontrat.md); bu bölüm uygulanan hâli ve mimari kararları özetler.

**Modül:** `Crm.Modules.Sales.{Domain,Application,Contracts,Infrastructure,Api}`, şema `sales`, tek `SalesDbContext`. Firma, kişi ve fırsat lead dönüştürmede tek transaction'da yazıldığı için tek modüldedir.
Api host'u (`ModuleCatalog`), Migrator (`InitialSales`) ve Worker (outbox) Identity ile aynı kalıpla bağlanır.

**Agregatlar** (hepsi `TenantAggregateRoot`, `IAuditLogged`, yumuşak silinen; kimlikler `Guid.CreateVersion7`, EF'te `ValueGeneratedNever`):

| Agregat | Kurallar |
|---|---|
| `Account` | Ad kiracıda benzersiz değil; adres düz kolonlar (`BillingStreet…`; denetim alan bazında fark üretsin diye); `SensitiveFields`: e-posta, telefon |
| `Contact` | İsteğe bağlı firma; `SensitiveFields`: e-posta, telefon, cep; `FullName` türetilir |
| `Lead` | Kaynak `web\|referral\|campaign\|coldCall\|other`, durum `new\|contacted\|qualified\|unqualified\|converted`; `Convert(...)` sonrası salt-okur (`lead.already_converted`); durum `converted` yalnız dönüştürmeyle olur |
| `Pipeline` + `PipelineStage` | Aşama türü `open\|won\|lost`; tam bir `won` + bir `lost` + en az bir `open` şart; aşama sırası dizi sırasıdır; kaldırılan aşama yumuşak silinir; kullanımdaki (fırsatlı) aşama silinemez (`pipeline.stage_in_use`); organizasyonda tam bir varsayılan huni |
| `Deal` | Olasılık aşamadan gelir (saklanmaz); `MoveToStage` kazanma/kaybetmede `ClosedAt` yazar, açık aşamaya dönünce temizler, `lost` için neden zorunlu (`deal.lost_reason_required`), aşama değişince `DealStageChanged` domain event'i (outbox); tutar `decimal(18,4)`, para birimi ISO-4217 (varsayılan `TRY`) |

**Uygulama katmanı:** her komut/sorgu bir `[RequiresPermission]` taşır (kontratın izin tablosu): okuma `crm.<kaynak>.read`, yazma `crm.<kaynak>.write`, huni okuma `crm.deals.read`, huni değiştirme `org.settings.manage`; dönüştürme `leads.write + accounts.write + contacts.write` (+ fırsat açılıyorsa handler'da `crm.deals.write`). Doğrulama FluentValidation (`validation` + `errors`) ile yapılır ve yetkiden önce çalışır (geçersiz gövdeli yetkisiz istek 400 alır). Alan doğrulayıcıları oluşturma/güncelleme komutları arasında `I*Fields` arayüzü + genel taban doğrulayıcıyla paylaşılır.
- **Sahip (owner):** verilmezse çağıran kullanıcı; verilirse organizasyonun aktif üyesi olmalı (`owner.not_member`, 400) — `Identity.Contracts.IMemberLookup` (`IsActiveMemberAsync`, `GetDisplayNamesAsync`). Sahibi değişmeyen kayıtta üyelik yeniden sorgulanmaz (sahibi pasifleşen kayıt düzenlenebilir). Yanıtlarda `ownerName` bu arayüzle doldurulur (pasif üyeler dahil).
- **Liste sorguları:** `SalesReadStore` (kiracı + yumuşak silme filtresi altında). `q` `ILIKE` + kaçışlı parametredir (kullanıcı girdisindeki `%`/`_` düz metindir, SQL birleştirme yok); `sort` yalnız beyaz listedeki alanlarda (bilinmeyen alan yok sayılır, her zaman `Id` ile kararlı): firma `name, industry, createdAt, updatedAt`; kişi `lastName, firstName, email, createdAt, updatedAt`; lead `lastName, company, status, source, rating, createdAt, updatedAt`; fırsat `name, amount, closingDate, closedAt, createdAt, updatedAt`. Sayfalama varsayılan 25, üst sınır 100 (`Paging` ayarı; `PagingDefaults` de 25/100'e çekildi — `/organization/audit` de artık en çok 100 satır döner).
- **Pano:** `GET /deals/board` aşama başına tek toplama sorgusu (`count`, `totalAmount`) + aşama başına en çok 100 kart (en yeni önce).
- **Lead dönüştürme** (`ConvertLeadHandler`): firma (mevcut veya lead şirket adıyla yeni) + kişi + isteğe bağlı fırsat (pipeline'ın ilk açık aşaması) + lead durumu + `LeadConverted` outbox kaydı **tek `SaveChanges`/transaction**'da yazılır; tüm nesneler önce bellekte kurulur, herhangi bir kural/yazma hatasında hiçbiri kalıcı olmaz (entegrasyon testi fırsat yazımını patlatarak doğrular). Yeni firma ve kişinin sahibi lead'in sahibidir.
- **Olaylar:** `DealStageChanged` (domain event → Sales outbox), `LeadConverted` (integration event, `Sales.Contracts`; M3/M4 tüketir), `OrganizationCreated` (Identity integration event, `Identity.Contracts`; Sales tüketir).

**Varsayılan huni tohumlama (Identity, Sales'e bağlanmaz):** Kayıt (`SignUpHandler`) `OrganizationCreated` olayını aynı transaction'da Identity outbox'ına yazar; Worker olayı `IEventBus`'a yayınlar; Sales'in `OrganizationCreatedHandler`'ı `IDefaultPipelineSeeder` ile organizasyon dilinde (`tr|en`) 6 aşamalı varsayılan huniyi kurar. Aynı `DefaultPipelineSeeder` (organizasyon başına PostgreSQL advisory lock + kilit sonrası yeniden kontrol → idempotent, eşzamanlı çağrıya dayanıklı) üç yoldan çalışır: (1) Worker olayı, (2) API açılışında `DefaultPipelineSyncHostedService` — M1'de açılmış mevcut organizasyonlar için (`Identity.Contracts.ITenantDirectory`), (3) tembel güvence: Worker henüz olayı işlemediyse ilk huni/pano/fırsat isteği `DefaultPipelineResolver` ile tohumlar (kayıttan hemen sonraki istek de çalışır).

**Denetim:** Firma, kişi, lead, fırsat, huni ve huni aşaması değişiklikleri `audit.audit_log_entries`'e yazılır (aynı transaction; `EntityType` = `Account|Contact|Lead|Deal|Pipeline|PipelineStage`); kişisel veri alanları `***` ile maskelenir; enum alanları API'deki gibi camelCase string yazılır (`"converted"`). Kayıt bazlı uç: `GET /api/v1/audit?entityType=Account&entityId={id}&page&pageSize` → `/organization/audit` ile aynı `{ items, total }` biçimi (`Identity.Api.AuditController`). Yetki: `org.audit.read` **veya** varlık türünün kaynağının okuma izni; tür–izin eşlemesini her modül `Identity.Contracts.IAuditEntityPermissions` ile bildirir (Sales: `SalesAuditEntities`), bildirilmeyen türler yalnız `org.audit.read` ile okunur. `entityType`/`entityId` zorunludur (`validation`).

**Uç noktalar** (hepsi `/api/v1`, Bearer):

| Yol | Notlar |
|---|---|
| `GET/POST /accounts`, `GET/PUT/DELETE /accounts/{id}`, `GET /accounts/{id}/contacts`, `GET /accounts/{id}/deals` | Detayda `contactCount`, `dealCount`; bağlı kişi/fırsat varken silme `account.has_dependents` (409) |
| `GET/POST /contacts`, `GET/PUT/DELETE /contacts/{id}` | Filtre `accountId`, `ownerUserId`; yanıtta `fullName`, `accountName` |
| `GET/POST /leads`, `GET/PUT/DELETE /leads/{id}`, `POST /leads/{id}/convert` | Filtre `status`, `source`, `ownerUserId`; `convert` → 200 `{ accountId, contactId, dealId? }` |
| `GET/POST /pipelines`, `GET/PUT /pipelines/{id}`, `PUT /pipelines/{id}/stages` | Okuma `crm.deals.read`, değiştirme `org.settings.manage`; varsayılanı düşürme `pipeline.default_required` (422) |
| `GET/POST /deals`, `GET/PUT/DELETE /deals/{id}`, `GET /deals/board`, `POST /deals/{id}/stage` | Filtre `pipelineId, stageId, stageKind, ownerUserId, accountId`; `PUT` aşamayı değiştirmez; yanıtta `contactId?`, `contactName?`, `stageName`, `stageKind`, `probability`, `pipelineName`, `closedAt?`, `lostReason?` |
| `GET /audit?entityType&entityId` | Kayıt bazlı denetim (yukarıda) |

Oluşturma uçları 201 + `Location` + oluşan kaydın gövdesini döner; güncelleme/silme 204 (PUT tam değiştirmedir: gönderilmeyen isteğe bağlı alan temizlenir, `ownerUserId` verilmezse mevcut sahip korunur).

**Yeni hata kodları** (kontrat + HTTP eşlemeleri; metinler `SharedResource.resx` tr/en):

| code | HTTP | Ne zaman |
|---|---|---|
| `lead.already_converted` | 409 | Dönüşmüş lead güncelleme/yeniden dönüştürme |
| `account.has_dependents` | 409 | Bağlı kişi/fırsatı olan firmayı silme |
| `pipeline.stage_in_use` | 409 | Fırsat içeren aşamayı silme (`stageName` argümanı) |
| `pipeline.default_required` | 422 | Varsayılan huniyi "varsayılan değil" yapma |
| `pipeline.stage_not_found` | 404 | Aşama bu hunide/organizasyonda yok |
| `deal.lost_reason_required` | 400 | `lost` aşamaya kayıp nedeni olmadan geçiş |
| `owner.not_member` | 400 | Sahip organizasyonun aktif üyesi değil |

Başka organizasyonun kaydı her zaman `not_found` (404) döner (çapraz referanslar dahil: başka organizasyonun firmasına kişi/fırsat bağlamak, başka organizasyonun aşamasına taşımak).

**Testler:** `Crm.Modules.Sales.Tests` — domain birim testleri (lead dönüştürme/değişmezlik, aşama/olasılık, kayıp nedeni, tam bir won + bir lost kuralı, aşama yeniden düzenleme/kullanımdaki aşama, hassas alanlar); Testcontainers HTTP testleri (her kaynağın CRUD'u, dönüştürme + geri alma + gerçek atomiklik, pano toplamları/100 kart sınırı, liste filtre/sıralama/sayfalama/arama kaçışı, yazma izni olmayan kullanıcıya 403, sahip kuralı, **kiracılar arası izolasyon**, kayıtta huni tohumlama (tr/en), açılışta mevcut organizasyon tohumlama, denetim kayıtları + maskeleme + kayıt bazlı uç). Mimari ve kiracı izolasyonu testleri yeni modülü otomatik kapsar.

## 9. Milestone 2'de değişenler (backend)
- Yeni modül `Sales` (şema `sales`, migration `InitialSales`); `Crm.Api`, `Crm.Migrator`, `Crm.Worker`'a bağlandı.
- `Identity.Contracts`: `CrmPermissions` içinden `accounts/contacts/leads/deals` anahtarları `SalesPermissions`'a taşındı; `IMemberLookup.GetDisplayNamesAsync`, `ITenantDirectory`, `OrganizationCreated`, `IAuditEntityPermissions` eklendi. Sistem rolleri: Administrator tüm anahtarlar (16), Standard tüm `crm.*` + `org.users.read`; mevcut organizasyonlar API açılışında `SystemRolePermissionSynchronizer` ile güncellenir.
- Identity: kayıtta `OrganizationCreated` outbox olayı; `GET /audit?entityType&entityId` (kayıt bazlı denetim).
- Ortak: `Paging` varsayılanı 25 / üst sınır 100; `AuditLogInterceptor` enum alanlarını camelCase string yazar; `SharedResource` tr/en'e Sales hata/alan anahtarları eklendi.
- Test altyapısı: `CrmApiFactory` Respawn şemalarına `sales` eklendi.

## 10. Activities modülü (Milestone 3)

HTTP sözleşmesinin bağlayıcı kaynağı [m3-aktivite-rapor.md](../plan/m3-aktivite-rapor.md); bu bölüm uygulanan hâli ve kararları özetler.

**Modül:** `Crm.Modules.Activities.{Domain,Application,Contracts,Infrastructure,Api}`, şema `activities`, tek `ActivitiesDbContext` (migration `InitialActivities`). `Crm.Api` (`ModuleCatalog`), `Crm.Migrator` ve `Crm.Worker` (outbox; bugün olay üretmez) Sales ile aynı kalıpla bağlandı. Modül Sales'e yalnız `Sales.Contracts` üzerinden, Identity'ye `Identity.Contracts` üzerinden bağlıdır (mimari testler yeşil).

**Agregat `Activity`** (`TenantAggregateRoot`, `IAuditLogged`, yumuşak silinen; kimlik `Guid.CreateVersion7`, kişisel veri alanı yok → maskelenen alan yok):

| Kural | Uygulama |
|---|---|
| Tür | `task`, `call`, `meeting`, `note`; `PUT` türü değiştirebilir (nota çevirmek durumu `completed` yapar) |
| Durum | `open`, `completed`, `cancelled`; oluştururken verilmezse `open` (not: `completed`). Geçişler: tamamla (`completedAt` yazılır, tekrar tamamlama ilk anı korur), yeniden aç (`completedAt` temizlenir; tamamlanmış ve iptal edilmişten), `PUT` ile `status` |
| Not | Her zaman `completed`; `complete`/`reopen`, farklı `status` (POST/PUT) → `activity.note_status_fixed` (409) |
| Aralık | `endAt >= startAt` (ikisi de verilmişse), aksi `activity.invalid_range` (400); zamanlar UTC'ye normalleştirilir (`Kind` belirtilmemiş = UTC) |
| Öncelik | `low`, `normal`, `high`; varsayılan `normal` (PUT'ta verilmezse `normal`) |
| İlişki | `relatedType` (`account`, `contact`, `lead`, `deal`) + `relatedId` birlikte (yoksa `validation`); yumuşak bağ: bağlı kayıt silinirse aktivite kalır, `relatedName` boş döner ve aktivite başka alanlar için düzenlenebilir kalır (ilişki yalnız değiştiyse yeniden doğrulanır) |
| Atanan | Verilmezse çağıran; verilen aktif üye değilse `owner.not_member` (400) — `IMemberLookup`; mevcut atanan korunurken üyelik yeniden sorgulanmaz |
| `isOverdue` | Yanıtta hesaplanır: `open` ve `dueAt` geçmiş |

**Sales'e bağ (`Sales.Contracts.IRecordLookup`):** `ExistsAsync`, `GetDisplayNameAsync`, toplu `GetDisplayNamesAsync(RecordRef[])` (tür başına tek sorgu). `Sales.Infrastructure.RecordLookup` kiracı + yumuşak silme filtresi altında uygular; ad: firma adı, kişi/potansiyel tam adı, fırsat adı. Yok / silinmiş / başka organizasyon → `activity.related_not_found` (404; M2'deki çapraz referans davranışıyla aynı, varlık sızdırılmaz).

**Uç noktalar** (`/api/v1`, izin `crm.activities.read` / `crm.activities.write`):

| Yol | Notlar |
|---|---|
| `GET /activities` | Filtre: `q` (konu + açıklama, `ILIKE`, joker kaçışlı), `type`, `status`, `assignedUserId`, `relatedType`, `relatedId`, `dueFrom`, `dueTo`, `overdue`; `sort`: `dueAt` (varsayılan artan; **boş `dueAt` her iki yönde de sonda**), `createdAt`, `subject`, `priority` (low < normal < high); bilinmeyen alan yok sayılır; her zaman `createdAt` + `Id` ile kararlı; sayfalama varsayılan 25 / üst sınır 100 |
| `GET /activities/summary?assignedUserId` | Yoksa çağıran → `{ openCount, overdueCount, dueTodayCount, completedThisWeek }` |
| `GET /activities/{id}`, `POST` (201), `PUT /{id}` (204, tam değiştirme), `DELETE /{id}` (204, yumuşak) | POST/PUT gövdesi: `type*, subject*, description?, status?, priority?, dueAt?, startAt?, endAt?, relatedType?, relatedId?, assignedUserId?` |
| `POST /activities/{id}/complete`, `POST /activities/{id}/reopen` | 204; idempotent; not için 409 |
| `GET /reports/activities/by-user?from&to` | `crm.reports.read` (aşağıda) |

`dueFrom`/`dueTo` UTC anlarıdır ve **uçları dahildir** (`dueAt >= dueFrom`, `dueAt <= dueTo`); biri verildiğinde `dueAt`'ı olmayan aktiviteler dışarıda kalır. Bu sayede pano listesi (`type=task&status=open&assignedUserId=<ben>&dueTo=<bugünün son anı>&sort=dueAt`) geciken + bugün vadeli görevleri en eski önce getirir. `overdue=true` yalnız `open` ve `dueAt` geçmiş olanları, `overdue=false` bunların dışındakileri döner. Sorgu parametresindeki tarih-saatlerin `Kind`'ı yoksa UTC kabul edilir.

**Özet ve kiracı saat dilimi:** "bugün" ve "bu hafta" **organizasyonun saat diliminde** (`organization.timeZone`) hesaplanır: bugün = yerel gün `[00:00, ertesi 00:00)`, hafta = pazartesi başlangıçlı `[pzt 00:00, sonraki pzt 00:00)`; sınırlar UTC'ye çevrilip sorguya gider (yaz saati geçişi ve UTC'den çok farklı dilimler testlidir). `openCount` açık, `overdueCount` açık ve `dueAt < şimdi`, `dueTodayCount` açık ve `dueAt` bugünün içinde (geçmiş olsun olmasın), `completedThisWeek` bu hafta tamamlanan. **Notlar (her zaman tamamlanmış) özet ve aktivite raporunda sayılmaz; iptal edilenler `openCount`'a girmez.** Saat dilimi `Identity.Contracts.TenantInfo.TimeZone` (`ITenantDirectory`) ile okunur; `TenantCalendarService` (Identity.Contracts, Identity kaydeder) Sales ve Activities için ortak kısayoldur (`Kernel.Time.TenantCalendar`: bugün, hafta başı, gün başlangıcı, UTC sınırları, ISO hafta/ay etiketleri; bilinmeyen kimlik → UTC).

**Denetim:** `Activity` değişiklikleri `audit.audit_log_entries`'e yazılır (`EntityType` = `Activity`); enum alanları camelCase string. `GET /audit?entityType=Activity&entityId=` **`crm.activities.read`** ile çalışır: Activities modülü `IAuditEntityPermissions` (`ActivitiesAuditEntityPermissions`, `ActivitiesAuditEntities`) ile türü bildirir; Identity'deki `GetEntityAuditHandler` değişmedi.

**Yeni hata kodları** (metinler `SharedResource.resx` tr/en):

| code | HTTP | Ne zaman |
|---|---|---|
| `activity.related_not_found` | 404 | İlişkili kayıt aktif organizasyonda yok |
| `activity.invalid_range` | 400 | `endAt < startAt` |
| `activity.note_status_fixed` | 409 | Not için tamamla/yeniden aç veya farklı durum |
| `owner.not_member` | 400 | Atanan aktif üye değil (Sales ile aynı kod) |

## 11. Raporlar (Milestone 3)

Hepsi `crm.reports.read` ister. Tarih parametreleri `from`, `to` (`YYYY-MM-DD`, **uçlar dahil**) **organizasyon saat diliminde takvim günü** olarak yorumlanır (UTC değil; ör. Europe/Istanbul'da 31 Mart 21:30Z, 1 Nisan sayılır); ikisi de isteğe bağlıdır, varsayılan **son 12 ay**: `to` = bugün (kiracı saatiyle), `from` = 11 ay önceki ayın ilk günü (ay bazlı gruplamada tam 12 dönem). Ters aralık veya 10 yılı aşan aralık → `validation` (`errors.to`); yalnız `from` verilip bugünden sonraya düşerse `validation`. Tutarlar fırsat para birimlerine bakmadan toplanır (`currency` yok).

| Yol | Modül | Yanıt ve tanım |
|---|---|---|
| `GET /reports/sales/funnel?pipelineId` | Sales | `{ pipelineId, stages: [{ id, name, kind, order, probability, count, totalAmount }] }` — huni verilmezse varsayılan (yoksa tohumlanır); **güncel durum** (tarih aralığı yok), tüm aşamalar sırasıyla (boş aşama 0), silinmiş fırsatlar yok; bilinmeyen/başka organizasyon huni `not_found` |
| `GET /reports/sales/won-lost?from&to&groupBy` | Sales | `groupBy` = `month` (varsayılan) veya `week`. `[{ period, wonCount, wonAmount, lostCount, lostAmount }]` — `closedAt`'a göre; dönem `2026-09` veya ISO hafta `2026-W38`; **aralıktaki boş dönemler 0 ile doldurulur**; ilk/son dönem aralık kenarında kısmi olabilir; artan sıra |
| `GET /reports/sales/leads-by-source?from&to` | Sales | `[{ source, count, convertedCount }]` — `createdAt`'a göre; yalnız potansiyeli olan kaynaklar; sayı azalan, sonra kaynak sırası |
| `GET /reports/sales/by-owner?from&to` | Sales | `[{ ownerUserId, ownerName, openDealCount, openDealAmount, wonCount, wonAmount, leadCount }]` — **açık fırsatlar güncel durumdur (aralıktan bağımsız)**, `won*` aralıktaki `closedAt`, `leadCount` aralıktaki `createdAt`; kazanılan tutar azalan sıra; adlar `IMemberLookup` (pasif üyeler dahil) |
| `GET /reports/activities/by-user?from&to` | Activities | `[{ userId, userName, completedCount, openCount, overdueCount }]` — `completedCount`: aralıkta `completedAt`; `openCount`: `open` ve `dueAt` aralıkta; `overdueCount`: bunlardan `dueAt < şimdi`; notlar hariç; tamamlanan azalan sıra |

Uygulama: Sales'te `ISalesReportStore` (`SalesReportStore`, veritabanında toplama; kapanış günleri `timezone(zone, closed_at)` ile — `SalesDbFunctions.ToLocalTimestamp` DbFunction eşlemesi — yerel güne indirilir) + `WonLostPeriods` (ay/ISO hafta bölme ve sıfır doldurma, saf hesap, birim testli); Activities'te `IActivityReadStore.GetUserTotalsAsync`. Rapor handler'ları `TenantCalendarService.ResolveReportRangeAsync` ile aralığı UTC yarı açık `[from, to+1 gün)` sınırlarına çevirir.

**Testler:** `Crm.Shared.Kernel.Tests` (`TenantCalendar`: saat dilimi, yaz saati, pazartesi haftası, ISO etiketleri, varsayılan aralık); `Crm.Modules.Activities.Tests` — domain (durum geçişleri, aralık kuralı, not kuralı, UTC normalleştirme, `isOverdue`, özet penceresi) + Testcontainers HTTP (CRUD ve tam değiştirme, doğrulama, tür kuralları, tamamla/yeniden aç, ilişkili kayıt doğrulama/adı/yumuşak bağ, atama kuralı, filtre/sıralama/sayfalama, **özet saat dilimi sınırları (UTC+14 kiracı)**, izinler, **kiracı izolasyonu**, denetim + `crm.activities.read` ile kayıt bazlı okuma, aktivite raporu bilinen veriyle); `Crm.Modules.Sales.Tests` — `SalesReportsApiTests` (funnel/won-lost/kaynak/temsilci bilinen veriyle, aralık uçları kiracı saat diliminde, boş dönem doldurma, ISO hafta, varsayılan aralık, izin, doğrulama, kiracı izolasyonu) + `WonLostPeriodsTests`.

## 12. Milestone 3'te değişenler (backend)
- Yeni modül `Activities` (şema `activities`, migration `InitialActivities`); `Crm.Api`, `Crm.Migrator`, `Crm.Worker`'a bağlandı; test altyapısı Respawn şemalarına `activities` eklendi; yeni test projesi `Crm.Modules.Activities.Tests`.
- `Sales.Contracts`: `IRecordLookup` (+ `RecordType`, `RecordRef`). `Sales`: 4 satış raporu ucu, `ISalesReportStore`, `SalesDbFunctions`.
- `Identity.Contracts`: `crm.activities.*` anahtarları `ActivitiesPermissions`'a taşındı (dizgeler aynı; Administrator = tüm 16 anahtar, Standard = tüm `crm.*` + `org.users.read` kuralı korunur — `SystemRolePermissionSynchronizer` API açılışında mevcut organizasyonlara da uygular); `CrmPermissions` yalnız `crm.reports.read` taşır; `TenantInfo.TimeZone` ve `TenantCalendarService` eklendi.
- `Kernel`: `Time.TenantCalendar`.
- `SharedResource` tr/en: `activity.*` hata anahtarları, `validation.activity_related`, `validation.date_range`, yeni `field.*` adları.
