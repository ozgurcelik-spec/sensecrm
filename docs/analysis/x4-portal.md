# C-X4 — Müşteri / partner portalı: analiz ve karar önerisi

Durum: analiz (kod yok). Tarih: 2026-09-20. Kart: C-X4 (`docs/team/board.md`). Kapsam: grup şirketlerinin müşterilerine, B2B kişilerine ve iş ortaklarına dışa dönük self-servis yüzeyi. Mimari resimde ("Kullanıcılar: Müşteriler / İş Ortakları (Portal / API)", "Frontend blok 1: Müşteri / Partner Portal") öngörülmüştür; bugün kodda **hiç portal yüzeyi yoktur** (`Portal` kelimesi `src/` altında geçmez; M6B §2.2 "müşteri kimliği/portalı yoktur", M9I D8/D10 "anonim/portal yüzeyi bu kartta yoktur").

Kaynak işareti: **[K]** kod/plan belgesinde doğrulandı (yol verilir), **[W]** web araştırması (tarih 2026-09-20), **[?]** doğrulanmadı / hukuki teyit gerekir.

## 0. Özet öneri

1. **Seçenek E (aşamalı hibrit) = B'nin aşamalı teslimi:** aynı monolit içinde ayrı `Portal` modülü + ayrı portal SPA + ayrı kimlik türü (portal hesabı ≠ `Identity.User`) + ayrı token/anahtar/köken; `Api:Surface` yapılandırmasıyla **aynı imaj, ayrı konteyner** olarak çalışabilir (D'ye geçiş yapılandırma anahtarıdır, yeniden yazım değildir).
2. **İlk sürüm salt "destek portalı":** talepleri izleme + yorum, talep açma, bilgi tabanı, kendi bilgilerini güncelleme, KVKK başvurusu. Ticari belgeler (teklif/sipariş/fatura) ikinci aşama; iş ortağı/tedarikçi üçüncü aşama ve **iş ortağı programı var mı** sorusuna bağlı.
3. **Güvenlik çıtası:** yalnız davetle etkinleşme (kendi kendine kayıt yok), parola + isteğe bağlı TOTP (e-posta bağlantısı tek başına kimlik doğrulama sayılmaz), portal tokenı staff tokenından yapısal olarak farklı, her portal verisi **portal için tasarlanmış DTO + kapsamlı okuma portu** üzerinden (staff DTO/handler yeniden kullanılmaz), yetkisiz kayıt her zaman tekdüze 404.
4. **KVKK:** her kiracı (grup şirketi) kendi müşterisinin **veri sorumlusu**; platformu işleten birim veri işleyendir; portal hesabı kiracıya özgüdür (aynı kişi iki şirkette iki ayrı hesap). Hizmet sunumu için açık rıza değil aydınlatma; kişi bazlı silme/erişim bugün **yok** (yalnız kiracı imhası M7) → portal öncesi kapatılmalı.
5. **Efor:** MVP (Faz 1) ≈ 22–26 mühendis-haftası; Faz 2 ≈ 9–11; Faz 3 ≈ 6–8. Bağımlılıklar: M8A, M8C, M9I, M9G (hard), M9H (koordinasyon), M6A/M9C (Faz 2).

## 1. Portal kullanıcıları ve senaryolar

### 1.1 Kullanıcı tipleri

| Tip | Kim | Sistemdeki karşılığı | Bağlanacağı nesne | Risk profili |
|---|---|---|---|---|
| **Son müşteri** (B2C) | Grup şirketinin bireysel müşterisi | `Contact` (firmasız, `AccountId = null`) [K: `Contact.cs`] | Kişi | Yüksek hacim, düşük güven; spam/hesap ele geçirme |
| **B2B kişi** | Müşteri firmanın çalışanı | `Contact` + `Account` | Kişi (+ firma, yalnız yetkiliyse) | Aynı firmanın kişileri arasında kayıt paylaşımı sorusu |
| **İş ortağı / bayi** | Satış kanalı | Yok: `Account`ta tür alanı yok [K: `Account.cs`: Name, Industry, Website, … tür yok] | Firma + kişi (yeni "ilişki türü" gerekir) | İş kuralı yoğun (kayıt koruma süresi, çakışma) |
| **Tedarikçi** | Satın alma emri karşı tarafı | M9C `Vendor` (kişi/hesap değil) [K: m9c D10, PO `vendorId`] | Vendor | Düşük hacim, finansal içerik |

Grup şirketi gerçeği: kiracı = şirket (K1). Bir kişi iki grup şirketinin müşterisi olabilir; K4 (grup konsolidasyonu yok) ve KVKK gereği bu kişi **her kiracıda ayrı portal hesabına** sahip olur (bkz. §3.3).

### 1.2 Senaryolar: değer × risk sıralaması

Değer 1–5 (müşteri memnuniyeti + temsilci iş yükü azalması), risk 1–5 (veri sızıntısı, kötüye kullanım, hukuki bağlayıcılık). Faz önerisi son sütun.

| # | Senaryo | Değer | Risk | Bağımlılık / not | Faz |
|---|---|---|---|---|---|
| 1 | Taleplerini izle + herkese açık yorum ekle | 5 | 2 | M6B; yorum `visibility` zaten `public|internal` [K: `CaseEnums.cs`] | 1 |
| 2 | Talep aç (konu/açıklama/ek) | 5 | 3 | Spam, ek yükleme; M9G atama kuralları (`origin=portal`) | 1 |
| 3 | Bilgi tabanı gez/ara + talep açarken öneri | 4 | 1 | M9I `ICustomerSolutionCatalog` [K: m9i D8, §Bağımlılıklar "C-X4 girdisi"] | 1 |
| 4 | Kendi iletişim bilgisini güncelle | 3 | 3 | KVKK m.11 düzeltme hakkı; e-posta değişimi ayrı akış | 1 |
| 5 | KVKK başvurusu / veri dışa aktarma | 3 | 3 | Yasal zorunluluk (§3.10); kişi bazlı imha yok | 1 |
| 6 | Teklif görüntüle, kabul/red | 4 | 4 | M6A/M9C; bağlayıcılık [?]; onaylı tutarın değişmezliği | 2 |
| 7 | Sipariş/fatura görüntüle, ödeme durumu | 4 | 4 | M9C `paid/partiallyPaid/overdue` türetilir [K]; PDF üretimi henüz yok (K16 "dışa aktarma sonra") | 2 |
| 8 | Belge indir (sözleşme, fatura PDF) | 3 | 3 | M9I klasör paylaşımı portala **açılmaz** [K: m9i]; belge ACL'si yeni | 2 |
| 9 | İş ortağı fırsat/lead kaydı (deal registration) | 4* | 4 | Program tasarımı (koruma süresi, çakışma, onay: M4 akışı) | 3 |
| 10 | Tedarikçi PO onayı / teslim tarihi | 2 | 3 | M9C PO; düşük hacim | 3 |
| — | Müşterinin kendi sistemi için API | 2 | 4 | M8B anahtarları **oluşturan personel gibi davranır** [K: backend §24.2] → müşteriye verilemez; portal-kapsamlı anahtar gerekir | ≥3 |

