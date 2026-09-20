# C-X6 — SSO (tek oturum açma): analiz ve karar önerisi

Durum: analiz (kod yazılmadı). Tarih: 2026-09-20. Kaynak kodu okunan dal: `main` @ `f2b7378`. Dış olguların kaynağı ve doğrulama durumu §7'de. Yazar rolü: Spec/analist.

Okunanlar: `board.md`, `kararlar.md` (K1, K6, K13, K16, K18, K19), `backend.md`, `hardening-report.md`, `docs/plan/m9h-erisim.md`, `m8b-entegrasyonlar.md`, `m7-saas-hazirlik.md`; Identity kodu (`AuthUseCases.cs`, `SessionIssuer.cs`, `User.cs`, `Membership.cs`, `RefreshToken.cs`, `AuthController.cs`, `MemberUseCases.cs`, `PermissionService.cs`, `TokenAndSecretServices.cs`); web `store/auth.store.ts`, `services/auth.service.ts`, `pages/auth/*`. Kardeş repo `senseik`: SSO/OIDC kodu **yok**; yalnız "SSO/OAuth/SAML ilk sürüm kapsamı dışı (PO kararı K-6)" notu ve ATS için "Google girişi = SSO" ifadesi var (yeniden kullanılacak bir şey yok).

Belge boşlukları (varsayımlar): (1) `docs/analysis/x4-portal.md` bu dalda **yok** (portal hesaplarının ayrı kimlik türü olduğu görev tanımından varsayıldı). (2) `hardening-report.md` ana dalda **C-SEC2 satırı içermiyor**; parola adım-yükseltme (step-up), platform yöneticisi oturum sınırı gibi C-SEC2 maddeleri görev tanımından varsayıldı, kodda doğrulanmadı. (3) M9H ve M8B yalnız **plan belgesi** olarak var (board: M9H Backlog, M8B Ready); `sid`, `ISessionRevocationStore`, `login_events`, `manager_user_id`, `SsrfGuard`, AES-GCM sır zarfı henüz kodda yok.

---

## 0. Özet ve öneri

