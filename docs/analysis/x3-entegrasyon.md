# C-X3 — ERP, dijital imza ve SFTP entegrasyonları (analiz ve karar önerisi)

Kart: `C-X3` (Spec, araştırma) · Tarih: 2026-09-20 · Kapsam: mimari diyagram blok 7 "Dış Sistem Entegrasyonları" içinde henüz karşılanmayan üç başlık: **ERP** (SAP, Oracle, Logo, Mikro, Netsis, Dynamics), **dijital imza**, **dosya transferi (SFTP)**. E-posta/SMS = M8A, webhook/Open API/API anahtarı = M8B, dosyalar = M8C, teklif/sipariş/fatura/tedarikçi/PO = M6A + M9C, içe/dışa aktarma = M9B, otomasyon = M9G (kapalı eylem kümesi). Bu belge kod içermez; yalnız analiz, seçenek karşılaştırması, güvenlik modeli, yol haritası ve karar listesidir.

**Doğrulama işaretleri:** `[K]` kaynaktan okundu (bölüm 8'de kaynak+tarih), `[İ]` ikincil/satıcı kaynağı, `[?]` doğrulanmadı (hafıza/varsayım), `[D]` bu depodaki belge/koddan doğrulandı. Hacim ve efor sayıları **tahmindir** (ölçülmedi).

## 0. Yönetici özeti

1. **Öneri: B seçeneği, kademeli.** Yeni `Connectors` modülü (bağlantı profili + şifreli sır + Worker'da outbox güdümlü, idempotent iş motoru + hata kuyruğu/yeniden dene arayüzü + uzlaştırma) ve üç port: `IErpConnector`, `IFileTransferClient` (SFTP), `ISignatureProvider`. Genel amaçlı eşleme dili/iPaaS **yapılmaz**; eşleme sağlayıcı adaptörünün kodudur.
2. **Faz 0 (2–3 hf, hemen):** D seçeneğinin güçlendirilmiş hâli — mevcut M8B Open API/webhook'lar ERP entegratörü için yeterli hâle getirilir (fatura olayları, `updatedSince`, `Idempotency-Key`, harici referans alanı). Bu, çerçeve beklemeden ilk müşteri değerini verir ve B'nin alt kümesidir.
3. **Önkoşul:** M8B'nin SSRF/egress/sır kodu `Integrations` içinde kalıyor ve modül sınırı yüzünden yeniden kullanılamaz; ayrıca squid yalnız `CONNECT ip:443|8443` izinlidir — SFTP (22) ve şirket içi ERP portları (Logo 32001, Mikro 8084/8094, SAP SL vb.) çıkamaz. **Önce ortak `Shared` egress/sır çıkarımı + operatör yönetimli hedef izin listesi** gerekir.
4. **SFTP ve dosya-bırakma ERP bağlayıcısı önce**, çünkü ERP'den bağımsızdır ve her ERP için evrensel yedek yoldur; ilk REST ERP adaptörü **hangi ERP'nin çalıştığı keşfedildikten sonra** seçilir (bilinmeyen #1).
5. **E-imza ikinci dalga:** CRM'de belge PDF üretimi yoktur (M9C kapsam dışı) ve harici imzacı için halka açık yüzey gerekir (M9G WebForms deseni / X4 portal). Yalnız TR ESHS sağlayıcıları varsayılan; yurt dışı SaaS varsayılan kapalı. Hukuki geçerlilik kararı hukukçu onayına bağlıdır.
6. **E-fatura CRM'de kesilmez** (ERP/mali program + GİB entegratörü kesmeye devam eder); CRM yalnız ERP'nin kestiği faturanın referansını/durumunu okur.
7. Toplam (P50, ±%35): Faz 0 ≈ 3 EW; Faz 1 (çerçeve + SFTP + dosya-bırakma + ilk REST ERP) ≈ 37 EW; Faz 2 (PDF + e-imza) ≈ 20 EW. EW = 1 mühendis-hafta (test+inceleme dahil, dış bekleme hariç).

## 1. İş senaryoları ve öncelik

Varsayım: kiracı = grup şirketi; her şirketin kendi ERP'si olabilir (farklı ürün/sürüm), veri merkezi içinde veya bulutta. Kiracı sayısı pilotta onlar, tasarım tavanı K3 (1000). Hacimler `[?]` varsayımdır; keşif çalıştayında doğrulanır (bölüm 5).

| # | Senaryo | Alt analiz | Öncelik | Neden |
|---|---|---|---|---|
| S1 | Kabul edilen teklif → onaylı sipariş → ERP satış siparişi (CRM→ERP), ERP belge no'sunun CRM'e yazılması | ERP | **P0** | Ana iş değeri; `quote.accepted`/`order.created` olayları zaten var [D m8b] |
| S2 | Müşteri (cari) ana verisi iki yönlü: cari kod, VKN/TCKN, vergi dairesi, vade, risk limiti | ERP | **P0** | Eşleşme anahtarı olmadan S1 yinelenen cari üretir; M9E `taxNumber/taxOffice/accountNumber` planlı, henüz merge değil [D m9e] |
| S3 | Ürün/stok kodu/fiyat listesi ERP→CRM (ERP ana kaynak) | ERP | **P0/P1** | Teklif kalemlerinin doğruluğu; Product.Code (SKU) mevcut [D] |
| S4 | Fatura/tahsilat durumu ERP→CRM (resmi fatura no, e-fatura UUID/PDF bağlantısı, açık bakiye) | ERP | **P1** | Satış temsilcisi bakiye/risk görmeli; M9C faturası ticari/pro-forma kalır (karar #2) |
| S5 | Cari bakiye/risk limiti canlı okuma (read-through, önbellekli) | ERP | P1 | Teklif onayı öncesi uyarı |
| S6 | CRM faturasını ERP'ye gönderme | ERP | P2 | S4 ile çelişir (tek kaynak kuralı); karar #2 |
| S7 | Tedarikçi/PO → ERP satın alma | ERP | P2 | M9C v1'de PO ile satış arasında ilişki yok |
| S8 | Stok/kullanılabilir miktar okuma | ERP | P2 | M9C'de stok yok |
| S9 | E-fatura/e-arşiv/e-irsaliye **kesme** | ERP/e-belge | **Kapsam dışı (öneri)** | Mali mühür + GİB entegratörü + VUK sorumluluğu; ERP'de çözülü |
| G1 | Teklif/sözleşmenin müşteri tarafından imzalanması (harici imzacı) | e-imza | **P1** (Faz 2) | Kabul kanıtı; `quote.accepted` bugün tık ile |
| G2 | Kurum (şirket yetkilisi) imzası: sunucu tarafı, HSM/sağlayıcı | e-imza | P2 | Maliyet/HSM; karar #7 |
| G3 | Toplu imza / yenileme dalgası | e-imza | P3 | |
| F1 | ERP toplu dosyası ERP→CRM (cari/ürün/fiyat CSV) SFTP'den çekme | SFTP | **P0** (dosya-bırakma ERP) | REST'i olmayan/kapalı ERP için evrensel yol |
| F2 | CRM→partner/ERP planlı dosya gönderme (sipariş listesi, tahsilat raporu) | SFTP | **P1** | |
| F3 | Banka/finans kurumu dosya alışverişi (ekstre/tahsilat mutabakatı) | SFTP | P2 `[?]` | Bankaya özgü biçim/PGP; CRM ödeme sistemi değildir |
| F4 | Partnerin bize SFTP ile **push** etmesi (kurum içinde SFTP sunucusu barındırma) | SFTP | P3 (öneri: v1'de yok) | Yeni içe dönük yüzey; çekme (pull) tercih |

### 1.1 ERP (S1–S8)

| Boyut | S1 sipariş→ERP | S2 cari iki yönlü | S3 ürün/fiyat | S4/S5 fatura-bakiye |
|---|---|---|---|---|
| Aktörler | Satış temsilcisi, ERP servis hesabı, mali işler | Satış, mali işler (cari açar) | Ürün yöneticisi (ERP'de) | Satış, mali işler |
| Yön | CRM→ERP (+ERP no geri) | CRM↔ERP (alan bazlı sahiplik) | ERP→CRM | ERP→CRM |
| Hacim `[?]` | 10–5 000 sipariş/gün/kiracı | ilk yükleme 10⁴–10⁵; sonra 10²–10³ değişim/gün | 10³–10⁵ SKU; günlük delta | okuma odaklı; olay başına |
| Tutarlılık | **En-az-bir-kez + idempotent** (aynı sipariş ERP'de bir kez); dakikalar; başarısızsa görünür hata kuyruğu | Nihai tutarlılık; **alan bazlı sahiplik** (son yazan kazanır **yasak**); yankı (CRM→ERP→CRM) döngüsü engellenir | ERP kazanır; CRM alanları salt okunur | Bayat okuma kabul (≤ dk); önbellek TTL |
| Zorluk | ERP'lerin çoğunda `Idempotency-Key` yok → "harici referansla ara, yoksa oluştur" + uzlaştırma | Eşleşme anahtarı (VKN→cari kod), yinelenen cari, silme/birleştirme | İlk toplu yükleme hız sınırı | ERP API hız/lisans sınırı |

### 1.2 Dijital imza (G1–G3)

| Boyut | Değer |
|---|---|
| Aktörler | Satış temsilcisi (başlatır), **harici imzacı** (CRM kullanıcısı değil; e-postayla bağlantı), şirket yetkilisi, imza sağlayıcısı (ESHS), hukuk/denetim |
| Yön | CRM→sağlayıcı (belge özeti/PDF), sağlayıcı→CRM (durum, imzalı PDF); ikinci yön için **gelen (inbound) webhook yok** (M8B kapsam dışı) → önce **yoklama (poll)**; gelen bildirim sonraya |
| Hacim `[?]` | 10–1 000 belge/ay/kiracı; süre saatler-günler (insan) |
| Tutarlılık | Durum makinesi `taslak→imzaya gönderildi→görüntülendi→imzalandı/reddedildi/süresi doldu`; **imzalanan baytların özeti** gönderim anında kilitlenir (Files'ta sürümlü, değişmez); imzalı dosya + doğrulama raporu + kanıt olayları atomik yazılır; sağlayıcı tarafı olay tekrarı idempotent |
| Önkoşullar | PDF üretimi (yok), harici imzacı için halka açık kimliksiz yüzey (M9G D21 gibi ayrı ana bilgisayar; X4 portal ile eşgüdüm), gerçek SMS sağlayıcısı (M8A'da yalnız port var) OTP için |

### 1.3 SFTP (F1–F4)

| Boyut | Değer |
|---|---|
| Aktörler | ERP/partner sistemi, banka, kiracı yöneticisi (profil), Worker |
| Yön | Çekme (pull) ve gönderme (push); CRM **istemci**; sunucu barındırma v1 dışı |
| Hacim `[?]` | Günlük–saatlik; 1 KB–500 MB; dosya başına 10²–10⁶ satır |
| Tutarlılık | Dosya başına **tam-bir-kez işleme defteri** (ad+boyut+SHA-256+uzak mtime); yazan taraf `.tmp`→rename disiplini; kısmi dosya/yeniden başlatma; kayan pencere ile yinelenen; zaman dilimi/kod sayfası (Windows-1254) — M9B D-notu |
| **Mevcut sınırlar [D]** | M9B içe aktarma: dosya ≤ 10 MB Postgres'te, satır tavanı plan limiti (örnek 1 000), **yalnız firma/kişi/potansiyel/ürün vb.**; M8C dosya başına 25 MB varsayılan (1–200). ⇒ **ERP toplu senkronu M9B sihirbazı üzerinden yapılamaz**; bağlayıcı kendi akışlı okuyucusu + sahip modülün dar senkron portunu kullanır |

## 2. Bağlayıcı çerçevesi: seçenekler

**A** Her entegrasyon kendi modülü (`Erp.Logo`, `Sftp`, `Esign.X`…). **B** Genel `Connectors` modülü: sağlayıcı SDK/portu, kiracı başına şifreli bağlantı profilleri, Worker'da outbox+idempotency+uzlaştırma, hata kuyruğu + yeniden dene arayüzü. **C** Harici iPaaS/ESB (Apache Camel/NiFi, n8n, WSO2, Mule…). **D** Yalnız Open API + webhook; bağlayıcıyı müşteri yazar.

Ağırlıklar: efor 15, güvenlik 15, veri merkezinde işletim 15, modüler monolit uyumu 10, kiracı izolasyonu 15, kapsam (ERP+e-imza+SFTP birlikte) 20, ilk değer süresi 10. Puan 1–5 (5 iyi; efor için 5 = en az). Skor = Σ(ağırlık×puan)/5.

| Ölçüt (ağırlık) | A | B | C | D |
|---|---|---|---|---|
| Efor (15) | 3 | 3 | 2 | 5 |
| Güvenlik (15) | 2 | 5 | 2 | 4 |
| DC işletim (15) | 2 | 4 | 2 | 4 |
| Modüler uyum (10) | 4 | 5 | 2 | 5 |
| Kiracı izolasyonu (15) | 3 | 5 | 2 | 4 |
| Kapsam (20) | 4 | 5 | 4 | 1 |
| İlk değer (10) | 4 | 3 | 3 | 4 |
| **Skor / 100** | **62** | **87** | **50** | **73** |

Gerekçe (kısa):
- **A:** her modül retry, sır saklama, SSRF, denetim, hata arayüzünü yeniden yazar; M8B'de çözülen sorunlar N kez çözülür, yönetici N farklı ekran görür. İlk entegrasyon hızlı, üçüncüden sonra pahalı ve tutarsız.
- **B:** M8B (SSRF/sır/kuyruk/kiracı adaleti), M9G (kapalı eylem, `Actor`, döngü koruması), M8C (tarayıcı portu) desenlerini **bir kez** kurar. Maliyet: ön yatırım ve `Shared`'a çıkarım (bölüm 4). Risk: "genel çerçeve" aşırı mühendislik → yalnız 3 port + ortak iş motoru; eşleme DSL'i yok.
- **C:** iPaaS akışları küresel/statik, kiracı farkında değil (RLS/`TenantId`, `EntitlementBehaviour`, denetim hattı, KVKK imhası atlanır); ek çalışma zamanı (JVM/Node) ve ikinci yetki modeli. Lisans: Camel/NiFi Apache-2.0 `[K]`; **n8n Sustainable Use License** ürüne gömüp müşteriye sunmayı kısıtlar `[K]`/hukuk onayı; MuleSoft/WSO2 ticari `[?]`. Conductor zaten var (K9) ama M9G D2 aynı gerekçelerle (PII motorda, ayrı imha, kiracıya dinamik tanım uymaz, H1 girdi güveni) kural motorunu Conductor'a yazmadı — bağlayıcılar için de aynı sonuç. C, **tek müşterinin kendi entegrasyon ekibi** olduğu dağıtımlarda bağımsız olarak mümkündür (D ile).
- **D:** Faz 0'da yeterli değer verir (S1, S4 kısmen) ve sıfır yeni saldırı yüzeyidir; ama e-imza (sağlayıcıyı bizim çağırmamız gerekir) ve SFTP (CRM'in dosya çekmesi/göndermesi) müşteri tarafından çözülemez; ERP tarafında da uzlaştırma/hata kuyruğu müşteride kalır.

**Duyarlılık:** kapsam ağırlığı 20→10 olursa (yeniden normalleştirilince) D ≈ 79, B ≈ 86: fark daralır, sıra değişmez; yani ihtiyaç yalnız ERP ve müşterinin güçlü entegratörü varsa D yeterlidir. Grup şirketleri için e-imza + SFTP + karma ERP beklendiğinden B seçilir; **D, B'nin Faz 0'ı olarak** uygulanır.

### 2.1 B'nin şekli (özet)

- **Modül:** `Sense.Crm.Modules.Connectors.{Domain,Application,Contracts,Infrastructure,Api}`, şema `connectors`, kapı modülü `connectors` (M7), limit `maxConnections`. `Integrations` genişletilmez: o modül "dışarıya açılan yüzey" (API anahtarı/webhook), bu modül "üçüncü tarafa kimlik bilgisiyle giden" bağlantıdır; yetki ve tehdit modeli farklı. Hiçbir modül Connectors'a bağlanmaz (mimari test).
- **Kiracı varlıkları:** `connection_profiles` (tür: `erp:<sağlayıcı>|sftp|esign:<sağlayıcı>`, hedef, şifreli sır, durum, ana bilgisayar anahtarı parmak izi), `connector_jobs` (tanım: yön/varlık/zamanlama), `job_runs` + `job_run_items` (sayaçlar, hata kodu; **yük PII taşımaz**), `external_refs` (`entity, entityId, profileId, externalId, externalHash, lastSyncedAt, origin`), `file_ledger` (SFTP), `signature_requests` (e-imza). Küresel teknik kuyruk `connector_queue` (M8B `delivery_queue` deseni: `FOR UPDATE SKIP LOCKED`, kiracı başına adil dilim, kira).
- **Akış (giden):** sahip modülün outbox integration event'i → Connectors işleyicisi `connector_queue` + iş satırı (aynı `SaveChanges`) → Worker rolü `connectors` → adaptör çağrısı (harici referans var mı → güncelle, yok → oluştur) → `external_refs` yazımı → uzlaştırma turu farkları kapatır. Yeniden deneme M8B ile aynı sınıflama (geçici/terminal), üstel geri çekilme, ölü mektup = **hata kuyruğu** (yeniden dene / yoksay / elle çöz; toplu yeniden dene).
- **Akış (gelen):** zamanlanmış (`LockedJobService`, advisory kilit) çekme → hazırlama → sahip modülün **dar senkron portu** (Contracts'ta `I<Varlık>SyncPort.UpsertByExternalRefAsync`; M9G `IBulkActionHandler`/`ILeadIntake` deseni; dispatcher üzerinden **değil**) → `ConnectorActor` (`AutomationActor` gibi: `UserId=null`, "Bağlayıcı: <profil>", `Origin=connector`, `CorrelationId=connector:<run>:<item>`). `Origin=connector` olayları M9G döngü korumasıyla uyumlu olarak kural motorunu tetiklemez (varsayılan) ve ERP'ye geri gönderilmez (yankı önleme).
- **Gözlem:** `CrmMetrics`'e düşük kardinaliteli sayaçlar (`connector_runs{kind,outcome}`, kuyruk yaşı, hata kuyruğu boyutu); kiracı kimliği etiket değil (K20).
- **Web:** Ayarlar → Entegrasyonlar altında "Bağlayıcılar" sekmesi: profil sihirbazı (sır yazma-yalnız, bağlantıyı sına, parmak izi onayı), iş listesi, çalıştırma geçmişi, **hata kuyruğu + yeniden dene**, eşleme/uzlaştırma raporu.
- **İzin:** `org.connectors.manage` (profil/iş/sır), `org.connectors.read` (günlük/kuyruk); `org.*` olduğundan **API anahtarı taşıyamaz** (M8B D10).

## 3. Güvenlik

### 3.1 Kimlik bilgisi saklama

| Konu | Karar |
|---|---|
| Şifreleme | M8B `WebhookSecretProtector` biçimi yeniden kullanılır: AES-256-GCM, `keyId`, **AAD = kiracı‖profil‖sürüm‖amaç** `[D m8b D7]`. Not: bu **doğrudan ana anahtar** şifrelemesidir, DEK/KEK zarf şifrelemesi veya KMS değildir; M8C yalnız SSE-S3 için statik KMS anahtarı kullanır `[D]`. ERP parolası/özel anahtar uzun ömürlü ve değerli olduğundan **v1: aynı biçim + `keyId` (KMS'e geçiş = yeniden şifreleme işi)**; v2: profil başına DEK + KEK sarmalama ve isteğe bağlı harici KMS/Vault (karar #12). |
| Çıkarım | `SecretProtector` ve `SsrfGuard/IpClassifier/SafeTransport` şu an `Integrations.Application/Infrastructure` içinde `[D]`; modül sınırı gereği Connectors bunlara bağlanamaz → **`Shared` altında çıkarım kartı (X3-0)**; Integrations tüketici olur, davranış değişmez (mevcut testler bekçi). |
| API yüzeyi | Sır **yazma-yalnız** (`secretHint`), yanıtta/günlükte/denetimde yok (`SensitiveFields`), döndürme + grace. Sunucu üretimli SFTP anahtar çifti: özel anahtar CRM'de şifreli üretilir, **yalnız ortak anahtar** gösterilir (özel anahtarın e-posta/sohbetle taşınması ve sızması önlenir). |
| Bellekte | Özel anahtar/parola yalnız `connectors` Worker rolünde, kısa ömürlü; diske yazılmaz; OAuth2 belirteçleri bellekte (kısa ömür), yenileme sırrı şifreli. API rolünün profil sırrını çözme yetkisi/anahtarı **yok** (yalnız Worker'da `Connectors:Encryption` anahtarı). |
| Anahtar kaybı/yedek | M8B runbook §4/§7 ile aynı: anahtar yedekle **ayrı** korunur; kayıp = tüm profillerde sır yeniden girilir. |

### 3.2 Çıkış (egress) ve ağ bölümleme

Mevcut durum `[D m8b D6]`: `backend` `internal`; çıkış yalnız `egress` profilindeki `egress-dns` + `egress-proxy` (Squid; `CONNECT <IP>:443|8443`; özel CIDR'lar yalnız operatör `AllowedPrivateCidrs`); Worker'a doğrudan internet yok. M8A `notification-worker` deseni: ayrı Worker rolü + ayrı ağ.

| Hedef türü | Örnek | Yol | Not |
|---|---|---|---|
| Genel HTTPS (bulut ERP/imza sağlayıcı) | Dynamics 365 BC, SAP bulut, ESHS API | M8B egress-proxy (443) | `AllowedHosts` ile daraltılabilir; kiracı yöneticisi seçebilir (varsayılan) |
| Şirket içi ERP (özel IP, özel port) | Logo REST 32001, Mikro 8084/8094 `[İ]`, SAP SL | **Operatör hedef izin listesi** `Connectors:Targets` (host/CIDR + port + protokol); proxy ACL aynı listeden üretilir | **Kiracı yöneticisi keyfî iç hedef giremez**: SaaS'ta bu, iç ağa SSRF olur; profil yalnız izinli hedeflerden birine bağlanır veya platform yöneticisi onaylar |
| SFTP (22 veya özel) | banka, ERP dosya paylaşımı | Seçenek 1 (öneri): Squid `CONNECT ip:port` ACL'ine izinli SFTP portları eklenir, SSH.NET HTTP proxy ile bağlanır `[?]` spike; Seçenek 2: `connectors` ağı + ana bilgisayar güvenlik duvarı (nftables) izin listesi | Tek denetim noktası tercih; SSH için başarılı bir ACL proxy'si standart değildir |

Kararlar: (1) SSRF savunması M8B'deki gibi **çöz → doğrula → IP'ye sabitle**, yönlendirme yok, kalıcı engel listeleri; SFTP için de aynı (ana bilgisayar adı yerine doğrulanmış IP'ye bağlanılır, **ana bilgisayar anahtarı pinlenir**). (2) `connectors` Worker rolü ayrı ağda; ana Worker ve API bağlayıcı hedefine ulaşamaz. (3) Varsayılan **kapalı** (`Connectors:Enabled=false`, hedef listesi boş). (4) Egress proxy erişim günlüğü (hedef IP/zaman/bayt) denetim kanıtıdır; içerik yok. (5) ERP tarafında bağlayıcı sunucusunun IP'sine kısıtlı servis hesabı.

### 3.3 SFTP'ye özgü

- **Ana bilgisayar anahtarı pinleme:** profil oluşturmada parmak izi (SHA-256) yöneticiye gösterilir, **açık onayla** pinlenir (TOFU değil, onaylı ilk kullanım); her bağlantıda `HostKeyReceived` karşılaştırması; uyuşmazlık → bağlantı reddi, profil `hostKeyChanged`, iş durur, uyarı; değişiklik ancak yönetici onayıyla (iki anahtar birlikte pinlenebilir: döndürme). "Her anahtara güven" seçeneği yok.
- **Algoritma politikası:** `ssh-rsa`(SHA-1)/DSA/zayıf KEX kapalı; eski banka sunucusu bunu şart koşarsa **istisna kaydı + karşı taraftan yükseltme talebi** (karar #11). SSH.NET: MIT lisanslı `[K]`; strict KEX (Terrapin, CVE-2023-48795) 2024.1.0'da (2024-06-28, PR #1366) `[K]`; DSA 2025.0.0'da kaldırıldı `[İ]`; 2026.0.0 (2026-08-09) `[K]` .NET 10 desteğiyle `[İ]`. Sürüm `packages.lock.json` ile sabit (L10). Alternatifler: Rebex (ticari), WinSCP .NET (GPL + harici exe) → önerilmez.
- **Yol geçişi/SSRF (dosya):** uzak dosya adları **güvenilmez veridir**: yerel disk yolu olarak **asla** kullanılmaz (yerel hazırlama adı GUID, M8C `ObjectKey` kuralı); uzak dizin yalnız profildeki temel dizin altında, **özyineleme yok** (veya sınırlı derinlik), sembolik bağ izlenmez, ad regex allow-list (`[A-Za-z0-9._-]{1,128}`), `..`/mutlak yol/denetim karakteri reddedilir; dosya sayısı/boyut/satır tavanı; zip **açılmaz** (M8C yalnız merkezi dizin okur). Giden dosya adları şablondan (`siparis_{tarih}_{işKimliği}.csv`), kullanıcı metninden değil (M9B dışa aktarma kuralı).
- **Yazma güvenliği:** `.tmp` adıyla yükle → doğrula (boyut) → rename; çekmede dosya sabitlenene (boyut/mtime iki turda aynı) kadar bekle; işlenenler `file_ledger`'da (ad+boyut+SHA-256); işlenmiş dosyayı taşıma/silme profil ayarı (varsayılan: `archive/` altına taşı, silme yok).
- **Şifreleme:** SFTP taşıma şifreli; iş kuralı olarak PGP (banka) `[?]` — port `IPayloadCodec` ile ertelenir, v1 yok.
- **Virüs tarama:** gelen tüm dosyalar ayrıştırmadan önce M8C `IFileScanner` portundan geçer `[D]`; `Provider=none` ile Production'da SFTP-içeri **açılmaz** (öneri): ClamAV adaptörü (M8C "gelecek iş") bu kartın önkoşuludur, `FailMode=closed`, sonuç `quarantined`. Not: `IFileScanner`/`IFileStorage` `Files.Application`'da; Connectors'a açılması için `Files.Contracts`'a dar bir "hazırlama deposu" portu gerekir (yoksa Connectors kendi kiracı önekli nesne alanını kullanır — karar Spec'te).

### 3.4 ERP'ye özgü

- **En az yetkili servis hesabı:** bağlayıcı için ayrı ERP kullanıcısı/lisans; yalnız gereken belge türleri (satış siparişi oluştur, cari/ürün/fiyat oku); yönetici/muhasebe fişi yetkisi yok; kaynak IP kısıtı ERP tarafında; okuma bağlayıcıları salt okunur hesapla; parola/anahtar döndürme takvimi ve süre dolumu uyarısı.
- **Veritabanı düzeyinde erişim yok:** ERP şemasına doğrudan SQL yazma desteklenmez (lisans/destek, bütünlük, işlem kilidi); Mikro V17'de doğrudan DB erişimin kapatıldığı ikincil kaynakta belirtiliyor `[İ]`. Yalnız resmi API (REST/OData/SOAP) veya dosya-bırakma.
- **Kimlik doğrulama zayıflıkları:** Mikro API oturumu MD5 tabanlı parola özeti kullanıyor `[İ]` → yalnız TLS/iç ağ üzerinde, sır asla günlükte; Logo REST belirteci (`/api/v1/token`, ClientId/Secret Logo çözüm ortağından) `[İ]`.
- **Uzlaştırma:** gecelik `external_refs` ↔ ERP delta karşılaştırması; fark raporu (CRM'de var ERP'de yok, tersi, tutar/durum farkı); otomatik düzeltme yalnız güvenli sınıflarda, kalanı hata kuyruğunda.

### 3.5 Denetim, izolasyon, KVKK

- **Denetim:** profil yaşam döngüsü `IAuditLogged` (sır/URL maskeli); her çalıştırma satırı (kim/ne/sayaç/süre/durum/hata kodu); dosya defteri (ad, boyut, SHA-256); ERP yükü **saklanmaz** (yalnız kimlik/durum/hash; hata ayıklama için PII'siz kısa özet, 30 gün); günlükte dosya adı/URL/sır yok (M8C günlük kuralı).
- **Kiracı izolasyonu:** tüm tablolar `ITenantEntity` + filtre + `(tenant_id,…)` indeks; küresel `connector_queue` yalnız teknik (M8B deseni, `BeginScope` sonra); **yeni `IgnoreQueryFilters` yok**; çapraz-kiracı testi zorunlu (profil, iş, defter, dosya). Kiracı başına eşzamanlılık/dakika kotası + **hedef başına devre kesici** (yavaş ERP diğer kiracıları açlığa itmez). Hazırlama dosyaları `{tenantId}/…` önekli, her adaptör önek kiracısını bağlamla karşılaştırır (M8C K21).
- **KVKK (`[K]`):** m.9, 7499 sayılı Kanun ile (RG 2024-03-12, yürürlük 2024-06-01) değişti: yeterlilik kararı → uygun güvenceler (standart sözleşme, bağlayıcı şirket kuralları; standart sözleşme imzadan itibaren **5 iş günü içinde Kurul'a bildirim**) → istisnai haller. Çıkarımlar: (1) Veri sorumlusu kiracıdır (M8B ile aynı); (2) ERP/imza sağlayıcısı yurt dışında ise (ör. bulut ERP bölgesi, DocuSign/Adobe) yurt dışına aktarımdır; **varsayılan olarak yalnız `hostingCountry=TR` beyanlı sağlayıcı/hedef etkin**, yurt dışı için `Connectors:AllowForeignProviders=true` + kiracı onayı + runbook uyarısı (`hostingCountry` operatör beyanıdır, doğrulanamaz `[?]`); (3) **veri asgarileştirme:** eşleme açık izin listesiyle (PII yok/asgari: cari için ad/VKN/adres gerekir; kişi e-posta/telefon varsayılan gönderilmez); (4) imza için belge sağlayıcıya gider → sağlayıcı veri işleyen (m.12 sözleşmesi); belge yerine **özet (hash) imzalama** mümkünse tercih (sağlayıcı belgeyi görmez; imza/özet biçimi sağlayıcıya bağlı `[?]`); (5) kiracı imhasında (M7) `TenantDataEraser` + `ConnectorsQueueEraser` + hazırlama dosyaları (`FilesObjectEraser` sırası) — **harici sistemde (ERP/sağlayıcı) kalan veri** silinmez, runbook'ta açıkça yazılır.

### 3.6 E-imzanın hukuki değeri ve kanıt

**Doğrulanabilenler `[K]` (5070 md. 3, 4, 5, 8, 13 özet; HMK 205; TBK 15):**
- Güvenli e-imza (md. 4: imza sahibine münhasır bağlı, güvenli araçla oluşturulan, **nitelikli sertifikaya** dayalı, değişikliği tespit ettiren) elle atılan imza ile **aynı hukuki sonucu** doğurur (md. 5); TBK md. 15 yazılı şekil için güvenli e-imzayı el yazısıyla eş tutar; HMK md. 205: usulüne uygun güvenli e-imzalı elektronik veri **senet hükmündedir**, hâkim belgenin güvenli e-imzayla oluşup oluşmadığını resen inceler.
- **İstisnalar (md. 5):** kanunun resmî şekle veya özel merasime tabi tuttuğu işlemler (noter/tapu vb.) ile banka teminat mektupları ve TR'de yerleşik sigorta şirketlerinin kefalet senetleri dışındaki teminat sözleşmeleri güvenli e-imzayla yapılamaz.
- **Zaman damgası** (md. 3): bir verinin üretildiği/gönderildiği/kaydedildiği zamanın **ESHS tarafından e-imzayla doğrulanan kaydı**; ESHS md. 8 ile BTK'ya bildirimle faaliyete geçer; BTK listesinde (2026-01-09 güncel) 8 kuruluş: E-Güven, TÜBİTAK (yalnız kamu), TürkTrust, E-Tuğra, EGMSM (yalnız EGM), E-İmzaTR, Ayyıldız İmza, Arkimza (hangisinin nitelikli sertifika/zaman damgası/mobil imza sunduğu sayfadan okunamadı).
- **Sınırlar:** yalnız **güvenli** e-imza için otomatik "elle imza eşdeğeri" vardır; "tıkla-onayla / e-posta OTP / SMS OTP" **basit** kanıttır `[?: md. 3'teki "elektronik imza" tanımı özetten çıkarılmadı]`: bağlayıcı olup olmadığı taraf delil sözleşmesi, sözleşme türü ve mahkemenin serbest takdirine bağlıdır (HMK md. 193 delil sözleşmesi `[?]`). Bu nedenle **teklif kabulü** (düşük risk) için basit kanıt + hukuk onayı, **sözleşme/taahhüt** için güvenli e-imza/mobil imza önerilir (karar #7).

**Doğrulayamadıklarım `[?]`:** (a) yurt dışı sağlayıcının (eIDAS nitelikli) imzasının TR'de 5070 anlamında güvenli e-imza sayılıp sayılmadığı (AB dışı; muhtemelen sayılmaz, hukukçuya sorulmalı); (b) mobil imzanın (Turkcell/Vodafone/Türk Telekom) güvenli e-imza statüsü ve operatör sözleşme koşulları `[İ]`; (c) mali mühürün GİB dışı sözleşme imzasında kullanımı; (d) saklama süreleri (ör. TTK md. 82, 10 yıl — hafızadan); (e) KEP'in delil/tebligat değeri ayrıntısı: 7201 md. 7/a (2013-01-19) ile anonim/limited/sermayesi paylara bölünmüş komandit şirketlere elektronik tebligat zorunlu `[K]`; CRM'in KEP gönderimi bu kartta **yok** (S-dışı, adaptör adayı).

**Kanıt (inkâr edilemezlik) tasarımı:** (1) gönderim anında PDF baytlarının SHA-256'sı kilitlenir; imzalanan = o baytlar (Files'ta değişmez sürüm); (2) imzalayıcı kimliği sertifikadan (ad, TCKN/VKN alanları) çıkarılır ve CRM'deki karşı tarafla eşleştirilir; uyuşmazlık = imza reddi; (3) olay defteri: gönderildi/görüntülendi/imzalandı/reddedildi + IP/UA/zaman (append-only, denetim hattı); (4) imzalı dosya + **doğrulama raporu** (sertifika zinciri, iptal durumu, zaman damgası) saklanır; (5) **uzun dönem doğrulama:** ETSI EN 319 142-1 (PAdES-B-B → B-T → **B-LT** → B-LTA) `[K]`: iptal bilgisi (OCSP/CRL) imza anında gömülür (CRL/OCSP yanıtları süreli), **arşiv zaman damgası** (RFC 3161) periyodik yenilenir; TSA olarak ESHS (E-Tuğra zaman damgasını birim başına fiyatlıyor `[K]`); (6) CRM sunucu saati kanıt değildir; zaman kanıtı yalnız TSA belirtecidir; (7) CRM imzalayanın özel anahtarına **hiç** dokunmaz (akıllı kart/HSM/mobil operatör/uzaktan imza).
**Doğrulama kütüphanesi notu:** AB DSS (LGPL-2.1, Java) `[K]` PAdES/XAdES/CAdES/ASiC üretir ve doğrular ama AB güven listeleriyle çalışır; **TR kök/ara sertifika güven çıpaları** ve ilke ayarı gerekir `[?]` → spike. .NET tarafında seçenek: sağlayıcı REST API'si (E-Tuğra PAdES/CAdES/XAdES, HSM, bulut `[K]`) veya TÜBİTAK ESYA/KamuSM kütüphaneleri `[İ]`.

## 4. Öneri ve yol haritası

### 4.1 Mimari kararlar (öneri)

1. B seçeneği; `Connectors` yeni modül, `Integrations` değişmez (yalnız çıkarılan kodun tüketicisi).
2. Ortak çıkarım (`Shared`): `SecretProtector`, `SsrfGuard`/`IpClassifier`, güvenli taşıyıcı, hedef izin listesi (`host/CIDR+port+protokol`). Varsayılan kapalı.
3. Sahip modül senkron portları (`Sales.Contracts`, `Commerce.Contracts`): `UpsertByExternalRef`, alan bazlı sahiplik meta verisi. `external_refs` Connectors'ta; sahip modüle "ERP kaynaklı" bayrağı ve kilitli alan kümesi.
4. Sıra: ERP-agnostik önce (çerçeve, SFTP, dosya-bırakma) → ilk REST ERP (keşifle) → PDF → e-imza.
5. E-fatura: v1'de kesme yok; ERP fatura durumu okuma (S4).

### 4.2 Faz ve kartlar (Spec→Backend+Web; EW = Spec/BE/Web/DevOps; ±%35)

| Kart | İçerik | Bağımlılık | Spec | BE | Web | DevOps | Top. |
|---|---|---|---|---|---|---|---|
| **C-X3-1** (Faz 0) Open API/webhook güçlendirme | `invoice.*` webhook türleri (M9C olayları var, katalog v1'de yok), `updatedSince` süzgeci, `Idempotency-Key`, harici referans alanı (M8D özel alan mı tek kolon mu: Spec), rehber güncellemesi | M8B merged; M9C (fatura olayları) | 0.5 | 2 | 0.5 | – | **3** |
| **C-X3-0** Ortak egress/sır çıkarımı + bağlayıcı ağ profili | `Shared`'a çıkarım (davranış aynı, mevcut Integrations testleri bekçi); hedef izin listesi (port+protokol); Squid ACL üretimi; `connectors` Worker rolü/ağ; compose/runbook | M8B | 0.5 | 2 | – | 1.5 | **4** |
| **C-X3-2** Connectors çekirdeği | Modül, profil/sır/parmak izi onayı, iş motoru, kuyruk, hata kuyruğu + yeniden dene, uzlaştırma iskeleti, metrikler, plan kapısı/limit, KVKK imha, izinler; web sekmesi | X3-0 | 1.5 | 7 | 3.5 | 0.5 | **12.5** |
| **C-X3-3** Senkron portları | `ISyncPort` (Sales: firma/kişi; Commerce: ürün/sipariş; sonra fatura), `ConnectorActor`, `Origin=connector`, yankı önleme, alan sahipliği | X3-2 (paralel), M9E `taxNumber/accountNumber`, M9C | 0.5 | 3 | – | – | **3.5** |
| **C-X3-4** SFTP bağlayıcı | Profil (anahtar çifti/parmak izi), çekme/gönderme işleri, defter, kısmi dosya kuralları, tarayıcı entegrasyonu, M9B dışa aktarma köprüsü (küçük); ClamAV adaptörü ayrı M8C-devam kartı (+1.5 BE) | X3-2, X3-3 | 0.5 | 3.5 | 1.5 | – | **5.5** |
| **C-X3-5** ERP dosya-bırakma bağlayıcısı | CSV/XML sözleşmesi + sürümlü eşleme şeması, kod sayfası (Windows-1254), akışlı okuyucu (100k satır), hata satır raporu | X3-4 | 0.5 | 3 | 1 | – | **4.5** |
| **C-X3-6** ERP REST bağlayıcı #1 | Keşifle seçilen ERP (Logo Tiger REST / SAP B1 SL / Mikro / BC…): S1–S3 (+S4); sözleşme testleri (kayıtlı fikstür); ilk yükleme hız yönetimi. **Her ek ERP: ~4–6 EW** | X3-3, keşif + sandbox | 0.5 | 5 | 1.5 | – | **7** |
| **Faz 1 toplamı** (X3-0,2,3,4,5,6) | | | | | | | **≈ 37** |
| **C-X3-7** Belge PDF üretimi | Teklif/sözleşme şablonundan PDF (M9C/M9J "şablonlar" kapsam dışıydı); kütüphane lisansı doğrulanmalı `[?]` (ör. QuestPDF Community sınırı, iText AGPL) | M8C | 0.5 | 3.5 | 1.5 | – | **5.5** |
| **C-X3-8** E-imza çekirdeği + 1 sağlayıcı | `ISignatureProvider`, durum makinesi, olay defteri, imzalı dosya + rapor, PAdES-B-LT/LTA yenileme işi, yoklama; sağlayıcı adaptörü (TR ESHS, sandbox erişimine bağlı) | X3-2, X3-7, hukuk onayı | 1 | 7 | 3 | – | **11** |
| **C-X3-9** Harici imzacı yüzeyi | Halka açık, ayrı ana bilgisayarlı imza sayfası (M9G D21 deseni) veya X4 portal içinde | X3-8, X4 kararı | 0.5 | 2 | 1 | 0.5 | **4** |
| **Faz 2 toplamı** | | | | | | | **≈ 20.5** |
| Opsiyonel: ek ERP adaptörleri, e-fatura entegratör adaptörü (~5/entegratör), KEP, hosted SFTP, PGP, KMS/DEK | | karar | | | | | 10–20 |

**Takvim:** Faz 0 hemen; X3-0 ile paralel başlar. X3-2/3/4/5 Faz 1'de 2–3 paralel ajanla ~14–18 takvim haftası (kartlar arası sıcak dosya çakışmaları: `ModuleCatalog`, Migrator/Worker `Program.cs`, `Permissions.cs`, TestFixture Respawn, runbook/compose). Sıralama önerisi: M9B/M9C/M9E merge sonrası X3-3 (M9C faturası, M9E alanları); X3-0/X3-2 bunlardan bağımsız başlayabilir.

**Test kapısı (her kart):** kiracı çapraz testi, SSRF/hedef listesi matrisi (mevcut M8B testleri desen), parmak izi uyuşmazlığı, yol geçişi/kötü ad fikstürleri, idempotency (aynı sipariş iki kez → ERP'de tek), yankı döngüsü, hata kuyruğu yeniden dene, askı/plan kapısı, sırların günlükte/yanıtta bulunmadığı test, imha.

## 5. Riskler ve spike ihtiyaçları

| # | Risk / bilinmeyen | Etki | Spike (süre, çıktı) |
|---|---|---|---|
| R1 | **Grup gerçekte hangi ERP'leri, hangi sürüm/barındırma ile çalıştırıyor?** (Logo/Netsis/Mikro/SAP/Oracle/Dynamics; şirket bazında) | X3-6 seçimi, efor ×2 | Keşif çalıştayı 2 gün: şirket × ERP × sürüm × barındırma × API lisansı × entegratör/partner tablosu |
| R2 | ERP API'lerinin yeteneği: idempotent oluşturma, harici referansla arama, delta okuma, hız sınırı, lisans/edition (ör. Logo REST Tiger 3/Wings'te var, Go3/Start/Plus'ta yok `[İ]`; Mikro API anahtar lisansı + başvuru, demo 100 kayıt `[K]`; BC webhook aboneliği 3 günde sona erer, yenilenmeli `[K]`; SAP SL OData v4 `/b1s/v2`, FP2405'ten itibaren `[İ]`) | Tasarım (uzlaştırma yükü) | Her aday ERP için 2 gün sandbox: oluştur/ara/güncelle/delta/limit ölçümü |
| R3 | **Sandbox/test ortamı erişimi** (ERP ve e-imza sağlayıcı): kim, ne zaman, hangi sözleşmeyle | Takvim (dış bağımlılık) | Şimdi talep açılır; sağlayıcı sözleşmeleri (ESHS API fiyat/koşul `[K: yalnız örnek liste fiyat]`) |
| R4 | E-imza: harici imzacıların **elinde nitelikli e-imza/mobil imza var mı?** UX (akıllı kart/istemci gereksinimi) | Benimsenme | 5–10 gerçek müşteri kişisiyle görüşme; mobil imza akışı denemesi |
| R5 | **Hukuki görüş:** basit kabul vs güvenli e-imza kapsamı, yurt dışı sağlayıcı, saklama süresi | E-imza kapsamı | Hukukçu görüşü (kartın dışı, PO) |
| R6 | PAdES-B-LT üretimi ve **bağımsız doğrulama** (TR güven çıpaları, OCSP/CRL erişimi = egress) | E-imza doğruluğu | 3 gün: sağlayıcı sandbox'ında imzala → başka doğrulayıcıyla (DSS demo/Acrobat) doğrula → egress ihtiyaçlarını listele |
| R7 | SSH.NET: strict KEX'in sabitlenecek sürümde etkin olduğu, HTTP proxy ile bağlantı, büyük dosya akışı (bellek), eski sunucu algoritma uyumu | SFTP güvenliği | 2 gün: sürüm 2026.0.0, Docker `connectors` ağı + Squid CONNECT:22, 1 GB akış, parmak izi olayı, eski sunucu fikstürü |
| R8 | **CSV formül önek kuralı (M9B `'`) makine tüketimini bozar** (negatif metin, `=`/`@` ile başlayan kodlar) | Veri bozulması | Makine dosyaları için ayrı yazıcı profili (formül öneki yok, katı alıntılama) — Spec kararı; insan CSV'si değişmez |
| R9 | İki yönlü cari senkronunda çakışma/yankı, yinelenen cari, birleştirme/silme semantiği | Veri kalitesi | Alan sahipliği matrisi (karar #4) + prova: 1 000 cari ile kuru çalıştırma raporu |
| R10 | İlk toplu yükleme (10⁵ kayıt) ve ERP hız sınırı (BC: kullanıcı başına 5 dk'da 6 000 istek `[İ]`) | Süre/başarısızlık | Toplu mod: sayfalama, kuyruk kotası, gece penceresi |
| R11 | Tek Worker varsayımları: bellek içi jeton kovası/devre kesici çok kopyada paylaşılmaz (runbook §9.1 `[D]`) | Ölçekte adalet | Kopya başına belgele; ölçek gerekirse Redis/DB tabanlı |
| R12 | Yurt dışı sağlayıcı/bulut ERP → KVKK m.9 yükümlülükleri (standart sözleşme + 5 iş günü bildirim) | Uyum | PO/KVKK sorumlusu; `hostingCountry` beyanı + varsayılan kapalı |
| R13 | Kapsam kayması: "her ERP", "her banka biçimi", e-fatura kesme | Efor | Bu belgedeki kapsam sınırları + karar listesi |
| R14 | Sağlayıcı/ERP API sürüm değişimi (SAP SL v1→v2 gibi) | Bakım | Adaptör yetenek sürümü, kayıtlı fikstürlü sözleşme testleri, izleme |
| R15 | PDF kütüphane lisansı | Ticari | X3-7 Spec'te lisans denetimi |

## 6. Ürün sahibi kararları (öneri ile)

| # | Soru | Önerim |
|---|---|---|
| 1 | Hangi ERP(ler) ilk? Şirketler hangi ERP'yi kullanıyor? | 2 günlük keşif sonrası; en büyük hacimli şirketin ERP'si ilk REST adaptörü. O zamana kadar ERP-agnostik çerçeve + SFTP/dosya-bırakma + Faz 0. |
| 2 | Resmi fatura kaynağı kim? (CRM faturası ticari/pro-forma mı, yoksa ERP'ye mi gider?) | **ERP kaynak sistem**; M9C faturası ticari kalır; ERP→CRM fatura no/durum/PDF bağlantısı (S4). S6 ertelenir. |
| 3 | E-fatura/e-arşiv/e-irsaliye kapsamı? | CRM'de **kesme yok**; yalnız ERP'nin kestiğinin referansı. Entegratör API adaptörü ayrı karar (eşik/hadler: 2025 hasılatı ≥ 3 M TL için 2026-07-01'de e-Fatura zorunluluğu `[İ]`, VUK GT sıra no kaynaklarda tutarsız: 535 vs 589 → doğrulanmalı). |
| 4 | Ana veri sahipliği? | Firma: ERP = cari kod, VKN/TCKN, vergi dairesi, vade, risk limiti; CRM = iletişim kişileri, sahip, aşama, notlar. Ürün/fiyat listesi: ERP. Sipariş: CRM ERP kabulüne kadar; sonra ERP durumu. Çakışmada "son yazan" yok. |
| 5 | Yeni `Connectors` modülü mü, `Integrations` genişlesin mi? | Yeni modül; kapı bayrağı `connectors`, `maxConnections` limiti, varsayılan kapalı. |
| 6 | Hedef modeli: kiracı yöneticisi serbest mi? | Genel HTTPS: kiracı (isteğe bağlı `AllowedHosts`). Özel IP/SFTP/özel port: **yalnız operatör izin listesi + platform onayı**. |
| 7 | E-imza seviyeleri | Teklif kabulü: basit kanıt (bağlantı+olay defteri, OTP hazır olunca) **hukuk onayıyla**; sözleşme/taahhüt: güvenli e-imza/mobil imza (TR ESHS). Kurum (sunucu) imzası ve mali mühür v1 dışı. |
| 8 | Yurt dışı e-imza/SaaS sağlayıcıları | Varsayılan **kapalı**; yalnız `hostingCountry=TR` sağlayıcılar. Açma kararı KVKK sorumlusunda. |
| 9 | Hosted SFTP sunucusu (partner push) | v1'de yok; yalnız CRM istemci (pull/push). |
| 10 | Virüs taraması | ClamAV `FailMode=closed` olmadan Production'da SFTP-içeri/harici dosya kabulü **kapalı**. |
| 11 | Eski SSH algoritmaları (ssh-rsa/SHA-1) | Reddet; yalnız kayıtlı, süreli istisna + partnere yükseltme talebi. |
| 12 | Sır saklama | v1: M8B AES-GCM ana anahtar (Docker secret); v2: DEK/KEK + Vault/KMS **müşteri isterse**. Müşteride HSM/Vault var mı? |
| 13 | PDF üretimi kimin? (X3-7 mi, M9J şablon kartı mı?) | Tek kart; X3-7 M9J ile birleştirilir ki iki şablon motoru doğmasın. |
| 14 | Harici imzacı yüzeyi: X4 portalı mı, ayrı halka açık sayfa mı? | Ayrı, tek kullanımlık imzalı bağlantıyla sayfa (M9G WebForms deseni); X4 gelince taşınabilir. |
| 15 | Sandbox/sözleşme sahipliği ve bütçe (ESHS API, ERP API lisansları: Mikro API anahtarı, Logo REST partner erişimi) | Bu hafta sorumlu atanır; Faz 1 başlangıcının kapısı. |
| 16 | Eşzamanlı ajan/ekip: Faz 1 için 2–3 paralel ajan kabul mü? | Evet; Faz 2 X4/hukuk görüşü sonrası. |

## 7. Kapsam dışı (bu analizde)
E-fatura/e-arşiv/e-irsaliye kesme ve GİB entegratör API'leri, KEP gönderimi, PGP/banka H2H ödeme dosyaları, ERP'ye doğrudan veritabanı erişimi, genel eşleme/ETL DSL'i, iPaaS ürünü işletimi, hosted SFTP sunucusu, gelen webhook (M8B kapsam dışı; e-imza durumları önce yoklama), stok yönetimi.

## 8. Kaynak ve doğrulama kaydı (erişim: 2026-09-20)

| Konu | Kaynak | Durum |
|---|---|---|
| 5070 md. 3 (zaman damgası, ESHS), md. 4, 5, 8, 13 | alomaliye.com 5070 metni; mevzuat.gov.tr/resmigazete bağlantıları arama sonucunda | `[K]` özet çıkarıldı; tam metin hukukçuyla doğrulanmalı |
| TBK md. 15, HMK md. 205 | mevzuat.gov.tr, barandogan.av.tr, saimincekas.com arama sonuçları | `[K]` (arama özeti) |
| BTK ESHS listesi (8 kuruluş, güncelleme 2026-01-09) | btk.gov.tr/elektronik-sertifika-hizmet-saglayicilari | `[K]` |
| E-Tuğra API (PAdES/CAdES/XAdES, HSM, bulut; örnek fiyatlar) | e-tugra.com.tr/api-entegrasyon | `[K]` satıcı sayfası; fiyat tarihsiz |
| Mobil imza (Turkcell/Vodafone/Türk Telekom), İzometri imzalayıcı | bthaber/fortuneturkey arama özeti | `[İ]` |
| SSH.NET: MIT, sürümler (2025.0.0 2025-04-18; 2025.1.0 2025-10-27; 2026.0.0 2026-08-09), strict KEX PR #1366 (2024.1.0, 2024-06-28) | github.com/sshnet/SSH.NET releases; nuget.org | `[K]` (.NET 10/DSA maddeleri `[İ]`) |
| KVKK m.9 / 7499 (RG 2024-03-12, yürürlük 2024-06-01, standart sözleşme, 5 iş günü bildirim) | kvkk.gov.tr yurt dışına aktarım sayfaları, hukuk büroları makaleleri | `[K]` |
| e-Fatura eşiği 3 M TL / 500 bin TL, 2026-07-01; e-Arşiv 2026-01-01 | uyumsoft.com/blog (2026-01-08), diğer blog özetleri; **GİB birincil kaynağı okunmadı** | `[İ]` |
| Logo REST Servis (Tiger 3/Wings, port 32001, token) | logoyazilimdestek.com/logo-rest-servis-rehberi | `[İ]` |
| Mikro API (REST/JSON, v16 8084, v17 8094, API anahtarı, başvuru formu, demo 100 kayıt) | apidocs.mikro.com.tr/guides | `[K]` satıcı; "V17'de DB erişimi kapalı" `[İ]` |
| SAP B1 Service Layer OData v4 / `/b1s/v2` (FP2405) | learning.sap.com, sap-b1-blog arama özeti | `[İ]` |
| Dynamics 365 BC API v2.0 OData, webhook abonelik süresi 3 gün, 6 000 istek/5 dk | learn.microsoft.com, getknit.dev arama özeti | `[K]`/`[İ]` |
| Oracle (Fusion/EBS), SAP S/4HANA, Netsis REST | **araştırılmadı** | `[?]` keşifte |
| n8n Sustainable Use License; MassTransit v9 ticari; Camel/NiFi Apache-2.0 | n8n docs; milanjovanovic.tech; ASF | `[K]` |
| PAdES seviyeleri (ETSI EN 319 142-1 V1.2.1, 2024-01), RFC 3161, AB DSS LGPL-2.1 | etsi.org, github.com/esig/dss, ec.europa.eu | `[K]` |
| KEP: 7201 md. 7/a (2013-01-19); TTK md. 18/1525 tabanlı yönetmelik (RG 2011-02-14) | hukuk/PTT KEP sayfaları | `[K]` |
| İç: M8A/M8B/M8C/M9B/M9C/M9E/M9G planları, `Integrations` modül kodu, `Worker/Program.cs`, senseik (`IntegrationConnection`/`ISignatureProvider` yalnız analiz notu; **kod ve SFTP/ERP bağlayıcısı yok**) | bu depo; `C:\Users\ALG0013\Documents\GitHub\senseik` (salt okunur) | `[D]` |