\* yalnızca gerçek bir bayi kanalı varsa.

## 2. Mimari seçenekler

- **A — Portal yok:** web formları (M9G, hedef bugün yalnız `lead`), bildirimler (M8A alıcı yalnız aktif üye [K: m8a D6]), Open API (M8B).
- **B — Modül içi ayrı portal:** `Sense.Crm.Modules.Portal.*` + ayrı SPA + ayrı kimlik/yetki modeli.
- **C — Personel uygulaması + kısıtlı rol:** dış kullanıcı `User` + `Membership` + düşük izinli rol.
- **D — Ayrı dağıtılabilir servis:** kendi süreci, kendi veri erişimi (olay/okuma kopyası/iç API).
- **E — Aşamalı hibrit:** Faz 0 = A'nın iyileştirmesi; Faz 1+ = B, `Api:Surface` ile ayrı konteyner; D yalnız kanıtlanmış ihtiyaçta.

Neden C elenir (kodla): `User` küreseldir, girişte `DefaultTenantId` ile kiracıya düşer, JWT'ye `roles`, `platform_admin`, `perm` çözümü ve `tid` yazılır [K: `User.cs`, `SessionIssuer.cs`, `RequestContextMiddleware.cs`]; e-posta sistem genelinde benzersizdir; bir dış kullanıcı staff uçlarının tümüne aynı şemayla kimlik doğrulanır ve yalnız izin anahtarlarına güvenir. Yüzlerce staff DTO'su/handler'ı bir izin unutulmasına karşı tek savunma hattı olur (C-SEC M6 "izin unutulan istek" testi bunu kısmen yakalar, DTO alan sızıntısını yakalamaz). Ayrıca çok kiracılı üyelik: dış kişi başka kiracıya davet edilerek staff arayüzünde "organizasyon değiştir" listesine girer.

Neden D bugün fazla: portal yazma işlemleri (yorum, teklif kabulü) modül aggregate'lerine yazar; ayrı servis ya bu yazmalar için yeni bir iç API + servis kimliği + olay senkronu ya da aynı veritabanına doğrudan bağlantı gerektirir (o zaman izolasyon kazancı yarıya düşer). Tek ekip için CI, migration, sır yönetimi, gözlemlenebilirlik (K20), runbook ikiye katlanır.

### 2.1 Puanlama (1 kötü – 5 iyi; ağırlıklı ortalama)

| Kriter (ağırlık) | A | B | C | D | E |
|---|---|---|---|---|---|
| Değer kapsamı (20) | 1 | 5 | 3 | 4 | 4* |
| Kimlik/veri izolasyonu (20) | 5 | 4 | 1 | 5 | 5 |
| Patlama yarıçapı (15) | 5 | 3 | 1 | 4 | 4 |
| Efor (15) | 5 | 2 | 4 | 1 | 3 |
| Veri merkezi işletimi: ayrı köken, CSP, hız sınırı, WAF (15) | 4 | 3 | 4 | 2 | 3 |
| KVKK/denetim netliği (10) | 3 | 4 | 1 | 4 | 5 |
| Genişleyebilirlik (5) | 2 | 4 | 1 | 4 | 5 |
| **Ağırlıklı ortalama** | **3,70** | **3,60** | **2,30** | **3,45** | **4,05** |

\* E'nin değeri aşamalıdır (Faz 1 destek, Faz 2 ticaret); B ile aynı kod, farkı: salt-okunur/küçük yüzeyle başlama, `Surface` ayrımını baştan kurma ve KVKK önkoşullarının kapı olarak konması. Puanlar öznel ve A ile E arasındaki fark küçüktür (3,70 / 4,05): "değer kapsamı" ağırlığı düşürülürse A öne geçer, yani sonucu **portal talebinin gerçekliği** (karar 1) belirler. A'nın Faz 0 iyileştirmeleri E'ye zaten dahildir.

### 2.2 Blast radius ve işletim (B/E)

