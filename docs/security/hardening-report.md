# Güvenlik sertleştirme raporu (C-SEC, pilot öncesi)

Kapsam: pilot öncesi güvenlik incelemesinin kalan bulguları (backend). M5 (pilot yayın) daha önce şunları kapattı: Production'da kapalı kendi kendine kayıt
(`Registration:Mode`), dağıtım sertleştirmesi, `ForwardedHeaders`, nginx CSP, dokümantasyonun yalnız Development'ta açık olması, en az yetkili `crm_app` DB rolü,
Conductor'un yayınlanmaması (bkz. [m5-pilot-yayin.md](../plan/m5-pilot-yayin.md)). Mimari ayrıntı: [backend.md §16](../architecture/backend.md). Operasyon: [runbook.md §9.1](../operations/runbook.md).

> **Kimlik eşlemesi hakkında not.** Özgün inceleme metni bu depoda yoktur; kart tanımı yalnız aşağıdaki kimlikleri açıkladı. Açıklanmayan kimlikler (H2, H3, M1, M4, M8, L1, L9, L11)
> tabloda "M5'te kapatılan başlıklarla eşlenir" olarak işaretlidir; hangisinin hangi M5 maddesine karşılık geldiğini özgün metinden Lead doğrulamalıdır.

## Kapanış tablosu