1. **Öneri: E (aşamalı hibrit), çekirdeği A (OIDC, CRM içinde bağlı-taraf/RP).** Önce OIDC; SAML 2.0 yalnız adı konmuş bir müşteri/IdP gerektirirse (Faz 2); SCIM ve tek-oturum-kapatma (SLO) Faz 3.
2. **İki katmanlı bağlantı modeli:** "IdP bağlantısı" (kim doğruluyor) ile "kiracı bağlaması" (hangi kiracı bu bağlantıya, hangi rol eşlemesiyle güvenir) ayrılır. Holdingin tek merkezî Entra/AD'si **platform düzeyi paylaşımlı bağlantı** olarak 250 kiracıya bağlanır; dış SaaS müşterisi **kiracı düzeyi** kendi bağlantısını getirir. Tek IdP öznesi = tek küresel `User` = N `Membership` (K1 ile birebir uyumlu).
3. **Güvenlik çekirdeği:** hesap bağlama asla e-postayla; `(issuer, sub)` (Entra'da `tid`+`oid`) anahtarı; e-posta ipucu yalnız **doğrulanmış alan adı** + `email_verified`/`xms_edov` ile; platform yöneticisi hesapları SSO dışı; zorlamalı SSO'da kiracı başına en çok 2 break-glass yerel yönetici; zorlamayı açmadan önce başarılı test girişi şart.
4. **Nokta atışı gerçek:** CRM'de yetkiler her istekte üyelikten çözülür ve refresh her seferinde üyeliği yeniden kontrol eder; yani "iptal etme" hazır. Eksik olan IdP'den **haber almak** (SCIM/back-channel/kısa SSO oturum ömrü). Faz 1'de SSO oturum ailesi mutlak ömrü 10 saate çekilir (öneri), Faz 3'te SCIM ile dakika düzeyine iner.
5. **Maliyet:** Faz 0 spike 1 mühendis-haftası (mh); Faz 1 ≈ 10–12 mh; Faz 2 (SAML) ≈ 6 mh; Faz 3 (SCIM + back-channel + yönetici eşleme) ≈ 6 mh; Faz 4 (IdP runbook'ları, sır/sertifika süresi uyarıları) ≈ 2 mh. Toplam ≈ 25 mh. Sert bağımlılık: **M8B** (egress + SSRF koruması + sır zarfı); yumuşak: **M9H** (`sid`, anlık iptal, giriş geçmişi).

---

## 1. Senaryolar ve öncelik

| # | Senaryo | Kim ister | Ne gerekir | Öncelik |
|---|---|---|---|---|
| S1 | **Grup çalışanı, merkezî kurumsal dizin** (holdingin tek AD/Entra'sı; 250 şirketin çoğu aynı dizinde) | Holding BT | Tek paylaşımlı IdP bağlantısı, çok kiracıya eşleme, grup→rol, kiracı seçici | **P0** |
| S2 | **Şirket başına farklı IdP** (bazı şirketler kendi AD FS/Keycloak/Okta'sında; aynı CRM kurulumu) | Şirket BT | Kiracı düzeyi bağlantı, kiracıya özel alan adı kanıtı, yalıtım | **P1** |
| S3 | **Dış SaaS kiracısı kendi IdP'sini getirir** (gelecek; M7) | Müşteri BT | Kendi kendine yapılandırma (kiracı yöneticisi), alan adı DNS kanıtı, plan kapısı (`sso`), SSRF güvenli keşif/metadata | **P1** (kapı: SaaS'a açılma kararı) |
| S4 | **Break-glass yerel yönetici** | Güvenlik/işletim | Zorlamalı SSO'da bile çalışan, denetlenen, en çok 2 yerel hesap; IdP arızasında erişim | **P0** (S1'in önkoşulu) |
| S5 | **Parolayla devam eden kullanıcılar** (SSO'suz şirketler, dış danışmanlar, gmail'li ortaklar) | Herkes | Parola girişi kalır; `Registration`/davet akışı değişmez | P0 (regresyon yok) |
| S6 | **API anahtarı / otomasyon / Worker / Conductor** | Entegratörler | **Etkilenmez.** Anahtar ayrı şema (`crmk_…`, oluşturan adına çalışır); zorlamalı SSO anahtara uygulanmaz. Yan fayda: oluşturan IdP'den kaldırılıp üyeliği pasifleşirse anahtar "kapsam ∩ oluşturanın izinleri" kuralıyla kendiliğinden ölür (M8B D10) | — |
| S7 | Platform yöneticisi (ürün/işletim) | İşletim | **SSO dışı** (yalnız yerel parola + C-SEC2 sınırları) | Karar D7 |
| S8 | Portal/partner hesapları (X4) | Partner | Ayrı kimlik türü; v1'de SSO dışı. Sonra aynı bağlantı çerçevesiyle B2B federasyon | P3 |
| S9 | Windows oturumuyla sessiz giriş (Kerberos/Entra Seamless SSO) | Çalışan | **CRM işi yok**: IdP tarafında bütünleşik kimlik doğrulama; CRM yalnız OIDC kodu alır | — |

e-Devlet: vatandaş/T.C. kimlik doğrulama sistemidir; işveren çalışan SSO'su için ilgisi **yok** (kaynak §7, doğrulama sınırlı). Ancak X4 portal analizinde vatandaş kimlik doğrulaması gerekirse orada ele alınır.

---

## 2. Protokol ve mimari seçenekleri

### 2.1 Seçenekler

| | Tanım | Ana özellik |
|---|---|---|
| **A** | Yalnız OIDC; kiracı başına IdP yapılandırması CRM Identity modülünde; CRM bağlı-taraf (RP) olur, doğrulamadan sonra **kendi JWT'sini** ve refresh ailesini üretir (`SessionIssuer`) | Mevcut oturum/RBAC/M9H aynen çalışır |
| **B** | A + SAML 2.0 SP | SAML-yalnız IdP'ler (bazı kamu/kurumsal, eski kurulumlar) |
| **C** | Kendi barındırdığımız broker (Keycloak) CRM önünde; CRM tek yayıncıya güvenir | Protokol/LDAP/Kerberos broker'da; CRM tek OIDC RP |
| **D** | Platform genelinde tek IdP, kiracı başına IdP yok | Yapılandırma işi (tek şema) |
| **E** | Aşamalı hibrit: D'nin sonucunu (platform paylaşımlı bağlantı) A'nın modeliyle **ilk teslim** yap, sonra kiracı bağlantısı, SAML/SCIM koşullu | A + B + SCIM, C dağıtım seçeneği olarak açık |

### 2.2 Puanlama (1 düşük, 5 yüksek; ağırlıklar analistin değerlendirmesidir)

| Ölçüt | Ağırlık | A | B | C | D | E |
|---|---:|---:|---:|---:|---:|---:|
| Senaryo kapsamı (S1–S4) | 25 | 3 | 5 | 5 | 1 | 5 |
| Çaba / ilk değere süre | 15 | 4 | 2 | 2 | 5 | 3 |
| Veri merkezinde işletilebilirlik | 10 | 4 | 3 | 2 | 5 | 4 |
| Lisans / maliyet | 10 | 5 | 5 | 5 | 5 | 5 |
| Kiracı yalıtımı / saldırı yüzeyi | 15 | 5 | 4 | 3 | 4 | 5 |
| Çok-org üyelik modeline uyum | 15 | 5 | 5 | 3 | 3 | 5 |
| SaaS'a hazırlık (kendi kendine IdP) | 10 | 4 | 5 | 3 | 1 | 5 |
| **Ağırlıklı (5 üzerinden)** | 100 | **4,15** | **4,20** | **3,45** | **3,15** | **4,60** |

Duyarlılık: A ile B baştan başa yakın; müşteri envanteri (spike) SAML-yalnız IdP gösterirse B öne geçer, göstermezse E'nin B'yi ertelemesi doğru kalır. C'nin puanını düşüren: yeni durumlu Java bileşeni (HA, yedek, yükseltme), kiracı yöneticisinin kendi IdP'sini CRM arayüzünden yönetememesi, IdP yapılandırmasının CRM dışına taşınması ve tüm kiracıların güveninin tek yayıncıda toplanması. **C dışlanmaz**: müşteri zaten Keycloak çalıştırıyorsa Keycloak, A'da sıradan bir OIDC IdP'dir (bağlantı olarak eklenir).

### 2.3 Neden CRM'in içinde, neden el yapımı akış

- Mevcut yapı: web token'ları JSON gövdeden alıp `localStorage`'a yazıyor (`auth.store.ts`), `SessionIssuer.Issue` tek oturum üretim yolu. SSO bu yolu **yeniden kullanır**; çerez tabanlı `AddOpenIdConnect` oturumu gerekmez.
- `Microsoft.AspNetCore.Authentication.OpenIdConnect` işleyicisi başlangıçta kayıtlı **statik** şemalar için tasarlıdır; kiracı/bağlantı başına çalışma zamanında değişen yapılandırma için `IOptionsMonitorCache` tuzakları gerekir. Öneri (analist yargısı): işleyiciyi kullanmadan, `Microsoft.IdentityModel.Protocols.OpenIdConnect` + `JsonWebTokenHandler` ile ince, testlenebilir bir kod akışı (yetkilendirme kodu + PKCE; kimlik jetonu doğrulaması bizde). Yeni bağımlılık çok az; PAR (RFC 9126) işleyicide .NET 9'dan beri var, ama gereksiz (§7).
- SAML için .NET'te yerleşik SAML **yok**; olgun seçenekler Sustainsys.Saml2 (MIT) ve ITfoxtec.Identity.Saml2 (BSD-3-Clause), ikisi de .NET 10'da çalışır (§7). Ticari ComponentSpace vb. gerekmez. Kendi XML imza doğrulamamızı **yazmayız**.

### 2.4 Çok-organizasyonlu üyelik modeli ve kiracı keşfi

Mevcut model: `User` küresel, e-posta benzersiz, `PasswordHash` zorunlu (`Guard.NotEmpty`); `Membership(tenant, user, role)` kiracı kapsamlı; giriş `DefaultTenantId`, yoksa ilk aktif üyelik.

Önerilen ek varlıklar (Identity, şema `identity`, tek migration, mevcut satırlar değişmez):

| Tablo | Kapsam | Ana alanlar |
|---|---|---|
| `sso_connections` | **küresel** (platform düzeyi) veya kiracıya ait (`owner_tenant_id`); `GlobalEntities` testine işlenir | `protocol` (`oidc`/`saml`), `issuer`, `client_id`, `client_secret_enc` (M8B AES-256-GCM zarfı; AAD = bağlantı+sürüm), `secret_expires_at`, `scopes`, `claim_map` (jsonb: e-posta/ad/ID/grup/rol talebi adı), `max_session_hours`, `allow_idp_initiated=false`, `status` (`draft`, `testing`, `active`, `disabled`), keşif/JWKS önbellek meta verisi |
| `sso_domains` | bağlantıya bağlı | `domain` (küçük harf, punycode), doğrulama: DNS TXT belirteci **veya** platform yöneticisi onayı, `verified_at`; bir alan adı bir bağlantıda doğrulanmış olabilir; **aynı bağlantı çok kiracıya bağlanabilir** |
| `tenant_sso_bindings` | kiracıya ait (`ITenantEntity`) | `connection_id`, `enforcement` (`off`/`optional`/`required`), `jit` (`off`/`mapped`/`default_role`), `default_role_id`, `role_mappings` (sıralı: grup/rol talebi → rol; ilk eşleşme kazanır), `sync_role` (IdP her girişte rolü günceller mi), `manager_claim?` |
| `federated_identities` | küresel | `connection_id`, `subject` (Entra: `tid:oid`; diğer: `sub`), `user_id` (FK, cascade), `linked_at`, `last_login_at`; benzersiz `(connection_id, subject)`; e-posta **anahtar değil**, yalnız son görülen değer |
| `sso_login_transactions` | kısa ömürlü (HybridCache/tablo, 10 dk, tek kullanımlık) | `state`, `nonce`, PKCE `code_verifier`, `connection_id`, `tenant_hint`, tarayıcı bağlama çerezi özeti |
| `memberships` (+) | mevcut | `source` (`manual`/`sso`/`scim`) — IdP yönettiği üyeliği elle yönetilenden ayırır; `sync_role` yalnız `sso/scim` kaynaklıya dokunur |
| `refresh_tokens` (+) | mevcut | `auth_method`, `connection_id`, `idp_sid`, `auth_time` (aile düzeyi; M9H `sid` = aile kimliği ile birlikte) |
| `users` (+) | mevcut | `auth_source` (`local`/`sso`), `has_local_password` (SSO kullanıcılarında `PasswordHash` **kullanılamaz sabit**: hiçbir parolayla eşleşmeyen işaretli değer) |

Kiracı keşfi (giriş ekranı):
1. `POST /auth/sso/discover { email?, organization? }` (anonim, hız sınırlı). E-posta alan adı **doğrulanmış** `sso_domains` içinde ise seçenekler döner. Alan adı-düzeyi yanıt: kullanıcı varlığı sızdırılmaz; bilinmeyen alan için "parola" seçeneği aynı biçimde döner.
2. **Aynı alan adı çok kiracıda** (holding: `@holding.com.tr` hepsinde) normaldir: alan adı bağlantıyı seçer, **kiracıyı** değil. Kiracı: (a) kullanıcının üyelikleri (`DefaultTenantId`, sonra seçici), (b) kiracıya özel giriş adresi `/t/{slug}` ile `organization` ipucu (kontrolü `tenant_sso_bindings` ile), (c) JIT için hedef kiracı yalnız ipucu + bağlama uygunluğuyla.
3. Kiracı ipucu **güvenilmez girdidir**: yalnız hangi bağlamanın uygulanacağını seçer; yetki hâlâ üyelik/eşleme sonucudur.

### 2.5 Sağlama (provisioning): JIT, SCIM, yalnız davet

| Yöntem | Ne yapar | Artı | Eksi | Öneri |
|---|---|---|---|---|
| **Yalnız davet** (mevcut `AddMember` + bekleyen davet) | Yönetici üye ekler; kullanıcı ilk SSO girişinde bağlanır | Sıfır yeni yüzey; mevcut kotayı/`DelegationGuard`'ı kullanır | 250 şirkette elle iş | Varsayılan (`jit=off`) |
| **JIT** | İlk girişte grup/rol talebinden üyelik açar | Sıfır yönetim işi | Herkese kapı açma riski; kota (`LimitKeys.Users`) tüketimi; rol bayatlığı | Yalnız `mapped` (eşleşen grup yoksa **red**) veya açık `default_role`; `ConsumesLimit` aynen; kota dolarsa nazik hata |
| **SCIM 2.0** (RFC 7643/7644) | IdP→CRM itme: oluştur/pasifle/grup | **Anlık pasifleme**, yönetici (manager) eşleme | Yeni anonim-dışı yüzey; Entra sağlama servisi bulut, DC'de CRM'e ulaşmak için **genel uç nokta veya şirket içi sağlama ajanı** gerekir (§7); ~3–4 mh | Faz 3 |

Lazy JIT: girişte yalnız (a) istenen kiracı ve (b) kullanıcının mevcut üyelikleri değerlendirilir; 250 kiracıya toplu yayılım **yok**.

### 2.6 Rol/grup → CRM rolü; hiyerarşi eşleme

- Eşleme **kiracı bağlamasında** tutulur (roller kiracıya özgüdür, K7). Girdi: `groups` veya `roles` talebi (Entra'da uygulama rolleri önerilir). Üyelik başına **tek rol** (K7) → sıralı eşleme, ilk eşleşme kazanır; eşleşme yoksa `default_role` veya red.
- **Administrator eşlemesi varsayılan kapalı** (kiracı bağlaması bunu açamaz; yalnız platform yöneticisi bayrağı ile). Gerekçe: IdP grup yönetimi = kiracı yönetici yetkisi; C-SEC M7 sınıfı yükseltme. Rezerve izinli roller (`org.access.manage`, `org.sso.manage`) eşlenemez. "Son aktif yönetici" değişmezi aynen.
- **Entra grup taşması (overage):** JWT'de 200, SAML'da 150 grup sınırı; aşılınca `groups` yerine `_claim_names/_claim_sources` (Microsoft Graph çağrısı gerekir). **v1: Graph yedeği yok**; yönerge: uygulama rolleri veya "yalnız uygulamaya atanmış gruplar". Taşma varsa eşleme yapılmaz → `default_role`/red (yapılandırılır) + günlükte uyarı. Şirket içi AD FS: güvenlik grupları `Group` talebiyle gelir (AD FS talep kuralı).
- **Hiyerarşi (`manager_user_id`, M9H):** OIDC kimlik jetonunda yönetici bilgisi **standart değil** (Entra'da Graph veya SCIM `manager` uzantısı gerekir). v1: yok; mevcut `PUT /organization/hierarchy` CSV/İK aktarımı sürer. Faz 3: SCIM `manager` → M9H doğrulamaları (döngü, derinlik ≤ 10); hata olursa satır atlanır ve raporlanır, giriş engellenmez. Yazan aktör "sistem (SSO/SCIM)", denetime düşer. IdP bir talep sağlıyorsa (Keycloak eşleyici, AD FS talep kuralı: `manager` e-postası) `manager_claim` opsiyonel.

### 2.7 Sağlamayı geri alma (deprovisioning) hızı

Bugünkü kod gerçeği: access token 15 dk, izinler her istekte `memberships`ten çözülür (önbellek değişimde geçersizlenir, Redis yokken ≤ 2 dk), `RefreshTokenHandler` her yenilemede `user.CanSignIn` ve aktif üyeliği kontrol eder. **CRM içinde iptal anlıktır; eksik olan sinyaldir.**

| Kanal | Gecikme | Kapsam | Faz |
|---|---|---|---|
| Kısa **SSO aile ömrü** (mutlak, öneri 10 sa; bağlantı başına 1–720 sa) | ≤ ömür | Tüm IdP'ler | 1 |
| Zorlamalı yeniden IdP kontrolü (`prompt=none` sessiz yenileme) | ≤ aralık | Üçüncü taraf çerez/iframe sorunları; **önerilmez** | — |
| IdP yenileme jetonunu saklayıp yenilemede IdP'ye sorma | ≤ IdP CAE gecikmesi | IdP sırrı saklamak (KVKK/sızıntı yüzeyi) + `offline_access`; **önerilmez** | — |
| **SCIM** `active=false` | IdP döngüsü (Entra artımlı döngü ≈ 40 dk, **doğrulanmadı**; isteğe bağlı sağlama var) | Üyelik pasif + aileler iptal (+ M9H `sid` iptali) | 3 |
| **Back-channel logout** (OIDC, `sid`) | Saniyeler | Keycloak/Okta/AD FS(?) destekler; **Entra desteklemiyor** (§7) | 3 |
| Front-channel logout (Entra) | Tarayıcı bağımlı | Yalnız kaydedilen `idp_sid`li aileyi sunucuda iptal edebilir (tarayıcı `localStorage`'ını temizleyemez) | 3 (isteğe bağlı) |

### 2.8 Zorlamalı SSO (kiracı başına) ve break-glass

- `enforcement=required`: kiracıya **yalnız SSO ile** oturum. `LoginHandler` bugün aktif üyelikleri sırayla dener; SSO zorlu kiracı **sıralamadan çıkar** (M7 "blocked" kiracı davranışı gibi atlanır). Tüm üyelikleri zorlu ise `auth.sso_required` (parola **doğruysa** söylenir, M3 ilkesi; SSO-yalnız hesapta parola doğrulanamayacağı için keşif adımı (§2.4) yönlendirir).
- `SwitchOrganization`: hedef kiracı zorlu ise ve oturumun `connection_id`'si o kiracının izinli bağlantılarında değilse → `403 auth.sso_required` (`args.connectionId`); SPA yönlendirme akışını başlatır. SSO oturumundan parola-only kiracıya geçiş serbest.
- **Break-glass:** kiracı başına ≤ 2 üyelik `sso_exempt=true`; yalnız platform yöneticisi **veya** kiracı yöneticisi (adım-yükseltmeyle) atar; yerel parola ≥ 14 karakter, denetim + giriş geçmişinde her kullanım işaretli, bildirim (M8A varsa) yöneticilere. Ek kurtarma: platform konsolunda "zorlamayı kaldır" (platform yöneticisi + C-SEC2 adım-yükseltme) ve Migrator komutu (mevcut `create-platform-admin` deseni).
- **Kilitlenme önlemi:** bağlama `required` yapılabilmesi için aynı yöneticinin o bağlantıyla **başarılı test girişi** (`testing` durumu) şart; `disabled` bağlantıda zorlama düşer (kiracı parolaya döner, denetim kaydı + yönetici uyarısı; PO kararı D9).

### 2.9 MFA devri ve adım-yükseltme (C-SEC2 yeniden yorumu)

- CRM'de MFA yok (K16, hardening "kabul edilen risk"). **SSO, MFA'ya pratik yoldur:** MFA IdP koşullu erişimine devredilir. Bağlama başına isteğe bağlı güvence: `required_amr` (örn. `mfa`) veya `required_acr`; sağlanmazsa red. Kimlik jetonundan `amr`, `acr`, `auth_time` oturuma yazılır ve access token'a `amr`/`auth_time` talebi eklenir. IdP'ler arası değer farkı büyük (Entra'da koşullu erişim kimlik doğrulama bağlamı gerekir): varsayılan **devret ve güven**, zorunlu talep opsiyonel.
- **Adım-yükseltme:** C-SEC2 parola adım-yükseltmesi yıkıcı platform eylemleri için; platform yöneticileri SSO dışı → **onlar için değişmez**. Kiracı düzeyinde benzer yıkıcı eylemler için (silme, rol/izin sertleştirme) SSO kullanıcısında yerel parola yoktur: yeniden yorum = **taze IdP kimlik doğrulaması**: `auth_time` ≤ 5 dk değilse `403 auth.step_up_required` (`args.method=sso`, `connectionId`); SPA `prompt=login` + `max_age=300` (SAML: `ForceAuthn=true`) ile yeniden yönlendirir; dönen kimlik jetonunun **yeni** `auth_time`'ı sunucuda doğrulanır (yalnız `iat` yeterli değil; refresh jetonu sessizce eski `auth_time` taşır, §7). Sonuç oturuma "adım-yükseltme bitişi" olarak yazılır. Entra'da `max_age`/`prompt` davranışı için doğrulanmış kaynak yok → spike.

### 2.10 Oturum ömrü hizası ve çıkış (SLO)

| Konu | Karar önerisi |
|---|---|
| Access token | 15 dk (değişmez) |
| SSO refresh ailesi | Mutlak ömür = `min(RefreshFamilyDays, bağlantı.max_session_hours)`; varsayılan 10 sa; kayan ömür yok (M5 kuralı) |
| Yenileme | Bizim refresh jetonumuz döner; IdP'ye gidilmez |
| Uygulama çıkışı | Yerel aile iptali (mevcut `LogoutCommand`); isteğe bağlı "IdP oturumunu da kapat" bağlantısı (RP-Initiated Logout 1.0 `end_session_endpoint`); **v1'de kimlik jetonunu saklamayız** → `id_token_hint` yok, yalnız `client_id` + `post_logout_redirect_uri` desteklenirse (IdP'ye göre; spike) |
| IdP'den çıkış | OIDC Back-Channel Logout (Final) Faz 3; **SAML SLO yapılmaz** (güvenilmez, XML yüzeyi büyütür) |

### 2.11 Giriş geçmişi (M9H) ve kilitleme mantığı

- `LoginHandler`'ın kilit sayacı yalnız **parola yoluna** aittir. SSO girişi `User.RecordSuccessfulLogin` çağrısıyla `FailedAccessCount`/`LockoutEndUtc`'yi **sıfırlamamalıdır** (aksi hâlde SSO parola kilidini siler) ve `CanSignIn` içindeki kilit kontrolü SSO yolunda **uygulanmaz** (yalnız `IsActive`); yani yeni `CanSignInViaSso`. `LoginThrottle` ve e-posta kovası SSO callback'te yok; SSO uçlarına IP başına `Auth` hız sınırı + bağlantı başına kova.
- `login_events` (M9H D16) `method` (`password`/`sso`) ve `connection_id` alır; başarılı SSO girişi ve organizasyon geçişi senkron yazılır; IdP tarafı başarısızlıklar CRM'e **ulaşmaz** (kayıt yok); callback'te doğrulama hatası (imza, nonce, alan adı) bilinen kullanıcıya atfedilebiliyorsa `sso_denied` (asenkron kanal, zamanlama etkisiz). Metrik `crm.auth.logins{outcome}` etiketi düşük kardinaliteli kalır: `method` ∈ {`password`,`sso`}; bağlantı/kiracı kimliği etiket **olmaz** (K20).

---

## 3. Güvenlik

### 3.1 Hesap bağlama kuralları (en yüksek risk)

| Durum | Karar |
|---|---|
| `(connection, subject)` kaydı var | Girişe izin (hesap `IsActive`, üyelik değerlendirmesi) |
| Kayıt yok; e-posta doğrulanmış (OIDC `email_verified=true` ya da Entra `xms_edov=true`) **ve** alan adı bu bağlantının doğrulanmış alan adı; yerel hesap **yok** | JIT kuralları (§2.5) → hesap oluştur (`auth_source=sso`) |
| Kayıt yok; **yerel hesap var** (pilot kullanıcıları: çok yaygın) | **Yerel parolayla bir kez onay** ("bağlama") **veya** yönetici ön-bağlama (davetteki e-postaya eşleme). Doğrulanmış alan + `email_verified` ile otomatik bağlama **varsayılan kapalı** (kiracı ayarı; PO kararı D6) |
| E-posta doğrulanmamış / eksik / alan adı kanıtsız | **Red** (`sso.email_unverified`); asla otomatik bağlama |
| Hesap `IsPlatformAdmin` | **Asla bağlanmaz**, SSO girişi reddedilir |
| Aynı e-posta, farklı bağlantı (ikinci IdP) | Ayrı `federated_identities` satırı için aynı ön koşullar tekrar; sessiz birleştirme yok |

Gerekçe: nOAuth (Descope, 2023-06): Entra'da `email` değiştirilebilir ve doğrulanmamıştı; herhangi bir Entra kiracısı yöneticisi hedef e-postalı jeton üretip e-postaya güvenen uygulamalarda hesabı devralabildi. Microsoft `xms_edov` ile önlem verdi; 2023 Haziran sonrası yeni uygulamalar doğrulanmamış e-postayı varsayılan **yaymıyor**. Buradan: kimlik anahtarı `tid`+`oid` (veya `sub`), e-posta yalnız ipucu.

**Alan adı sahipliği kanıtı:** kiracı düzeyi bağlantıda DNS TXT (`_crm-sso-verify.<alan> = <belirteç>`), belirteç kiracı+alan+rastgele; **ilk doğrulayan kazanır**, çakışmayı platform yöneticisi çözer. Platform düzeyi paylaşımlı bağlantıda alan adlarını platform yöneticisi onaylar (DNS kanıtı da istenir). Alan adı karşılaştırması ASCII küçük harf/punycode; e-posta normalizasyonu mevcut `ToUpperInvariant` ile tutarlı (Türkçe kültür kayması yok; IdP'den gelen e-posta ilk önce kırpılıp aynı `Normalize`'a girer).

### 3.2 Akış sertleştirmesi

| Tehdit | Önlem |
|---|---|
| **IdP karıştırma (mix-up)** | Her bağlantının **kendi** yönlendirme adresi (`/auth/sso/callback/{connectionId}`) ve işlem kaydına bağlı `connection_id`; yanıtta `iss` parametresi (RFC 9207) varsa tam eşleşme; kimlik jetonu `iss` == bağlantı yayıncısı (tam dize), `aud` == `client_id`, `azp` kuralı, `exp/iat/nbf` (kayma ≤ 60 sn), `nonce`, `alg` izin listesi {RS256, PS256, ES256} (`none`/HS* red), imza JWKS'ten. Entra'da `common`/`organizations` yayıncıları **desteklenmez**: kiracıya özel `https://login.microsoftonline.com/{tid}/v2.0` |
| **Kod enjeksiyonu / CSRF / oturum sabitleme** | `state` 256-bit, tek kullanımlık, sunucu tarafı işlem kaydı (10 dk); **PKCE S256** (RFC 7636; RFC 9700 zorunlu sayar); tarayıcı bağlama çerezi (`__Host-sso_tx`, `SameSite=Lax`, `HttpOnly`) ile "başkasının kodunu bana giydirme" (login CSRF) engellenir; yönlendirme adresi tam eşleşme; `returnTo` yalnız uygulama içi yol izin listesi |
| **SPA belirteç sızıntısı** | Kod/`state` URL'de SPA `/auth/sso/callback` rotasına gelir, hemen `POST /auth/sso/complete` ile sunucuya iletilip adres çubuğundan temizlenir; CRM belirteçleri **URL'de asla taşınmaz** (yanıt gövdesi, `Cache-Control: no-store`) |
| **Host başlığı zehirlenmesi** | Yönlendirme adresi `Sso:PublicBaseUrl` yapılandırmasından; `Host`/`X-Forwarded-Host`'tan türetilmez (müşteri ters vekili arkasında) |
| **SAML XML imza sarma / ayrıştırıcı farkı** (Faz 2) | Yalnız bakımlı kütüphane; **tek** ayrıştırıcı hem imzayı hem kimliği okur (imza doğrulama ile öznitelik çıkarımının ayrı XML kitaplıklarıyla yapılması 2026 CVE'lerinin ortak nedeni); işlenen `Assertion` **imzalı öğenin kendisi** olmalı, birden çok `Assertion` red; DTD/XXE kapalı; `InResponseTo`, `Destination`/`Recipient` (ACS), `Audience` (SP entityId), `NotBefore/NotOnOrAfter` (≤ 2 dk kayma), `Assertion ID` tekrar önbelleği; algoritma izin listesi (SHA-1 varsayılan red); IdP-başlatmalı (unsolicited) **kapalı**; bilinen sarma yükleriyle regresyon testleri; kütüphane sürümü `packages.lock.json`'da sabit, CVE izleme |
| **Metadata/sertifika dönüşümü** | JWKS: `kid` bilinmiyorsa hız sınırlı yenileme; SAML metadata: Worker'da zamanlı yenileme; **yeni imza sertifikası "bekleyen"** olarak eklenir ve yönetici onayıyla (çakışma penceresi) etkinleşir (metadata uç noktası ele geçirilirse otomatik güven olmaz) |
| **SSRF (keşif/JWKS/metadata/token uç noktası)** | Aşağıda §3.3 |
| **Bilgi sızıntısı** | Hata iletileri jenerik (`sso.failed` + `correlationId`); ayrıntı günlükte (yalnız bağlantı kimliği, sonuç); talep değerleri günlüğe **girmez** |

### 3.3 Egress, SSRF ve sırlar (M8B ile ilişki)

- Bugünkü ağ: `backend` `internal`; **yalnız Worker** egress-proxy/dns üzerinden çıkar (K19, M8B). OIDC kod akışı ise **API**'nin IdP'nin token/JWKS uç noktasına gitmesini ister. Seçenekler: (a) API'ye de aynı egress-proxy kimliği (öneri; proxy zaten hedef-IP ACL'li), (b) token değişimini Worker'a devretme (gecikme + iş kuyruğu karmaşası; önerilmez).
- **M8B `SsrfGuard` varsayılanı özel IP'leri engeller** (`AllowedPrivateCidrs` yalnız operatör). Müşterinin AD FS/Keycloak'u DC içinde özel IP'dedir → operatör bu CIDR'ları izin verir. Entra/Okta için internet çıkışı gerekir ve bu, K17'deki "veri merkezi dışına çıkış yok" güvencesini **daraltır** (yalnız tanımlı IdP host'larına, TLS, jeton içeriği ve PII yok; kimlik jetonu tarayıcıdan gelir, sunucu-sunucu yalnız kod/JWKS/keşif) → PO/DevOps kararı D14.
- **Kiracı yöneticisinin verdiği yayıncı URL'i güvenilmez girdidir:** şema yalnız `https`; IP değişmezi, `localhost`, iç ad soneklerini ret; çözümleme → sınıflandırma → IP'ye sabitleme (M8B akışı) — **genelleştirilmiş paylaşımlı** `Shared.Infrastructure/Egress` bileşeni gerekir (M8B'de webhook'a özel; ortak hâle getirme işi X6-E'de). Keşif belgesindeki `issuer` yapılandırılana **tam eşit** olmalı; token/JWKS uç noktaları da aynı korumadan geçer ve **yönlendirme izlenmez**. Kurum PKI'sı (özel CA) için mount edilen CA paketi gerekir (spike).
- **Sırlar:** OIDC `client_secret` (gerekirse `private_key_jwt` sonra) M8B AES-256-GCM zarfıyla (AAD: kiracı/bağlantı/sürüm), bir kez gösterilir, anahtar döndürme; `secret_expires_at` izlenir (Entra sırları süreli) ve süre dolmadan yönetici uyarısı (M8A varsa). SAML SP imza/şifreleme anahtar çifti bağlantı başına üretilir (özel anahtar şifreli), kamu sertifikası metadata'da.

### 3.4 Kiracı yalıtımı, denetim, KVKK, yetki

- **Yalıtım:** kiracı bağlamaları ve kiracı sahipli bağlantılar `ITenantEntity` (kiracı filtresi); platform bağlantıları küresel (`GlobalEntities`), yalnız platform yöneticisi yazar; kiracı yalnız **bağlandığı** platform bağlantısının **adını/durumunu** görür, sırrı/URL'yi görmez. Çapraz-kiracı testleri zorunlu: kiracı A yöneticisi B'nin bağlantı/alan/eşlemesini okuyamaz/yazamaz; başka kiracının alan adını doğrulayamaz.
- **Yetki:** yeni izin `org.sso.manage` (rezerve: yalnız `Administrator`, özel rollere verilemez, API anahtarına verilemez; M9H D18 deseni). Okuma `org.users.read`. Platform bağlantıları `[PlatformAdminOnly]`. `[RequiresPermission]`/`[AnyAuthenticatedUser("…")]` mimari testi yeni komutlara uygulanır. Plan kapısı: `sso` özelliği + `maxSsoConnections` (K18); iç plan (grup) sınırsız.
- **Denetim:** bağlantı/alan/bağlama/eşleme CRUD tam denetim (sırlar maskeli); her SSO girişi/başarısızlığı `login_events` + denetim; bağlama, JIT üyelik oluşturma, rol senkronu, break-glass kullanımı, zorlama açma/kapama **ayrı denetim satırı** (kim/ne zaman/hangi bağlantı).
- **KVKK (veri asgarileştirme):** yalnız `openid email profile` (+ eşleme açıksa `groups`/`roles`); **`offline_access` yok, Graph yok**, IdP access/refresh jetonu ve ham kimlik jetonu **saklanmaz**; saklanan: `(bağlantı, subject)`, e-posta/ad (zaten hesap alanları), eşlemenin **sonucu** (rol). IdP'den gelen öznitelik listesi runbook veri envanteri satırı; veri sorumlusu kiracı (aydınlatma metni onun). Hesap silme/imha: M7 `IdentityAccountEraser` `federated_identities` satırlarını (FK cascade) siler; kiracı imhasında bağlantı/bağlama/alan adı `ITenantDataEraser` adımıyla gider; SSO'dan çıkarılan kişinin hesabı denetim için kalır (üyelik pasif).
- **Platform yöneticisi hesapları:** SSO dışı (D7): bağlama, JIT ve eşleme `IsPlatformAdmin`'i ne verir ne bağlar; IdP talebiyle yükseltme yolu yoktur (M7 D5 ile tutarlı: yalnız Migrator).

---

## 4. Öneri ve yol haritası

### 4.1 Aşamalar (mühendis-haftası, tahmin ±30 %)

| Faz | İçerik | Kapsam | mh |
|---|---|---|---|
| **0** Spike | Müşteri IdP envanteri, ağ yolu, Entra test kiracısı + Keycloak konteyneri (+ varsa AD FS), `max_age`/logout davranışı, CA/PKI, e-posta/UPN eşlemesi | Analiz + küçük PoC | 1 |
| **1** Kurumsal OIDC | Bağlantı/alan/bağlama/kimlik modeli; OIDC kod+PKCE akışı; alan adı DNS kanıtı; hesap bağlama kuralları; JIT (`mapped`/`default_role`); zorlama + break-glass + test şartı; adım-yükseltme; SSO oturum ömrü; keşif ekranı + kiracı seçici + yönetici arayüzü; denetim/metrik | Backend ≈ 6,5–7, Web ≈ 3, DevOps/güvenlik incelemesi ≈ 1 | **10–12** |
| **2** SAML 2.0 (koşullu) | SP metadata/ACS, imza/assertion doğrulama sertleştirmesi, metadata yükleme/yenileme, sertifika bekleme/onay, regresyon yükleri, ayrı güvenlik incelemesi | Backend ≈ 4, Web ≈ 1, güvenlik ≈ 1 | ≈ 6 |
| **3** Sağlama ve çıkış | SCIM 2.0 Users(+Groups), yönetici (manager) eşleme, back-channel logout, front-channel (Entra), Keycloak/Entra SCIM koşum kılavuzu | Backend ≈ 5, Web ≈ 0,5, DevOps ≈ 0,5 | ≈ 6 |
| **4** İşletim | IdP runbook'ları (Entra/AD FS/Keycloak/Okta), sır/sertifika süresi uyarıları, metadata yenileme Worker'ı, DC ağ/CA rehberi | DevOps + Web | ≈ 2 |

Toplam ≈ 25 mh. Takvim: Faz 1 iki mühendisle ≈ 6–7 hafta. Faz 1 tek başına S1, S2 (yapılandırma), S4, S5'i karşılar.

### 4.2 Kartlara bölünme (Spec → Backend + Web)

| Kart | Kapsam | Bağımlılık |
|---|---|---|
| **X6-0** | Spike (Faz 0) | — (PO kararları paralel) |
| **X6-A** Identity SSO çekirdeği | Şema/migration, kimlik/alan/bağlantı/bağlama varlıkları, `org.sso.manage`, plan kapısı `sso`, `IdentityAccountEraser`/`ITenantDataEraser` genişletme, çapraz-kiracı testleri | **M7 merged**; sıcak dosya çakışması (`Permissions.cs`, `SystemRoleDefinitions.cs`) yalnız-ekleme |
| **X6-E** Egress genellemesi | M8B `SsrfGuard`/güvenli HTTP istemcisini `Shared.Infrastructure/Egress`'e ayır; API'ye egress-proxy yolu; CA paketi; sır zarfı yeniden kullanımı | **M8B merged** (sert) |
| **X6-B** OIDC akışı | Keşif/JWKS, işlem kaydı, `discover/start/complete`, kimlik jetonu doğrulama, bağlama/JIT/eşleme, `SessionIssuer` uzantısı (`auth_method`, `idp_sid`, `auth_time`, aile ömrü), `LoginHandler`/`SwitchOrganization` zorlama, kilit ayrımı | X6-A, X6-E |
| **X6-C** Zorlama ve adım-yükseltme | `enforcement`, break-glass, test şartı, `auth.step_up_required` | X6-B |
| **X6-D** Web | Keşif ekranı, kiracı seçici, callback rotası (`auth.store` uzantısı), bağlama onay ekranı, Ayarlar → SSO (bağlantı, alan adı DNS kanıtı, eşleme, zorlama, test), platform konsolu bağlantı ekranı | X6-B sözleşmesi (sahte API ile başlayabilir) |
| **X6-F** (Faz 2) SAML | §3.2 SAML | Faz 1 |
| **X6-G** (Faz 3) SCIM + logout | SCIM, manager eşleme, back-channel | Faz 1; **M9H merged** (`sid`, anlık iptal, `manager_user_id`, `PUT /organization/hierarchy`) |

M9H ile ilişki: Faz 1, M9H olmadan da teslim edilebilir (kısa SSO ömrüyle); M9H merge olunca `sid` iptali ve giriş geçmişi sütunları (`method`, `connection_id`) eklenir (küçük ek kart). Sıcak dosyalar: `ModuleCatalog.cs` değişmez (Identity genişler); `Permissions.cs`, `SharedResource*.resx`, `App.tsx`, `navigation.ts`, `i18n`, locale JSON yalnız ekleme.

---

## 5. Riskler ve spike gerektirenler

| # | Risk / bilinmeyen | Etki | Spike/aksiyon |
|---|---|---|---|
| R1 | Grup şirketlerinin **hangi IdP'yi** çalıştırdığı bilinmiyor (Entra/AD FS/Keycloak/Okta/kamu IdP'si; SAML-yalnız var mı) | B'nin ve C'nin gerekliliği | Müşteri envanteri anketi (şirket → IdP türü, OIDC desteği, MFA politikası, UPN≠e-posta) |
| R2 | **Ağ yolu:** DC'deki API → IdP (özel IP'li AD FS/Keycloak veya internetteki Entra) ve **kurum PKI**si (konteyner güven deposu); split-horizon DNS | Kod akışı çalışmaz | X6-0'da uçtan uca deneme; operatör CIDR/host izin listesi; CA paketi |
| R3 | Müşteri ters vekili arkasında **yönlendirme adresi/başlıklar** | Yönlendirme uyuşmazlığı, host zehirlenmesi | `Sso:PublicBaseUrl` zorunlu yapılandırma; vekil test |
| R4 | E-posta/UPN uyumsuzluğu (`.local` UPN, paylaşılan posta kutusu, e-postasız hesap) | JIT/bağlama başarısız | Talep eşleme (`email` vs `preferred_username`), yoksa red |
| R5 | Aynı alan adı çok kiracıda + tek dizin: **yanlış kiracıya** düşme | Yetki hatası | Kiracı seçici + bağlama uygunluğu + çapraz-kiracı testi |
| R6 | Entra grup taşması; AD FS grup talebi biçimi | Yanlış/eksik rol | Uygulama rolleri önerisi; taşmada güvenli varsayılan |
| R7 | **SAML güvenlik yüzeyi** (2025–2026'da çok sayıda XML imza CVE'si; .NET kütüphaneleri anılmadı ama risk sınıfı aynı) | Kimlik atlatma | Faz 2 koşullu; ayrı güvenlik incelemesi; yalnız bakımlı kütüphane |
| R8 | Mevcut yerel hesap sahipleri ve bağlama kullanılabilirliği; hesap devralma | Sosyal mühendislik, kilitlenme | Parola onaylı bağlama; yönetici ön-bağlama; e-posta altyapısı yok (M8A bekliyor) → e-posta doğrulama bağlantısı ile bağlama şimdilik **yok** |
| R9 | Sinyal gecikmesi (SCIM/back-channel yok): IdP'de kapatılan kullanıcı ≤ aile ömrü kadar içeride | Uyum/denetim | SLA kararı (D10), kısa ömür, Faz 3 |
| R10 | Sır/sertifika süresi dolması → toplu giriş kesintisi | İşletim | Süre izleme + uyarı + break-glass |
| R11 | `LoginThrottle` bellek içi/tek örnek; SSO callback hız sınırı çok kopyada zayıf | Kötüye kullanım | Pilot tek örnek (runbook §9.1); Redis'e taşıma ayrı iş |
| R12 | İstemci `localStorage` belirteç saklama (XSS'e açık) SSO ile değişmez; SSO sonrası oturum çalınması aynı sınıf | Kabul edilmiş mimari borç | Kapsam dışı, not |
| R13 | M8B/M9H henüz kodda yok; egress ve `sid` kayabilir | Takvim | Bağımlılık kapıları (§4.2) |

---

## 6. Ürün sahibinin vermesi gereken kararlar (öneri ile)

| # | Soru | Önerim |
|---|---|---|
| D1 | Yol: E (OIDC önce, SAML/SCIM koşullu) mu, C (Keycloak broker) mu? | **E.** Keycloak bir IdP olarak desteklenir; broker işletmeyi zorunlu kılmayız |
| D2 | İlk altın yol IdP'ler | **Entra ID + AD FS (OIDC) + Keycloak**; Okta genel OIDC ile; spike sonrası kesinleşir |
| D3 | SAML ne zaman? | Adı konmuş bir müşteri/IdP gerektirince (Faz 2); şimdi değil |
| D4 | Bağlantı sahipliği | Holding: **platform yöneticisi** paylaşımlı bağlantı; SaaS: **kiracı yöneticisi** (`org.sso.manage`, plan kapısı); kiracı platform bağlantısının sırrını göremez |
| D5 | JIT varsayılanı | **Kapalı**; açılırsa `mapped` (eşleşen grup yoksa red); "herkes girsin" yalnız bilinçli `default_role` + en düşük rol |
| D6 | Mevcut yerel hesabın SSO'ya bağlanması | **Yerel parolayla onay** veya yönetici ön-bağlama; otomatik bağlama (doğrulanmış alan+e-posta) **kapalı**, kiracı açabilsin mi? → hayır (v1) |
| D7 | Platform yöneticisi SSO'su | **Hariç**; yalnız yerel parola + C-SEC2 |
| D8 | Zorlamalı SSO ve break-glass | Kiracı yöneticisi açabilir; **test girişi şart**; kiracı başına ≤ 2 break-glass; platform yöneticisi kurtarma |
| D9 | Bağlantı `disabled`/silinince zorlama | Kiracı parolaya **döner** (denetim + uyarı) — yoksa kiracı kilitli kalır; ödünleşim: geçici zayıflama, break-glass ile karşılaştırılabilir |
| D10 | Geri alma SLA'sı | Faz 1 kabul: ≤ **10 sa** (SSO aile ömrü); hedef Faz 3: SCIM ile ≤ IdP döngüsü (dakikalar); daha sıkı (≤ 1 sa) isteniyorsa SCIM/back-channel öne çekilir |
| D11 | SSO oturum ömrü ve adım-yükseltme penceresi | Aile 10 sa (1–720 sa yapılandırılabilir), adım-yükseltme `auth_time` ≤ 5 dk |
| D12 | Administrator rolünün IdP grubundan atanması | **Kapalı**; Administrator yalnız elle/yerel |
| D13 | SLO kapsamı | Yerel çıkış + isteğe bağlı IdP çıkış bağlantısı; back-channel Faz 3; **SAML SLO yok** |
| D14 | Ağ: API'nin IdP'ye çıkışı (egress-proxy + operatör IdP host/CIDR izin listesi) DC/KVKK politikasınca kabul mü? | Kabul, **yalnız tanımlı IdP host'ları**, varsayılan kapalı (`Sso:Enabled=false`) |
| D15 | Yönetici (manager) hiyerarşisini IdP'den eşleme | Faz 3, SCIM ile; o zamana kadar mevcut toplu aktarım |
| D16 | Yerel MFA (TOTP) eklensin mi? | **Hayır**; SSO kullanan kiracılar için MFA IdP'ye devredilir; SSO'suz kiracılar için ayrı kart sonra |
| D17 | SSO'yu SaaS plan özelliği yapma | Evet (`sso` özelliği + `maxSsoConnections`); grup (`internal`) sınırsız |

---

## 7. Dış olgular: kaynak, tarih, doğrulama (erişim: 2026-09-20)

| Olgu | Kaynak | Doğrulama |
|---|---|---|
| Sustainsys.Saml2 MIT lisanslı (2.x), .NET 10'a kadar kararlı; ticari destek paketleriyle geliştiriliyor; v2.12 izleniyor (Şubat 2026) | github.com/Sustainsys/Saml2, nuget.org, saml2.sustainsys.com (arama özeti) | Kısmen (arama özeti; paket sayfası ayrıca açılmadı) |
| ITfoxtec.Identity.Saml2 BSD-3-Clause, .NET 10 desteği, ECDSA imza | github.com/ITfoxtec/ITfoxtec.Identity.Saml2, nuget.org (arama özeti) | Kısmen |
| ASP.NET Core OIDC işleyicisi .NET 9'dan beri PAR (RFC 9126) destekli, `PushedAuthorizationBehavior` seçeneği | Microsoft Learn/ASP.NET Core 9 yazıları, auth0.com blog (arama özeti) | Kısmen |
| Entra `sub` uygulama başına ikili (pairwise), `oid` kararlı; çok kiracılı için `tid`+`oid` önerilir; `xms_edov` isteğe bağlı talep; yeni uygulamalar doğrulanmamış e-postayı varsayılan yaymıyor | learn.microsoft.com "ID token claims reference", "Migrate away from using email claims…" (arama özeti) | Kısmen (Microsoft Learn özeti) |
| nOAuth: Descope, 2023-06-20 açıklandı; e-posta talebi değiştirilebilir/doğrulanmamış; Microsoft Haziran 2023 önlemleri | descope.com/blog/post/noauth, semperis.com, bleepingcomputer.com | Kısmen (birden çok kaynak uyumlu) |
| Entra: 200 (JWT) / 150 (SAML) grup sınırı, taşmada `_claim_names`+Graph | learn.microsoft.com (grup talepleri, id/access token claims reference) | Kısmen |
| Entra sağlama: SCIM 2.0 uç noktası, şirket içi uygulama için sağlama ajanı (Windows Server 2016+, ≥3 GB RAM, Microsoft'a giden bağlantı) | learn.microsoft.com "on-premises app provisioning to SCIM-enabled apps" | Kısmen |
| Entra artımlı sağlama döngüsü ≈ 40 dk | Yazarın hatırası | **Doğrulanmadı** |
| Entra OIDC **back-channel logout desteklemiyor**, yalnız front-channel | learn.microsoft.com Q&A (soru-cevap; resmî sayfa değil) | **Zayıf**; spike'ta doğrulanacak |
| OIDC Back-Channel Logout 1.0 ve RP-Initiated Logout 1.0 **Final** | openid.net/specs (arama sonucu) | Kısmen |
| AD FS OIDC yalnız 2016 ve sonrası (2016/2022/2025); AD FS resmen EOL değil, Microsoft Entra'ya geçişi öneriyor | learn.microsoft.com Windows Server AD FS OIDC, Q&A (arama özeti) | Kısmen |
| Keycloak Apache-2.0, CNCF incubating, LDAP/AD federasyonu + kimlik brokerlığı; 26.7 (2026-07-09) yerel SCIM API'yi **preview**'a aldı (varsayılan kapalı) | keycloak.org, skycloak.io blog (arama özeti) | Kısmen |
| RFC 9207 (`iss` yanıt parametresi, mix-up); RFC 9700 (OAuth 2.0 güvenlik BCP: PKCE zorunlu, ImplicitFlow/ROPC kaldırıldı) | rfc-editor.org, datatracker.ietf.org | Kısmen |
| OIDC `max_age`/`prompt=login`/`auth_time`/`acr`/`amr` ile adım-yükseltme; refresh yenilemesi `auth_time`'ı taşır | OIDC Core; blog yazıları (arama özeti) | Kısmen; **Entra'da `max_age` desteği doğrulanmadı** |
| 2025–2026 SAML CVE'leri: Ruby-SAML (CVE-2025-25291/25292, 66567/66568), PHP-SAML, Authentik CVE-2026-25922, OneUptime CVE-2026-34840; PortSwigger "SAML Roulette" (Black Hat Europe, 2025-12); ortak neden: ayrıştırıcı tutarsızlığı, kısmen imza doğrulama, imza ve kimlik çıkarımının ayrık işlenmesi. **.NET kütüphaneleri anılmadı** | workos.com/blog/saml-vulnerabilities-2026, portswigger.net/research | Kısmen |
| SCIM 2.0 = RFC 7643/7644 | Standart bilgisi | Bu oturumda ayrıca doğrulanmadı |
| e-Devlet Kapısı vatandaş kimlik doğrulaması (şifre, e-imza, mobil imza, T.C. kimlik kartı, internet bankacılığı); özel sektör çalışan SSO'su için bir entegrasyon yolu **bulunamadı** | turkiye.gov.tr, giris.turkiye.gov.tr | Sınırlı (özel sektör senaryosu doğrulanamadı; Alo 160'a sorulmalı) |

Notlar: "arama özeti" = yanıtın özeti web aramasından, birincil sayfa tam okunmadı. Lisans hukuki değerlendirmesi ve sürüm sabitleme (`packages.lock.json`, L10 kuralı) uygulama kartında yapılmalı.