| Konu | Karar |
|---|---|
| Süreç | Aynı imaj, `Api:Surface = staff | portal | public` (M9G `PublicEndpoints:Enabled` benzeri; yalnız ilgili controller'lar eşlenir). Faz 1'de iki konteyner: `api` (staff) ve `api-portal`. Portal seli staff havuzunu/`GlobalLimiter`ını tüketemez. |
| Veri erişimi | Aynı veritabanı; **`crm_portal` ayrı DB rolü** yalnız portal ports'un okuduğu şema/tablolara SELECT + `portal` şemasına yazma (INSERT yorum/talep gibi yazmalar modül portları üzerinden, o rol için ayrı grant). Bu, D'nin "veritabanı izolasyonu" kazancının ucuz kısmıdır [?] (migrator/`db-init` değişikliği gerektirir; spike S2). |
| Köken | `portal.<alanadı>` (staff'tan **farklı site**; çerezler, CSP, CORS ayrı). Staff hostu ve portal hostu birbirinin rotalarını `404` verir (Host denetimi; M9G D21 deseni [K]). |
| nginx | K17 "yalnız web 127.0.0.1'de" bozulmaz: yeni `portal-web` konteyneri **ayrı loopback portu** (ör. 127.0.0.1:8081); yalnız `/api/v1/portal/**` ve SPA statiği iletilir, `/api/v1/*` geri kalanı, `/scalar`, `/metrics` **iletilmez**. M9G'nin `publicforms` profiliyle tek `external` profilinde birleştirilebilir. |
| Müşterinin vekili/WAF'ı | İlk kez **internete bakan** yüzey (bugünkü pilot kurum içi): vekil DMZ'de yayınlar. Beklenen: TLS + HSTS vekilde, `X-Forwarded-For` tek sıçrama (`TRUSTED_PROXY_CIDR`, K17), IP başına bağlantı/istek sınırı vekilde ve uygulamada (çift), JSON gövde sınırı 1 MB (yükleme rotası hariç 10 MB), WAF (ör. OWASP CRS) paranoya 1–2, talep açıklaması/yorum alanı için kural istisnası, ölü bağlantı zaman aşımı. Bu maddeler runbook'a yazılır (Faz 1 DevOps). |
| CSP | Staff'takiyle aynı (`default-src 'self'`, harici font/CDN/CAPTCHA yok) + `frame-ancestors 'none'`. Mantine `style-src 'unsafe-inline'` ihtiyacı [K: `security-headers.conf`] portalda nonce ile sıkılaştırılabilir [?] (spike). |

## 3. Güvenlik ve KVKK

### 3.1 Kimlik türü ayrımı

`portal.portal_accounts` (**kiracıya özgü** `TenantEntity`; `Identity.User` değil):

| Alan | Not |
|---|---|
| `id`, `tenant_id`, `normalized_email` | `(tenant_id, normalized_email)` benzersiz |
| `subject_kind` (`contact`\|`vendor`), `subject_id` | Müşteri/iş ortağı → `Contact`; tedarikçi → `Vendor`. Kişi başına en çok bir **canlı** portal hesabı |
| `audience` (`customer`\|`partner`\|`supplier`) | Hangi senaryo kümesi açık (personel atar) |
| `portal_role` (`member`\|`account_admin`) | Firma kapsamı yalnız `account_admin` |
| `status` (`invited`\|`active`\|`disabled`) | Kilit `lockout_end_utc` ayrı |
| `password_hash?`, `security_stamp`, `failed_count`, `lockout_end_utc`, `last_login_at`, `locale` | Aynı PBKDF2 hasher soyutlaması (`IPasswordHasher`) yeniden kullanılır; **tablo/varlık ayrı** |
| `totp_secret_enc?`, `mfa_enabled` | Faz 2 |
| `terms_version`, `terms_accepted_at`, `invited_by_user_id` | KVKK/denetim |

Bilinçli farklar: (a) hesap **kiracıya özgü**, (b) `MustChangePassword`/geçici parola yok (parolayı hiç personel görmez), (c) platform yöneticisi, rol adı, izin anahtarı kavramı yok, (d) `Membership` yok. Personel ve portal kimlikleri **hiçbir tabloda birleşmez**; bir kişi hem personel hem müşteri ise iki ayrı kimlik alır.

### 3.2 Kayıt, davet, etkinleştirme

- **Kendi kendine kayıt kapalı** (K17 `Registration:Mode` mantığı). Sahte hesapla firma kişisini taklit (pre-hijacking), e-posta squatting ve toplu hesap açma engellenir. İleride "erişim iste" formu yalnız personel kuyruğuna kayıt düşer, hesap açmaz.
- Akış: personel (`crm.portal.manage`) kişi kaydında **"Portala davet et"** → sunucu `portal_invitations` (belirteç 32 bayt rastgele, yalnız SHA-256 özeti saklanır [K: API anahtarı deseni], 7 gün ömür, tek kullanımlık, kişiye bağlı, hesap oluşturulmaz) → e-posta **yalnızca kişi kaydındaki adrese** (istekte adres alınmaz) → `https://portal…/t/{slug}/activate#<belirteç>` (belirteç URL **parçasında**: sunucu günlüğüne, `Referer`'a, e-posta bağlantı tarayıcılarına düşmez) → sayfa belirteci **POST ile** tüketir (bağlantı önizleme/tarayıcı botlarının tek kullanımlık belirteci yakması bilinen sorun: [W] better-auth #6985, supabase #41618, topluluk raporları) → parola belirle (sunucu politikası), aydınlatma metni sürümünü onayla → hesap `active`, oturum aç.
- E-posta yoksa (M8A henüz yok / SMTP kapalı, varsayılan): personel **tek seferlik etkinleştirme bağlantısını** arayüzde görür ve kişiye başka kanaldan iletir (bugünkü geçici parola modeli [K: `AddMemberHandler`] ile aynı ilke, ama parola değil bağlantı).
- Etkinleştirme hataları tekdüze ("bağlantı geçersiz/süresi dolmuş"); yeniden gönderme yeni belirteç üretir, eskisini iptal eder.
- Kişi e-postası personelce değişirse portal hesabının e-postası **otomatik değişmez**; portal e-posta değişimi yeni adres doğrulaması + mevcut parola + eski adrese bildirim ister. Kişi silinirse (yumuşak silme) hesap `disabled`, tüm oturumlar iptal.

### 3.3 Kiracı çözümü (kimlik doğrulama öncesi)

Personelde kiracı URL'de taşınmaz, token'dan gelir [K: `RequestContextMiddleware`]. Portalda giriş/etkinleştirme öncesi kiracı gerekir:

| Seçenek | Artı | Eksi | Karar |
|---|---|---|---|
| E-postadan kiracı bulma (tek portal) | Kullanıcı dostu | Hesap varlığı sızar; aynı e-posta çok kiracıda | Reddedilir |
| **Yol öneki `portal…/t/{slug}`** | Tek hostname, tek sertifika, vekil işi yok | Slug tahmin edilebilir (düşük risk: kiracı yoksa/portal kapalıysa tekdüze 404) | **MVP** |
| Kiracı başına alt alan adı | Marka/çerez ayrımı | Joker DNS/sertifika = müşteri vekilinde iş | Faz 2, `portal_hosts` eşlemesiyle |

Kurallar: slug yalnız giriş/etkinleştirme/sıfırlama gövdesine ve SPA rotasına girer; verilen token'dan sonra kiracı **yalnız token `tid`**'inden alınır (URL'deki slug ile çelişirse 404). Kiracı: aktif, `portal_settings.enabled = true` (varsayılan **kapalı**), M7 erişimi `none` değilse; aksi halde tekdüze 404. Kiracı salt-okunur/askıdaysa portal **yazmaları** `403 tenant.suspended` (K18 ile aynı) — okuma açık. Plan kapı modülü `portal`; **portal hesapları personel kullanıcı limitine sayılmaz**, ayrı `maxPortalAccounts` (yumuşak/sert kararı §6-16).

### 3.4 Kimlik doğrulama: parola mı, parolasız mı, MFA

| Yöntem | Değerlendirme | Öneri |
|---|---|---|
| Parola (PBKDF2-SHA512 210k, mevcut hasher) | Kanıtlanmış kod. NIST SP 800-63B-4: tek faktörlü parola en az 15, çok faktörlü en az 8 karakter; bileşim kuralı ve periyodik değiştirme yok [W: pages.nist.gov/800-63-4/sp800-63b/authenticators/, 2026-09-20; özet küçük model çıktısı, madde numaraları teyit edilmeli]. Bugünkü `MinPasswordLength = 10` [K: `IdentityOptions`] | MVP. Portal için 12 + "yaygın/sızmış parola" kara listesi (yerel dosya, dış çağrı yok); bileşim zorunluluğu yok |
| E-posta bağlantısı/OTP ile giriş | NIST 800-63B-4: e-posta **out-of-band kimlik doğrulama için kullanılamaz** [W, aynı sayfa]. Bağlantı tarayıcıları belirteci yakar [W]. Posta kutusu ele geçirilirse hesap düşer | Faz 2, **isteğe bağlı**, yalnız düşük risk (talep izleme); teklif kabulü/PII değişimi için geçerli değil; POST onayı + başlatan tarayıcıya bağlı doğrulayıcı + 10 dk |
| TOTP | Ucuz, yerel, SMS maliyeti/SS7 yok | Faz 2; `account_admin`, iş ortağı ve ticari senaryo açıldığında zorunlu, müşteri için isteğe bağlı |
| SMS OTP | NIST'te kısıtlı [W]; maliyet, SMS sağlayıcı = egress (K17) | Yok |
| WebAuthn/passkey | Oltalamaya dirençli [W] | Faz 3+ (spike'ta kütüphane değerlendir) |
| Kurumsal SSO (B2B) | C-X6 | Ayrı analiz; portal hesabı modeli `external_identity` alanına açık bırakılır |

Ele geçirme/keşif direnci (M3 deseni [K: backend §16.3] birebir taşınır): parola **her zaman önce** doğrulanır, yoksa kukla hash; genel `auth.invalid_credentials`; kilitli/pasif bilgisi yalnız doğru parolada; e-posta kovası + IP+hesap azaltması + hesap kilidi; "parolamı unuttum" her zaman `202`, e-posta yalnız hesap varsa; sıfırlama belirteci 30 dk, tek kullanım, hash'li; parola/e-posta/MFA değişiminde tüm refresh aileleri iptal + kullanıcıya bildirim e-postası. Anonim yüzeyde üçüncü taraf CAPTCHA **yok** (residency); M9G'nin imzalı belirteç + bal küpü + proof-of-work katmanı [K: m9g D16] giriş/etkinleştirme/erişim-iste uçlarında yeniden kullanılır (spike S6).

### 3.5 Oturum ve token modeli (staff'tan ayrı)

| | Personel (bugün) | Portal (öneri) |
|---|---|---|
| Şema | `Smart` → JwtBearer / ApiKey [K: `AuthenticationExtensions.cs`] | Ayrı `Portal` JwtBearer şeması; **yalnız** `/api/v1/portal/**` controller'larına `[Authorize(AuthenticationSchemes = "Portal")]`; staff controller'ları portal şemasını kabul etmez |
| İmzalayan/audience | `crm` / `crm-api`, tek RSA anahtarı | `crm-portal` / `crm-portal-api`, **ayrı RSA anahtarı** (`Portal:Auth:SigningKeyPem`, Docker secret); `typ=portal+jwt` |
| Claim'ler | `sub`, `tid`, roller, e-posta, `platform_admin`… | `sub` (portal hesap), `tid`, `pkind`, `pctc` (kişi), `pacc` (firma), `prl` (portal rolü). Rol adı/izin/e-posta/platform yok |
| Ömür | 15 dk + 30 gün aile | 10 dk + 7 gün mutlak aile (kayan yok); aynı dönüşüm/yeniden kullanım tespiti, ayrı `portal.refresh_tokens` (Identity varlığı **kopyalanır**, modül sınırı korunur) |
| İstemci saklama | Refresh JS durumunda [K: `auth.store`] | Refresh **HttpOnly, Secure, SameSite=Strict `__Host-` çerezi** (path `/api/v1/portal/auth`), access bellekte. Çerezli refresh ucu: özel başlık + `Origin` denetimi (CSRF) |
| Bayatlık | İzinler her istekte çözülür [K: M9] | Her istekte **hesap durumu + kapsam** (`disabled`, kişi silindi, rol değişti) önbellekten (değişimde anında geçersiz kıl) doğrulanır; token yetkiyi taşımaz |
| Hız sınırı | Kullanıcı 600/dk, **kiracı 3000/dk** [K] | Ayrı bölümler: `portal-ip` (anonim), `portal-account`, `portal-tenant`. **Portal trafiği staff kiracı kovasını tüketemez** (mevcut `tid` bölümü paylaşılırsa portal seli personeli boğar → düzeltilmesi gereken bulgu) |

`ICurrentUser.UserId` bugün `Guid?`; portal ilkesi bunu doldurmaz. `AuditLogEntry`'ye `ActorKind` (`user|portal|api_key|automation`) + `PortalAccountId` eklenir (M8B `ApiKeyId` ve M9G `AutomationActor` deseni [K]). Böylece "kim yaptı" personel kullanıcı kimliği uydurmadan yazılır.

### 3.6 Kayıt bazlı yetki: kapsam modeli ve IDOR

Bu, analizin merkezidir. Power Pages (Contact/Account/Parent/Self kapsamları, web rolü + tablo izni birlikte) ve Salesforce Experience Cloud (paylaşım kümeleri: hesap veya kişi eşleşmesi) aynı sonuca varır: **dış kullanıcı kayıt erişimi ilişki üzerinden (kişi/hesap) tanımlanır, personel rol/sahiplik modelinden ayrıdır** [W: learn.microsoft.com/en-us/power-pages/security/table-permissions; help.salesforce.com "Create a Sharing Set for Experience Cloud Site Users"; 2026-09-20; arama özeti].

| Kapsam | Kural | Kullanım |
|---|---|---|
| `self` | `Contact.Id = pctc` | Profil |
| `contact` | kayıt `ContactId = pctc` | Talepler, teklifler (kişiye yazılmış) |
| `account` | kayıt `AccountId = pacc` ve (`prl = account_admin` veya kiracı ayarı `shareAccountRecords`) | Firma faturaları/siparişleri |
| `partner` | kayıt, iş ortağı firmasının kaydettiği/atandığı (`partner_account_id = pacc`) | Deal registration |
| `vendor` | PO `vendorId = subject_id` | Tedarikçi |

Uygulama kuralları:
1. **Tek geçit:** her modül portala `IPortal<X>Port` uygular (Service `IPortalCaseGateway`, Commerce `IPortalQuoteGateway`…; `Contracts`'ta, M9I `ICustomerSolutionCatalog` emsali [K]). Port, çağıranın `PortalScope`'unu (`tid`, `pctc`, `pacc`, `prl`, `subject`) **parametre olarak** alır ve sorgunun tabanına kapsam süzgecini koyar. Controller/handler'da elle `Where(ContactId == …)` yok. Portal modülü iş varlıklarını **hiç görmez**, yalnız portlarla portal DTO'larını birleştirir.
2. **Portal DTO'ları izin listesidir:** staff DTO'ları yeniden kullanılmaz (OWASP API3 nesne özelliği düzeyi yetki: [W: owasp.org/API-Security/editions/2023/en/0xa3-…]). Mimari test: `Sense.Crm.Modules.Portal.*` ve portal DTO'ları staff `*Dto`/`*Detail` tiplerine referans veremez; tüm portal DTO'ları `PortalDto` işaretleyicisi taşır ve alan listesi anlık görüntü testiyle kilitlenir (yeni alan bilinçli eklenir).
3. **IDOR/BOLA (OWASP API1 [W]):** kayıt bulunamadı, başka kiracı, başka firma/kişi, silinmiş, taslak/iç kayıt → **aynı 404, bayt bayt aynı gövde** (M9H D9 sözleşmesi [K]); ayrım için 403 yok. Test: portal rota tablosundan **otomatik üretilen** çapraz-hesap testi (her `{id}` rotası için iki kiracı × iki firma × iki kişi matrisi) + var olmayan GUID ile karşılaştırma.
4. **M9H ile koordinasyon (kritik):** M9H `RecordScopeMiddleware` kimliksiz/tanınmayan ilkeyi "hiçbir şey" (fail-closed) sayar ve kapsamı EF global filtresine koyar [K: m9h D8, D10 bypass envanteri]. Portal portları personel kayıt kapsamından geçmez; kendi `PortalScope`'larını uygular. Bu, açık bir **bypass envanteri girdisi** ve M9H'de "portal ilkesi = personel kapsamı uygulanmaz, yalnız `IPortalScope`" tanımı gerektirir. Spike S2 + M9H planına not.
5. **Yazma:** portal yalnız beyaz listeli komutlar: `AddPortalComment`, `CreatePortalCase`, `UpdateOwnProfile`, `RespondToQuote`, … Her komut kapsamı yeniden doğrular (TOCTOU: kayıt kapsam dışına çıkmış olabilir), `xmin` çakışması 409, idempotency anahtarı (çift tıklama = tek yorum/kabul).
6. **Mimari testler:** her portal isteği `[RequiresPortalScope]`; `[AnyAuthenticatedUser]` portal şemasında yasak; portal controller'ı `Portal` dışı mediator isteği gönderemez.

### 3.7 Yorum/ek görünürlüğü (iç ↔ müşteri)

- Yorum: portal yalnız `visibility = public` görür [K]. **Tehlike:** bugün `public` yorumlar yalnızca personel tarafından okunuyor varsayımıyla yazıldı; portal açılınca **geçmiş tüm `public` yorumlar müşteriye görünür olur**. Öneri: kiracıda `portal_visible_since` (portalın açıldığı an) — bundan önceki yorum/olay/ek portala **hiç görünmez** (yeni talepler dahil, kesme talep oluşturma değil yorum zamanına göre); personel arayüzünde "müşteri bunu görür" işareti ve ilk açılışta uyarı. Karar 12.
- Zaman çizelgesi olayları (`CaseEvent`): portal yalnız müşteri durum eşlemesi (`received | in_progress | waiting_for_you | resolved | closed`) gösterir; atanan kişi adı, öncelik, SLA hedef/ihlal, çözüm notu (ayrı karar) **yok**.
- Ekler (M8C): bugün ek erişimi kaydın kendi okuma iznine bağlı, ek başına görünürlük yok [K: K21 "yeni izin yoktur"]. Portal için `attachments.customer_visible bool default false` (yalnız personel işaretler; portaldan yüklenen ek otomatik `true`). Bilgi tabanı `internalNote` **hiçbir müşteri projeksiyonuna girmez** [K: m9i D8].
- Portal yorumu: `CaseComment.AuthorUserId` boş olamaz [K: `CaseComment.cs`] → `author_kind` (`user|portal`) + `author_portal_account_id` (`author_user_id` nullable) migration'ı; `CaseChannel`'a `Portal`; portal yorumu ilk yanıt SLA'sını **saymaz**, ama `pending → open` geçişini tetikler; kapalı talepte yorum `case.closed`. Talep oluşturmada sahip atama M9G `IOwnerAssignment` (`origin = portal` eklenir) ve `crm.cases.write` gerektirmez (portal komutu sistem aktörüyle yazar; `createdBy` = `ActorKind portal`).

### 3.8 Kötüye kullanım, spam, yükleme

| Tehdit | Önlem |
|---|---|
| Talep spam'ı / yorum seli | Hesap başına: 10 talep/gün, 60 yorum/gün, talep başına 200 yorum (yapılandırılabilir); yeni hesapta düşük; kiracı ayarıyla kapatılabilir; aşımda `429` + personel uyarısı |
| Kullanıcı bulma/şifre deneme | §3.4 |
| Sıralı GUID tarama | GUID v7 (zamana bağlı, tahmini kolay) → yetki tek savunma; §3.6 testleri; talep `number` (C-2026-0001) tahmin edilebilir → portalda **numara yalnız gösterim**, rota kimliği GUID ve kapsamlı |
| Zararlı dosya | M8C allow-list + imza + boyut + kota [K] **artı** portalda: dar liste (pdf, png, jpg, txt, docx/xlsx yalnız kiracı ayarıyla), ≤ 10 MB, talep başına ≤ 10, gün başına hesap kotası, **tarama zorunlu** (`scan_status = pending → clean | infected`; `clean` olana dek personele indirilemez, portalda "işleniyor"); tarama motoru ağ dışı (ClamAV imza güncellemesi K17 "çıkış yok" ile çakışır → içerde ayna/çevrimdışı paket, spike S5); indirme API'den akar, `attachment`, `nosniff`, `no-store` [K: m8c] |
| XSS | Yorum/açıklama düz metin (Markdown yok); render metin düğümü; personel arayüzünde portal kaynaklı içerik "dış kaynak" işaretli ve zaten `SafeHtml`/kodlanmış [K: m9i sunucu önizlemesi deseni]; marka: yalnız ad/logo/renk, **özel HTML/CSS yok** |
| Aramada sızıntı | Portal aramasının kapsamı yalnız bilgi tabanı yayınlanmış+`customer` ve kişinin kendi talepleri; genel arama (M9A) portala açılmaz |
| Zaman/uzunluk oracle'ı | 404 tekdüzeliği, hata gövdesi sabit, `ETag`/`Content-Length` ayırt etmez [K: m9h test 2] |

### 3.9 Denetim ve izleme

- Yazmalar `audit_log_entries`'e `ActorKind = portal` ile (aynı transaction) [K: K14]. Yorum gövdesi `***` maskeli [K].
- **Okuma denetimi** yalnız mali/hassas nesnede (teklif, fatura, belge indirme): `portal.access_log` (`account_id`, `record_kind`, `record_id`, `at`, `action`; IP/ad yok) — M8C indirme günlüğü emsali [K: m8c satır 116]. Giriş olayları (`login_ok/fail/lockout/reset/mfa`) ayrı; ham IP 90 gün sonra HMAC'e dönüştürülür [M9G `ip_hash` deseni, K].
- Personel görünümü: kişi kaydında "Portal etkinliği" (son giriş, açık oturum, davet durumu) + "oturumları kapat/hesabı devre dışı bırak". Metrikler: yalnız düşük kardinaliteli etiketler [K: K20]; portal giriş hatası/kilit/yeniden kullanım sayaçları.

### 3.10 KVKK

**Roller.** Her kiracı ayrı tüzel kişi (grup şirketi) → o şirket kendi müşteri/iş ortağı verisinin **veri sorumlusu**. Platformu veri merkezinde işleten grup BT'si/holding (veya bizim ekibin destek erişimi) **veri işleyen**: yazılı talimat ve işleyen sözleşmesi gerekir (6698 m.12; işleyenin yazılı talimat dışında işleme yapamaması [W: kvkk.gov.tr rehberleri, ikincil kaynak özetleri, 2026-09-20]). Kanunda ortak veri sorumluluğu ayrıca tanımlı değil; grup şirketleri ortak amaç belirliyorsa hukuki nitelendirme gerekir [?]. Portalda kiracıya özgü hesap modeli ve K4 (konsolidasyon yok) bu ayrımı **teknik olarak zorlar**: bir kişinin iki şirketteki verisi birleşmez.

| Konu | Tasarım kararı |
|---|---|
| Hukuki sebep | Hizmet/sözleşmenin ifası ve meşru menfaat (m.5); portal kullanımı için **açık rıza istenmez**. Zorunlu: kiracıya özgü, sürümlü **aydınlatma metni** (m.10: sorumlu tüzel kişi, amaç, alıcılar, haklar; ilk girişte ve sürüm değişince onay/okundu kaydı). Açık rıza yalnız isteğe bağlı işlemede (pazarlama e-postası; ticari ileti onayı/İYS ayrı: 6563 [?]) ve **ayrı, işaretsiz** kutu. M9G rıza metni sürüm kalıbı yeniden kullanılır [K] |
| İlgili kişi başvurusu (m.11–13) | Portalda "Kişisel veri talebi" formu (erişim, düzeltme, silme, itiraz, aktarım). Kimlik zaten doğrulanmış olduğundan ek belge gerekmez. Talep kayıtlı hesaba düşer, personel kuyruğunda **30 gün** sayacı (en geç otuz gün, ücretsiz: m.13/2 [W: rodresponsa.com / kisiselverilerinkorunmasi.org, ikincil; 2026-09-20]), cevap portaldan tebliğ |
| Kendi verisini indirme | Kişi kaydı + talepler + herkese açık yorumlar + rızalar + oturum/giriş özeti, JSON/CSV; yalnız portalın gösterebildiği alanlar (iç notlar, atama, SLA çıkmaz; bunun istisna gerekçesi başvuru cevabında yazılır) |
| Silme | **Bugün yok.** Plan taraması: yalnız kiracı imhası (M7), dosya imhası (M8C), web formu kişi verisi silme (M9G) [K, anahtar kelime taraması]. Kişi bazlı `IPersonalDataEraser` (anonimleştirme: kişi ad/e-posta/telefon → `[silindi]`, talep yorumları yazarı anonim, portal hesabı silinir, refresh iptal) portal öncesi ayrı kartta yapılmalı. Fatura/sipariş/teklif gibi kayıtlar yasal saklama nedeniyle silinmez, anonimleştirmede kişi bağı kesilir (VUK/TTK 10 yıl saklama [?] hukuk teyidi) |
| Saklama | Portal oturum/refresh: süre sonunda Worker temizler (M5 açık işi [K]); giriş günlüğü 12 ay; davet/sıfırlama belirteçleri süre sonu + 7 gün; kapalı talep saklaması kiracı politikası (varsayılan: kapanıştan 5 yıl [?] karar 11) |
| Özel nitelikli veri | Talep serbest metni sağlık vb. içerebilir: formda uyarı ("özel nitelikli veri paylaşmayın"), günlük/e-posta/webhook'ta gövde yok. Webhook zarfı zaten PII'siz [K: K19] — portal olayları da aynı |
| Bildirim içeriği minimizasyonu | E-posta/SMS: "Talebinizde güncelleme var (C-2026-0001). Görmek için portala giriş yapın." Yorum metni, ek, tutar, ad-soyad (yalnız ad) **yok**; bağlantı jeton taşımaz (yalnız etkinleştirme/sıfırlama bağlantısı taşır, parça biçimli); SPF/DKIM/DMARC (kurum rölesi, runbook); oltalama farkındalığı için "size parola sormayız" cümlesi. M8A alıcıları bugün yalnız aktif üye ve serbest adres kabul etmez [K: m8a D6] → **doğrulanmış portal hesabı adresine, yalnızca portal şablonlarıyla** giden yeni `IExternalRecipient` portu gerekir (serbest adres/şablon hâlâ yok) |
| Yurt dışı | Portal 3. taraf betik/yazı tipi/CAPTCHA/CDN/analitik içermez (CSP `self`); veri merkezi Türkiye. 2024 (7499) m.9 değişikliği (standart sözleşme vb.) yalnız yurt dışı aktarımda geçerli, tasarım aktarım üretmez [W: ikincil kaynaklar, 2026-09-20] |
| VERBİS | Her sorumlu kendi kaydında "portal/müşteri hesap verisi" kategorisi ve saklama sürelerini güncellemeli (operasyonel, hukuk teyidi [?]) |

## 4. Önerilen yol haritası ve kart bölünmesi

Kanıt: Faz 0 dışında hiçbir faz M8A/M8C teslimi olmadan ürün olarak anlamlı değildir (davet e-postası ve ek). Efor mühendis-haftası (Backend + Web + DevOps + Security, kabaca; ±%30).

| Faz | Kart | İçerik | Sahip | Efor | Bağımlılık |
|---|---|---|---|---|---|
| 0 | X4-S (spike) | S1–S7 (§5) | Spec+Backend | 2 | — |
| 0 | X4-0 (isteğe bağlı) | M9G web formuna `case` hedefi (anonim talep, otomatik yanıt yok) | Backend+Web | 2 | M9G |
| 1 | **X4-1 Portal çekirdeği** | `Portal` modülü: hesap, davet/etkinleştirme, giriş, sıfırlama, ayrı token/refresh, `PortalScope`, ayarlar, hız bölümleri, `Api:Surface`, `crm_portal` rolü, mimari testler; portal SPA kabuğu (giriş, etkinleştirme, profil, tr/en); nginx `external` profili + compose + runbook; kişi kaydında davet/etkinlik paneli | Spec→Backend+Web+DevOps | 10–11 | M7 (kapı/limit), M9H (ilke tanımı) |
| 1 | **X4-2 Destek** | `IPortalCaseGateway` (Service), yorum yazar türü migration'ı, `portal_visible_since`, talep aç (M9G atama `origin=portal`), talep listesi/detay/yorum; bilgi tabanı uçları + talep açarken öneri; portal SPA ekranları | Spec→Backend+Web | 5–6 | X4-1, M9I, M9G |
| 1 | **X4-3 Ekler** | `customer_visible`, portal yükleme/indirme, zorunlu tarama (ClamAV konteyneri + imza güncelleme yolu) | Backend+Web+DevOps | 3 | M8C, X4-2 |
| 1 | **X4-4 KVKK** | Kişi bazlı `IPersonalDataEraser` (ayrı kart olarak öne çekilir), aydınlatma/rıza sürümleri, veri talebi kuyruğu (30 gün sayacı), dışa aktarma, portal bildirim şablonları + `IExternalRecipient` | Spec→Backend+Web | 4–5 | M8A, X4-1 |
| 1 | **X4-5 Güvenlik incelemesi** | Bağımsız IDOR/DTO/sızma testi, WAF/CSP/limit gözden geçirme | Security | 1 | X4-1..4 |
| 2 | X4-6 Ticaret | Teklif/sipariş/fatura portları, teklif kabul/red (sürümlü kanıt + değişmezlik), ödeme durumu, okuma denetimi; belge indirme (PDF üretimi başka kartta yoksa buraya +2) | Spec→Backend+Web | 5 (+2) | M6A, M9C, X4-1 |
| 2 | X4-7 MFA/parolasız | TOTP, kurtarma kodları, isteğe bağlı e-posta bağlantısı (POST onaylı) | Backend+Web | 3 | X4-1 |
| 2 | X4-8 Kiracı hostu/marka | `portal_hosts`, ad/logo/renk | Backend+Web+DevOps | 1–2 | S1 |
| 3 | X4-9 İş ortağı | İlişki türü, kayıt koruma süresi, çakışma kuralı, onay (M4 iş akışı), iş ortağı görünümü | Spec→Backend+Web | 4–5 | Karar 14, M4, M9G |
| 3 | X4-10 Tedarikçi PO | PO onayı/teslim tarihi | Backend+Web | 2 | M9C |

Toplam: Faz 1 ≈ 22–26 (2 paralel ekiple ~3 takvim ayı), Faz 2 ≈ 9–11, Faz 3 ≈ 6–8, spike+Faz 0 ≈ 4.

Sıra: X4-S → X4-4'ün eraser kısmı ile X4-1 paralel → X4-2 → X4-3 → X4-5 → pilot (tek şirket, davetli 20–50 kişi) → Faz 2. Board'a kartlar: `C-X4-1..5` (Faz 1), Faz 2/3 kartları Faz 1 pilot sonrası açılır.

## 5. Riskler ve spike gerektirenler

| # | Risk / bilinmeyen | Etki | Spike (çıktı) |
|---|---|---|---|
| S1 | Müşterinin vekili portalı internete nasıl açacak (DMZ, WAF, joker DNS/sertifika, `X-Forwarded-For` güveni) | IP tabanlı azaltma çalışmaz; ilk dış yüzey | Vekil sahibiyle 0,5 gün çalıştay; `TRUSTED_PROXY_CIDR` ve yol öneki (`/t/{slug}`) doğrulama |
| S2 | M9H kayıt kapsamı + `crm_portal` DB rolü + portal portları: global filtre bileşimi ve bypass envanteri | Çapraz-hesap sızıntı (en yüksek etki) | PoC: portal portundan kapsamsız sorgu üretmenin **imkânsızlığı** testi; rol grant matrisi |
| S3 | Geçmiş `public` yorumların müşteriye görünmesi | Yanlışlıkla iç bilgi ifşası | Veri taraması: mevcut pilot verisinde `public` yorum sayısı/içerik örneklemesi; `portal_visible_since` tasarımı |
| S4 | Kurum SMTP rölesi, SPF/DKIM/DMARC, bağlantı tarayıcıları (Defender/Proofpoint) etkinleştirme bağlantısını tüketir mi | Etkinleştirme başarısız, destek yükü | Test posta kutusu + kurum güvenlik ağ geçidiyle POST-onaylı parçalı bağlantı denemesi |
| S5 | ClamAV imza güncellemesi ile "çıkış yok" (K17/K19) çakışması | Tarama bayatlar veya çıkış açılır | `egress` profili + izin listesi (yalnız imza aynası) ya da çevrimdışı imza paketi; karar |
| S6 | Anonim uçlarda M9G bal küpü/PoW katmanının giriş akışına uyarlanması; parola doldurma (credential stuffing) | Hesap ele geçirme | Yük/kötüye kullanım testi, eşikler |
| S7 | Portal-staff aynı süreçte kaynak yarışı (Faz 1'de ayrı konteynerle çözülür) | Portal seli personel API'sini yavaşlatır | `Api:Surface` ayrımıyla yük testi (k6) |
| S8 | Hukuk: teklif kabulü tıklama onayının bağlayıcılığı, VUK/TTK saklama vs silme, İYS, işleyen sözleşmesi, ortak sorumluluk | Tüm faz 2 kapsamı ve silme davranışı | Kiracı hukuk müşavirliği yazılı görüşü [?] (bu analizde hukuki teyit yok) |
| S9 | Kişi yinelenmeleri (M9E `IDuplicateFinder`) — aynı gerçek kişi iki `Contact` → iki portal hesabı/yanlış bağ | Yanlış kişiye veri | Davet öncesi yineleme uyarısı; birleştirmede portal hesabı taşıma kuralı |
| R1 | Yeni yüzeyle "izin unutulan istek" sınıfı hataları (özellikle DTO alan sızıntısı) | Sızıntı | Anlık görüntü testi + X4-5 sızma incelemesi |
| R2 | Kapsam şişmesi (belge yönetimi, ödeme, sohbet, mobil) | Gecikme | Kapsam dışı listesi aşağıda |

**Kapsam dışı (bu analizde):** çevrim içi ödeme (PSP/PCI), canlı sohbet, mobil uygulama (K0: yalnız web), anonim halka açık yardım merkezi, kurumsal SSO (C-X6), müşteriye özel API anahtarı, klasör paylaşımı, müşteri tarafı belge imzası (C-X3 dijital imza).

## 6. Ürün sahibinin vereceği kararlar

| # | Soru | Önerilen cevap |
|---|---|---|
| 1 | Portal gerçek bir talep mi, hangi şirket pilot? | Evet, **tek şirket pilotu** ve destek portalı; talep yoksa Faz 0 (web formu + bildirim) ile yetin |
| 2 | Hedef kitle önceliği? | Faz 1: son müşteri + B2B kişi; iş ortağı/tedarikçi ancak gerçek kanal varsa Faz 3 |
| 3 | Mimari? | **E** (B'nin aşamalısı, `Api:Surface` ayrı konteyner); D yalnız yük/uyum kanıtıyla |
| 4 | Kimlik? | Kiracıya özgü ayrı portal hesabı, kişiye/Vendor'a 1:1 bağlı; global `User` kullanılmaz |
| 5 | Kimlik doğrulama? | Parola (12+, kara liste) + TOTP (Faz 2, ticari/`account_admin`/iş ortağı zorunlu); e-posta bağlantısı yalnız isteğe bağlı düşük risk, teklif kabulünde geçersiz |
| 6 | Kayıt? | Yalnız davetle; "erişim iste" formu sonra, hesap açmaz |
| 7 | Kiracı çözümü? | MVP `/t/{slug}` yolu; kiracı alt alan adı Faz 2 |
| 8 | Aynı firmanın kişileri birbirinin kaydını görsün mü? | Varsayılan **hayır**; personelin atadığı `account_admin` veya kiracı ayarı ile firma kapsamı |
| 9 | Veri sorumlusu/işleyen sözleşmesi kim imzalar? | Sorumlu: kiracı tüzel kişi; işleyen: platformu işleten birim; hukuk yazılı teyit versin |
| 10 | Aydınlatma mı rıza mı? | Aydınlatma + sürüm onayı; rıza yalnız pazarlama, ayrı kutu |
| 11 | Silme ve saklama? | Kişi bazlı anonimleştirme, mali belge kaydı kalır; kapalı talep saklama 5 yıl (teyit gerekir) |
| 12 | Geçmiş `public` yorumlar? | Portal açılışından önceki tüm içerik gizli (`portal_visible_since`) |
| 13 | Müşteri neyi görsün? | Durum eşlemesi, herkese açık yorum, işaretli ek; atanan kişi adı, öncelik, SLA, iç not **yok**; çözüm notu yalnız personel yayınlarsa |
| 14 | Teklif kabulü? | Faz 2'de önce "kabul isteği" (temsilciye görev) ; doğrudan durum değişimi kiracı ayarıyla ve hukuk onayıyla |
| 15 | Ödeme? | Yalnız durum gösterimi; çevrim içi ödeme kapsam dışı |
| 16 | Plan ve limit? | Kapı modülü `portal`, ayrı `maxPortalAccounts`; portal hesabı staff kullanıcı limitine sayılmaz |
| 17 | Profil güncelleme? | Telefon/adres/dil doğrudan; ad/e-posta değişikliği doğrulama veya personel onayı |
| 18 | Yükleme? | Dar tür listesi, 10 MB, tarama zorunlu; tarama altyapısı (ClamAV + imza yolu) kim işletir? — BT ile netleştir |
| 19 | İş ortağı programı var mı (koruma süresi, çakışma çözümü, onaylayan)? | Yoksa Faz 3 kartları açılmaz |
| 20 | Marka özelleştirme? | Yalnız ad/logo/renk; özel HTML/CSS yok |

## Kaynaklar (erişim 2026-09-20)

| Kaynak | Kullanım |
|---|---|
| Microsoft Learn, Power Pages table permissions: https://learn.microsoft.com/en-us/power-pages/security/table-permissions | Contact/Account/Parent/Self kapsam modeli (arama özeti; sayfa tam okunmadı) |
| Salesforce Help, Create a Sharing Set for Experience Cloud Site Users | Kişi/hesap eşleşmesiyle dış kullanıcı paylaşımı (arama özeti) |
| NIST SP 800-63B-4: https://pages.nist.gov/800-63-4/sp800-63b/authenticators/ | E-posta OOB yasağı, parola uzunluk/bileşim/rotasyon (WebFetch özeti; küçük model çıktısı, madde numaraları teyit edilmeli); belge 2025 Temmuz'da yayımlandı (ikincil kaynak) |
| OWASP ASVS 5.0.0 (Mayıs 2025): https://github.com/OWASP/ASVS/tree/v5.0.0 | Doğrulama gereksinim çatısı (gereksinim düzeyi eşlemesi yapılmadı) |
| OWASP API Security Top 10 2023, API1 BOLA, API3: https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/ | Nesne ve özellik düzeyi yetki |
| better-auth #6985, supabase #41618 (GitHub tartışmaları) | Bağlantı tarayıcılarının tek kullanımlık bağlantıyı tüketmesi (topluluk raporu, anekdot) |
| 6698 m.13 (30 gün): rodresponsa.com, kisiselverilerinkorunmasi.org (ikincil); KVKK 7499 değişikliği: gunespartners.com, lexinlegal.com (ikincil) | KVKK süre ve yurt dışı aktarım; **resmî metin (mevzuat.gov.tr) bu oturumda okunamadı** |
| Doğrulanmadı **[?]** | VUK/TTK 10 yıl saklama; ticari ileti/İYS kapsamı; ortak veri sorumluluğu; tıklama onayının bağlayıcılığı; Mantine CSP nonce desteği; 5651 günlük saklama gerekliliği bu ürüne uygulanır mı |
| Kardeş repo `senseik` | Portal/self-service kalıbı **yok**; yalnız kariyer sitesi (aday) tarafında `public` DTO'da iç alan bulunmaması (`InternalNotes`) ve jeton bağlantılı takvim (`SchedulingLink.TokenHash`) örnekleri var: `docs/analysis/kolayik-video/11.3.11-ats-ishe-alim.md` |
