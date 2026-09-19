# Backend (.NET) — mimari özet ve HTTP sözleşmesi

Kararlar için bkz. [kararlar.md](kararlar.md). Bu belge Milestone 1 (platform temeli) sonunda backend'in gerçek durumunu anlatır.
Altyapı, senseik (HR SaaS) deposundan taşınmıştır: aynı teknoloji yığını (.NET 10, EF Core + Npgsql, xUnit v3) ve aynı proje yapısı.

## 1. Proje yapısı

```
Crm.slnx
src/
  Crm.Api/              API host'u (Program.cs, JWT, güvenlik başlıkları, correlation id, health)
  Crm.Worker/           Modüllerin outbox'ını boşaltan arka plan süreci (OutboxPollingService<T>)
  Crm.Migrator/         EF migration'larını uygular (migrate | reset)
  Shared/
    Crm.Shared.Kernel/          Entity/AggregateRoot/TenantAggregateRoot, Result/Error, EmailAddress, Money, DateRange
    Crm.Shared.Contracts/       CQRS arayüzleri, ITenantContext/ICurrentUser, IModule, izinler, sayfalama, integration event
    Crm.Shared.Infrastructure/  Dispatcher + davranışlar, ModuleDbContext, interceptor'lar, outbox/inbox, denetim, yerelleştirme (resx)
    Crm.Shared.Web/             ApiControllerBase, izin yetkilendirmesi, hata yönetimi, sürümleme/OpenAPI/CORS/hız sınırı
  Modules/
    Identity/           Domain · Application · Contracts · Infrastructure · Api   (şema: identity)
tests/
  Crm.Tests.Architecture/      Onion/modül sınırı kuralları (NetArchTest)
  Crm.Tests.TenantIsolation/   EF modelinde kiracı filtresi/indeks denetimi (veritabanısız)
  Crm.Tests.Shared/            Testcontainers (postgres:17-alpine) + WebApplicationFactory<Program> + Respawn
  Modules/                     Crm.Modules.Identity.Tests (birim + HTTP entegrasyon), Shared.Kernel/Infrastructure testleri
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
dotnet run --project src/Crm.Migrator                     # migration'ları uygular (audit + identity); "-- reset" yalnız Development
dotnet run --project src/Crm.Api                          # http://localhost:5080  (Scalar: /scalar, OpenAPI: /openapi/v1.json)
dotnet run --project src/Crm.Worker                       # outbox işleyici
dotnet test Crm.slnx                                      # entegrasyon testleri için Docker gerekir
```

- Geliştirme bağlantısı `appsettings.Development.json` içindedir (yalnız yerel compose parolası). Üretimde `ConnectionStrings__Database` ve `Auth__SigningKeyPem` ortam değişkeniyle verilir; depoda gizli bilgi yoktur.
- JWT (RS256): `Auth:SigningKeyPem` boşsa yalnız Development/Testing'de süreç başına geçici anahtar üretilir; diğer ortamlarda uygulama başlamaz. Issuer `crm`, audience `crm-api`.
- Web geliştirme sunucusu `/api` isteklerini `http://localhost:5080`'e yönlendirir; CORS `http://localhost:5173` için açıktır (`Cors:AllowedOrigins`).
- Migration üretimi (yerel araç `dotnet-ef`, `dotnet-tools.json`):
  `dotnet dotnet-ef migrations add <Ad> --project src/Modules/Identity/Crm.Modules.Identity.Infrastructure --startup-project src/Crm.Migrator --context IdentityDbContext -o Persistence/Migrations`
  Ortak denetim şeması için `--project src/Shared/Crm.Shared.Infrastructure --context AuditDbContext`. Mevcut migration'lar: `InitialIdentity`, `InitialAudit`.

## 4. Yeni modül ekleme

1. `./build/new-module.ps1 -Name Sales` — 5 projeyi (Domain/Application/Contracts/Infrastructure/Api) açar, `Crm.slnx`'e ekler, derler.
2. `src/Crm.Api/ModuleCatalog.cs`'e `new SalesModule()` ekleyin.
3. `Crm.Migrator` ve `Crm.Worker`'a modülün Infrastructure projesini referans verip `AddModuleDbContext` (Worker'da ayrıca `AddModuleHandlers` + `AddHostedService<OutboxPollingService<SalesDbContext>>`) ekleyin.
4. İzinleri `Contracts/SalesPermissions.cs`'te tanımlayın (`crm.<kaynak>.<eylem>`, grup `crm`); modül `IModule.Permissions` ile katalogla paylaşır. Yeni anahtarlar mevcut organizasyonların sistem rollerine API açılışında `SystemRolePermissionSynchronizer` ile yayılır (`SystemRoleDefinitions`: Administrator = tümü, Standard = tüm `crm.*` + `org.users.read`). Milestone 2'de `CrmPermissions` (Identity.Contracts) içindeki geçici kayıt ilgili modüle taşınıp kaldırılır.
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
- `GET /permissions` → `[{ key, group }]` (`group`: `org` | `crm`); 16 anahtar: `org.settings.manage, org.users.read, org.users.manage, org.roles.manage, org.audit.read`, `crm.{accounts,contacts,leads,deals,activities}.{read,write}`, `crm.reports.read`

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
- `CrmPermissions` geçici kaydının Milestone 2'de modüllere taşınması.