| Kimlik | Bulgu | Durum | Dosyalar | Testler |
|---|---|---|---|---|
| **H1** | Conductor görev girdisine güven (kiracı, lead, rol kimlikleri, onaylayıcı, karar) | **Burada düzeltildi** | `Workflows.Infrastructure/WorkflowsRuntime.cs` (`WorkflowTaskRunner`), `Workflows.Application/Tasks/TaskModel.cs` (`TrustedTask`), `TaskHandlers.cs`, `Triggering/WorkflowTrigger.cs` (`WorkflowInputs.Build`), `Definitions/crm_*.v1.json`, `Crm.Worker/Workflows/ConductorTaskPollingService.cs` | `TaskInputTrustApiTests` (sahte kiracı, sahte örnek kimliği, bitmiş/bilinmeyen yürütme, tür uyumsuzluğu, sahte rol kimliği, sahte HUMAN çıktısı, karar DB'den) |
| **H2, H3** | (kart tanımında açıklanmadı) | M5'te kapatılan başlıklarla eşlenir — doğrulanmalı | Kayıt kapalı (`Registration:Mode`), `deploy/`, Conductor yayınlanmaz | `PlatformApiTests`, `HostHardeningTests` |
| **H4** | Hesap modeli: yönetici parola seçiyor; mevcut hesap onaysız üye/yönetici oluyor; başka kiracı hesap verisi sızıyor; parola değiştirme yok; zayıf parola; `CreateOrganization` onaysız yönetici | **Burada düzeltildi** (a–f) | `Identity.Application/Members/MemberUseCases.cs`, `Me/AccountUseCases.cs`, `Platform/PlatformUseCases.cs`, `PasswordPolicy.cs`, `Domain/Users/User.cs`, `Domain/Memberships/Membership.cs`, `Infrastructure/Persistence/IdentityReadStore.cs`, `Shared.Web/Middleware/PasswordChangeRequiredMiddleware.cs`, migration `SecurityHardening` | `AccountModelApiTests`, `PlatformApiTests` |
| **M1, M4, M8** | (kart tanımında açıklanmadı) | M5'te kapatılan başlıklarla eşlenir — doğrulanmalı | `ForwardedHeadersSetup`, nginx CSP, `Docs:Enabled` | `HostHardeningTests` |
| **M2** | Kimliği doğrulanmış genel hız sınırı yok; istek gövdesi sınırsız | **Burada düzeltildi** | `Shared.Web/DependencyInjection/WebServiceCollectionExtensions.cs` (`GlobalLimiter`: kullanıcı + kiracı), `Crm.Api/Program.cs` (Kestrel `MaxRequestBodySize`, `UseRateLimiter` sırası), `RateLimitingOptions`, `RequestLimitsOptions` | `AuthenticatedRequests_AreRateLimited…`, `TenantLimit_IsShared…`, `KestrelRequestBodyLimit_…` |
| **M3** | Giriş: zamanlama farkıyla hesap keşfi, kilit/pasif bilgisi parola doğrulanmadan sızıyor, bilinen e-postayı sonsuz kilitleme | **Burada düzeltildi** (yaklaşım: bkz. backend.md §16.3) | `Identity.Application/Auth/AuthUseCases.cs` (`LoginHandler`), `Infrastructure/Security/TokenAndSecretServices.cs` (`VerifyDummy`, `LoginThrottle`), `Domain/Users/User.cs` | `Login_RevealsLockedOutOrDisabled_OnlyWhen…`, `Login_FromOneIp_CannotLockAKnownAccount…`, `Login_HasAnEmailKeyedRateLimitBucket…`, `UnknownUserVerification_…`, `LoginThrottle_KeysOnIpAndAccount…` |
| **M5** | Refresh token: kayan sınırsız ömür, atomik olmayan dönüşüm (yarış), eşzamanlı isteği hırsızlık sayma | **Burada düzeltildi** | `Identity.Domain/Tokens/RefreshToken.cs`, `Application/Auth/SessionIssuer.cs`, `AuthUseCases.cs` (`RefreshTokenHandler`), `Infrastructure/Persistence/Repositories.cs` (`TryRotateAsync`), migration `SecurityHardening` | `ConcurrentRefresh_…`, `ReusingARotatedToken_WithinTheGraceWindow…`, `…AfterTheGraceWindow…`, `RefreshFamily_HasAnAbsoluteLifetime…` |
| **M6** | İzin unutulan istekler fark edilmiyor | **Burada düzeltildi** | `Shared.Contracts/Security/Permissions.cs` (`AnyAuthenticatedUserAttribute`), tüm `[AnyAuthenticatedUser]` istisnaları (Me, GetOrganization, ListPermissions, GetEntityAudit, auth komutları, davetler, onay liste/tekil/özet, `CreateOrganization`) | `Crm.Tests.Architecture/RequestAuthorizationTests.cs` |
| **M7** | Delegeli yönetici yetki yükseltebiliyor (kendinde olmayan rol/izin, kendi rolü) | **Burada düzeltildi** | `Members/MemberUseCases.cs` (`DelegationGuard`, `UpdateMemberHandler`), `Roles/RoleUseCases.cs` | `DelegationAndCacheApiTests` |
| **M9** | Tek örnek / izin önbelleği bayatlığı | **Burada düzeltildi + belgelendi** (kod: değişiklikte anında geçersiz kılma, Redis yokken 2 dk; belge: runbook §9.1) | `InfrastructureServiceCollectionExtensions.cs`, `CachingDefaults`, `MemberUseCases.cs`, `AccountUseCases.cs`, `PermissionService.cs` | `RoleUpdate_TakesEffectImmediately…`, `MembershipChanges_…`, `AcceptingAnInvitation_…` |
| **L1, L9, L11** | (kart tanımında açıklanmadı) | Doğrulanmalı (M5 başlıkları ya da kapsam dışı) | — | — |
| **L2** | Aktivite `relatedName` okuma iznine bakmıyor | **Burada düzeltildi** (yer tutucu `***`) | `Activities.Infrastructure/Persistence/ActivityReadStore.cs` | `RelatedNameVisibilityApiTests` |
| **L3** | Fırsat sahibi kendi fırsatının onaylayıcısı olabiliyor | **Burada düzeltildi** (yalnız üye ise `no_approver`) | `TaskHandlers.cs` (`CreateApprovalsTaskHandler`), `Sales.Contracts/RecordLookup.cs` (`GetOwnerUserIdAsync`) | `DealOwner_IsExcludedFromTheApprovers`, `DealOwner_WhoIsTheOnlyApprover_FailsTheExecution…` |
| **L4** | JWT: algoritma sabitlenmemiş, `kid` sabit `crm-dev` | **Burada düzeltildi** | `Crm.Api/AuthenticationExtensions.cs` (`ValidAlgorithms=[RS256]`, parmak izi `kid`), `appsettings.json` | `Jwt_UsesRs256AndAKeyThumbprintKid_AndRejectsOtherAlgorithms` |
| **L5** | Parola hash yineleme sayısı düşük; giriş parolası sınırsız | **Burada düzeltildi** (210 000 PBKDF2-SHA512, girişte yeniden hash, login max 128) | `TokenAndSecretServices.cs` (`AspNetPasswordHasher`), `LoginValidator` | `PasswordHasher_UsesAtLeast210000Iterations`, `Login_UpgradesLegacyHashes_…`, `Login_RejectsOversizedPasswords_…` |
| **L6** | Sayfalama aritmetiği taşması → 500 | **Burada düzeltildi** | `Shared.Contracts/Paging/PagedQuery.cs` (`SkipFor`), `Shared.Web/Binding/PagedQueryBinder.cs`, `IdentityReadStore.cs` | `AbsurdPagingValues_Return400Validation…`, `ExtremeButRepresentablePagingValues_…` |
| **L7** | Firma web sitesi doğrulanmıyor (`javascript:` vb.) | **Burada düzeltildi** (yalnız yazmada; eski kayıtlar okunur) | `Sales.Application/Common.cs` (`OptionalHttpUrl`), `Accounts/AccountUseCases.cs` | `AccountWebsiteApiTests` |
| **L8** | `BuildLike` LIKE jokerlerini kaçışlamıyor | **Burada düzeltildi** (silinmedi, kaçışlandı) | `Shared.Infrastructure/Querying/GridQueryExtensions.cs` | `GridLikeEscapingTests` |
| **L10** | Kilitsiz bağımlılıklar, susturulmuş zafiyet uyarıları, kayan imaj etiketleri | **Burada düzeltildi** (web imajı hariç) | `packages.lock.json` (36 proje), `Directory.Build.props`, `.github/workflows/ci.yml`, `src/Crm.*/Dockerfile`, `deploy/docker-compose.prod.yml`, `infra/docker-compose.yml` | Linux'ta `docker build` (kilitli restore) + `dotnet restore --locked-mode` + zafiyet taraması temiz |

## Kabul edilen riskler (gerekçeli)

| Konu | Gerekçe / hafifletme |
|---|---|
| Access token (15 dk) iptal edilemez | İzinler her istekte veritabanı/önbellekten çözülür; pasifleştirilen üye ve rol değişikliği anında etkilenir. JWT'de `perm` claim'i **yoktur** (kart notu "var" diyordu; doğrulandı: yok) — token süresi izin bayatlığını uzatmaz. Parola değişince refresh aileleri kapanır; kalan access token ≤ 15 dk. |
| Giriş azaltma sayaçları bellek içi, kopyalar arası paylaşılmaz | Pilot tek `api` örneğidir (runbook §9.1). Çok kopyada Redis destekli paylaşımlı depo gerekir. Kalıcı koruma: veritabanındaki hesap kilidi + auth IP hız sınırı. |
| Çok IP'li (dağıtık) saldırgan bir hesabı 15 dk'lık aralıklarla kilitleyebilir | Kilit eşiği 10; tek IP+hesap eşiği 5 → tek IP kilitleyemez. Kilitliyken yanlış deneme kilidi uzatmaz; doğru parolalı kullanıcı yalnız beklemek zorundadır. Kademeli kilit artışı bilerek eklenmedi (kaybı büyütür). |
| Kilitli/pasif hesap **doğru parola** verene `locked_out`/`user_disabled` gösterir | Kart gereği: yalnız parolayı bilen kişi görür. |
| Bekleyen davet e-posta varlığını yöneticiye ifşa eder (yeni hesap → `temporaryPassword`, mevcut → `pending`) | Kart sözleşmesi bu ayrımı gerektirir; yalnız `org.users.manage` sahibi görür, hesap verisi (ad, kimlik) sızmaz. |
| E-posta ile parola sıfırlama/MFA yok | E-posta altyapısı yok (M5 açık işi); unutulan parola yönetici müdahalesiyle çözülür. |
| Sahte HUMAN çıktısı workflow'u başarısız yapabilir (karar yoksa) | Karar yazılmaz (veri bütünlüğü korunur); iş sonunda yürütme `decision_not_found` ile başarısız görünür, yönetici yeniden dener. |
| `web/Dockerfile` (node/nginx) etiketleri kayan | Kart kapsamı dışında (`web/` dokunulmaz); önerilen: `node:24.21.0-alpine`, `nginxinc/nginx-unprivileged:1.29.8-alpine` (Docker Hub'da doğrulandı). |

## Ertelenenler / açık işler

- **Merge sonrası `packages.lock.json`:** diğer kartların yeni projeleri (Ticaret/Servis/Pazarlama) kilit dosyasız gelir; Lead merge sonrası `dotnet restore Crm.slnx` çalıştırıp dosyaları commit etmeli, aksi hâlde CI/Docker `--locked-mode` kırılır. `TestFixture.cs`/`Permissions.cs` çakışmaları yalnız ekleme.
- Yeni modüller için: her yeni `ICommand/IQuery` `[RequiresPermission]` ya da gerekçeli `[AnyAuthenticatedUser]` taşımalı (mimari test zorlar); yeni controller'lar `[Authorize]`.
- Süresi dolmuş refresh token temizliği (KVKK; M5 açık işi), Conductor kimlik doğrulaması, e-posta ile davet/parola sıfırlama, MFA.
- Web tarafı: mevcut hesap davet ekranı (`/me/invitations`), geçici parola akışı (`mustChangePassword`), web sözleşmesi bu raporun kaynağı olan backend ile uyumludur (bkz. backend.md §5).

## Doğrulama (bu dalda çalıştırılanlar)

`dotnet build Crm.slnx --no-incremental` (0 uyarı / 0 hata), `dotnet format Crm.slnx --verify-no-changes`, `dotnet test Crm.slnx --no-build` (tüm modül + mimari + kiracı izolasyon testleri),
kendi veritabanı/portunda canlı duman testi (üye ekleme → geçici parola → parola değiştirme → davet kabul → eşzamanlı refresh → yeniden kullanım). Sonuçlar teslim mesajındadır.
