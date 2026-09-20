# C-X1 — BPMN süreç tasarım stüdyosu: analiz ve karar önerisi

Tarih: 2026-09-20 · Yazar: Spec (araştırma) · Kart: `C-X1` (`docs/team/board.md`) · Kod yazılmadı; yalnız bu belge.
Girdiler: `docs/architecture/mimari.webp` (blok 1 "Süreç Tasarım Stüdyosu", blok 6 "Süreç Yönetimi ve Governance"), `kararlar.md` (K5, K9, K10, K13, K17–K21), `backend.md` §13/§21/§22, `hardening-report.md` (H1, L3), `docs/plan/m4-workflow.md`, `docs/plan/m9g-otomasyon.md` (D2, D8–D13, D22), gerçek kod (`src/Modules/Workflows`, `src/Sense.Crm.Worker/Workflows`), kardeş repo `senseik` (salt okunur).

## 0. Özet ve öneri

- **Bugün:** iki sabit workflow (`crm_lead_assignment`, `crm_deal_approval`) kodda, gömülü JSON olarak; her yapı değişikliği (yeni onay kademesi, paralel hukuk onayı, SLA yükseltmesi) bir yazılım sürümüdür. M4 bunu "kapsam dışı" bıraktı; M9G kısa/senkron kurallar için süreç-içi motor getiriyor ve blueprint/BPMN'i açıkça dışarıda tutuyor.
- **Öneri: E (aşamalı melez), "önce çekirdek, sonra tuval".** Tasarım yüzeyinden bağımsız, kapalı katalog + sürümlü **Süreç Tanımı (TPD)** çekirdeğini ve yönetişimi (sürüm/yayın/onay/geri alma/simülasyon/denetim) kur; ilk yazma yüzeyi form/liste tabanlı adım kurucusu; **bpmn-js tuvali** (kısıtlı BPMN profili) spike geçerse 2. aşamada, TPD üzerinde bir görünüm olarak gelir. Conductor kalır (K9); Camunda/Flowable değil.
- **Üç temel ilke:** (1) TPD kanonik kaynaktır, BPMN XML yalnız istemcide üretilen/okunan görünümdür (sunucu XML ayrıştırmaz). (2) Conductor JSON'u yalnız **yapı** taşır; hiçbir kullanıcı metni/parametresi/ifadesi JSON'a girmez, parametreler sürüm anlık görüntüsüyle CRM veritabanında durur ve görevler H1 deseniyle oradan okur (Conductor `${...}` enjeksiyonu ve tenant başına tanım çoğalması kapanır). (3) Kullanıcı kodu yok: HTTP, INLINE (GraalJS), JSON_JQ, LAMBDA, EVENT, KAFKA, LLM/MCP, dinamik fork, serbest SUB_WORKFLOW **derleyici tarafından üretilemez**.
- **Ön koşul:** OSS Conductor'da kimlik doğrulama/RBAC/çok kiracılılık yoktur (bkz. §3.1); kiracı-yazımlı süreçler açılmadan önce Conductor'un ağ + kimlik doğrulayan vekil arkasına alınması zorunludur (DevOps, ~1 ew).
- **Maliyet:** spike ~2 ew; MVP (çekirdek + yönetişim + form kurucu) ~19 ew; tuval + izleme yer paylaşımı ~+6 ew; toplam ~33 ew (±%40). Sıra: spike şimdi, yapım **M9G merge sonrası** (M9B `IFilterMatcher`, M9G eylem portları, `ActionTemplateEngine` yeniden kullanılır).

## 1. Problem ve gerçek kullanıcılar

**Çözülen problem.** Grup şirketlerinin süreçleri (satış indirim onayı, teklif/sipariş onayı, kredi/limit, servis talebi yükseltmesi, sözleşme onayı, müşteri açılışı) şirketten şirkete kademe, eşik, sorumlu rol ve SLA bakımından farklıdır. M4 yalnız *parametre* (eşik + rol) ayarlatır; *yapı* (kademe eklemek, paralel onay, zaman aşımında yükseltme, koşullu dal) her seferinde Algosense'in kod + dağıtım işidir. Stüdyo, "değişiklik maliyetini" (bekleme + risk) düşürür ve süreci **belgelenebilir/denetlenebilir** kılar (ISO, iç denetim, KVKK "veri işleme envanteri" için süreç tanımı).

**Kim kullanır (varsayım: doğrulanmadı, kullanıcı görüşmesi gerekir — bkz. §7 K1):**

| Persona | Sayı (tahmin) | İhtiyaç | Stüdyodan beklenti |
|---|---|---|---|
| Süreç sahibi / iş analisti (şirket veya grup "süreç ofisi") | şirket başına 1–3 | Kademe/eşik/SLA değiştirme, yeni onay akışı | Form/tuval, simülasyon, yayın talebi |
| Kiracı yöneticisi (yayın onaylayıcı) | 1–2 | Kontrollü yayın, geri alma | Fark/etki görünümü, dört göz |
| BT ekibi | grup düzeyi | Şablon dağıtımı, izleme, hata ayıklama | Yürütme izleme, kullanıcı-dışı Conductor UI |
| Denetim | okuma | "Bu onay hangi kurala göre, kim yayınladı" | Sürüm geçmişi, salt okunur erişim, dışa aktarma |
| Uç kullanıcı (satış, operasyon) | yüzlerce | Onay/görev almak, durum görmek | **Tasarımcıyı görmez**; İş Kuyruğu/Onaylarım (M9A, M4) |

**Dürüst değerlendirme.** Gerçek talebin büyük kısmı (tahmin) "eşikli, SLA'lı, 1–4 kademeli onay zinciri + bildirim + atama"dır; BPMN'in genel ifade gücü çoğu kiracıya gerekmez. Bu yüzden değer, notasyondan çok **sürümlenebilir/test edilebilir/denetlenebilir tanım modeli**ndedir; tuval iletişim ve belgeleme kazancıdır. `senseik` reposunda BPMN/tuval yoktur; `Workflow` modülü **form tabanlı** onay politikası (sıralı adımlar, koşul, paralel, SLA) uygulamıştır ve Kolay İK analizinde ("Yeni süreç ekle" = ad + sıralı onaycı listesi) kullanıcıların bunu yeterli bulduğu görülür (`senseik/docs/analysis/kolayik-video/11.3.04-onay-surecleri.md`). Bu, D/E seçeneklerinin ilk aşama gerekçesidir.

**Mimari şema kapsamı (blok 6) → teslim eşlemesi**

| Şemadaki öğe | Nerede karşılanır |
|---|---|
| BPMN tasarımcısı | Faz 3 (bpmn-js, kısıtlı profil); Faz 2'de form kurucu |
| Süreç kataloğu | Faz 2 (liste, durum, sürümler) |
| Sektör şablonları | Faz 4 + C-X7 (paket modeli); TPD şablon olarak dağıtılır |
| Versiyon yönetimi | Faz 1 (değişmez sürümler, içerik özeti) |
| Etki analizi (AI destekli) | Faz 1: **belirleyici** (statik) analiz; AI kısmı C-X2'ye bağlı, ertelenir |
| Simülasyon & test | Faz 1 (yorumlayıcı + lint + gölge), Faz 2 arayüz |
| Onay/yayın süreci | Faz 1 (durum makinesi, dört göz) |
| Değişiklik yönetimi | Faz 1 (değişiklik gerekçesi + sürümler arası fark) |
| Rollback & migration | Rollback Faz 1; çalışan örnek **migrasyonu** Faz 5 (isteğe bağlı) |
| SLA & izleme | Faz 1 (adım SLA'sı), Faz 4 (ölçüm panosu) |
| Audit & compliance | Kesen konu (`IAuditLogged`, salt okunur denetçi izni, dışa aktarma) |

## 2. Seçenekler

### 2.1 Tanımlar

- **A — bpmn-js modeler → BPMN'den Conductor'a dönüştürücü.** Kanonik kaynak BPMN XML.
- **B — React Flow tabanlı özel tasarımcı, doğrudan Conductor JSON üretir.** Kanonik kaynak Conductor JSON.
- **C — BPMN-yerel motor (Camunda 8 / Flowable / Operaton)** Conductor'un yerine ya da yanında.
- **D — Stüdyo yok:** kodda tanımlı workflow'lar + M9G form tabanlı kural kurucu (+ senseik tarzı onay-zinciri formu).
- **E — Aşamalı melez:** D'nin yönetişim çekirdeği (TPD, sürüm, yayın, simülasyon) + form kurucu → kısıtlı BPMN tuvali (A'nın arayüzü, B'nin sunucu modeli değil).

### 2.2 Puanlama (1 = kötü, 5 = iyi; ağırlıklar toplam 100; puanlar yargıdır, ölçüm değildir)

| Ölçüt (ağırlık) | A | B | C | D | E |
|---|---|---|---|---|---|
| Conductor/mevcut yığınla uyum (15) | 3 | 4 | 2 | 5 | 5 |
| Güvenlik / saldırı yüzeyi (20) | 3 | 3 | 3 | 5 | 4 |
| Değer: yönetişim paneli + kullanıcı ihtiyacı kapsamı (20) | 4 | 3 | 5 | 2 | 4 |
| İlk değere kadar süre / efor (15) | 2 | 3 | 1 | 5 | 4 |
| Lisans / maliyet / tedarikçi riski (10) | 4 | 5 | 2 | 5 | 4 |
| İşletim (KVKK, yurt içi, HA, yük) (10) | 5 | 5 | 1 | 5 | 5 |
| Geleceğe dayanıklılık (SaaS, sektör paketleri) (10) | 4 | 3 | 3 | 2 | 5 |
| **Ağırlıklı toplam (100 üzerinden)** | **69** | **71** | **53** | **82** | **87** |

Duyarlılık: E–D farkı küçüktür (5 puan); Değer ağırlığı 10'a inse bile E, D'nin 1 puan üstünde kalır. Yani E, "D + tuval seçeneğine sahip olmak" demektir; Faz 1–2 fiilen D'nin üst kümesidir ve tuval kararı spike sonucuna ertelenir. A ve B, TPD çekirdeği altında **aynı sunucu**ya oturur; fark yalnız tuval seçimidir (§2.4).

### 2.3 Seçenek notları

| | A | B | C | D | E |
|---|---|---|---|---|---|
| **Lisans / maliyet** | bpmn-js: MIT benzeri; bpmn.io **filigranı görünür kalmak zorunda**, kaldırılamaz/örtülemez, ticari kullanım serbest [E1]. Ürün içi kullanımda kabul edilebilir; SaaS markalamada PO kararı (§7 K2). Kaldırma için ücretli yol lisans sayfasında yok (doğrulanamadı: Camunda ayrı muafiyet satıyor mu) | React Flow MIT; atıf filigranı belgelere göre kaldırılabilir, Pro yalnız örnekler/destek [E5] | Camunda 8 ≥ 8.6: **üretimde Enterprise lisansı zorunlu**, ücretsiz sürüm yalnız geliştirme/test [E3]. Camunda 7 CE ömrünü Ekim 2025'te doldurdu; Apache 2.0 çatal **Operaton** ve **Flowable** (Apache 2.0, açık çekirdek) alternatif [E4][E6] | Ek yok | A'nın filigran şartı Faz 3'te; Faz 1–2 lisans-nötr |
| **Conductor modeliyle uyum** | Anlamsal boşluk büyük (bkz. §2.5); yalnız yapılandırılmış alt küme derlenir | 1:1, ama ham Conductor türlerini kullanıcıya açar | K9'u tersine çevirir; M4 (H1, onay) yeniden yazılır; ya iki motor | Tam uyum | TPD → Conductor derleyicisi (yalnız yapı); boşluk derleyicide kapalı |
| **Güvenlik** | XML girişi (XXE, boyut) + serbest genişletme öğeleri; sunucu ayrıştırırsa yüzey büyür (E'de sunucu ayrıştırmaz) | Kullanıcı tanımlı Conductor JSON = en büyük yüzey (HTTP/INLINE); beyaz liste şart | Script/ifade görevleri, ayrı kimlik/çok kiracılık modeli, ES/Zeebe yığını | Yüzey yok (kodla gelir) | TPD kapalı katalog; bkz. §3 |
| **İşletim** | Ek servis yok | Ek servis yok | JVM + Zeebe/ES (Camunda 8) ya da yeni JVM uygulaması; HA/yedek/KVKK imhası yeniden kurulur | Değişmez | Değişmez |

### 2.4 Tuval seçimi (A vs B) — Faz 3 kararı

| | bpmn-js (BPMN kısıtlı profil) | React Flow (özel düğüm seti) |
|---|---|---|
| Standart / belgeleme | ISO 19510 gösterimi, analistler bilir, harici araçlardan içe aktarma | Kendi notasyonumuz; denetçi/analist aşinalığı yok |
| Doğrulama | bpmnlint ile profil zorlama; moddle uzantısı ile `crm:*` özellikleri | Tümü elle |
| UI uyumu | Mantine 9 dışı SVG tuvali, kendi özellik paneli (tr çeviri, erişilebilirlik, CSP `style-src` **spike**) | React-yerel, Mantine ile tutarlı |
| Marka | Filigran zorunlu | Yok |
| Efor | Orta (adaptör + lint + panel) | Orta (düğümler + bağlantı kuralları + yerleşim) |

Öneri: **bpmn-js**, spike S2 (CSP/i18n/a11y/paket boyutu) olumluysa; olumsuzsa React Flow, sunucu değişmez. Kanonik model TPD olduğundan karar geri döndürülebilirdir.

### 2.5 BPMN ↔ Conductor anlamsal boşluğu

Conductor tanımı **blok-yapılı** bir görev dizisidir (`SWITCH` iç içe dallar, `FORK_JOIN`+`JOIN`, `DO_WHILE`, `SUB_WORKFLOW`, `WAIT`, `HUMAN`, `TERMINATE`); BPMN ise keyfi **çizge**dir (rastgele geri dönen akışlar, yapısız birleşim). Dönüştürme yalnız iyi-iç-içe (well-nested) alt küme için tanımlıdır; derleyici bunu doğrular, aksi hâlde yayın reddedilir.

| BPMN yapısı | Conductor karşılığı | Durum (v1 profili) |
|---|---|---|
| Başlangıç (none) | workflow başlangıcı; tetik CRM'de (elle / M9G kural eylemi `startProcess`) | Var |
| Başlangıç: zamanlayıcı/mesaj | Conductor'da değil; M9G zaman tetiği/olay kuralı | CRM'de karşılanır |
| Bitiş / sonlandırıcı bitiş | bitiş / `TERMINATE` | Var |
| Kullanıcı görevi | `HUMAN` (+ CRM `approvals`/görev kaydı, M4 deseni) | Var; kademe/paralel/çoğunluk CRM'de |
| Servis görevi | `SIMPLE` — yalnız katalogdaki `crm_*` görevleri | Var (kapalı katalog) |
| Betik görevi | `INLINE` (GraalJS) | **Yasak** |
| İş kuralı görevi (DMN) | yok | **Yok**; koşul = M9B `FilterDefinition`, CRM'de değerlendirilir |
| Özel (exclusive) ağ geçidi | `SWITCH` (yalnız `value-param`; koşulu CRM görevi hesaplar) | Var |
| Paralel ağ geçidi | `FORK_JOIN` + `JOIN` (iç içe) | Var (yapılandırılmış) |
| Kapsayıcı (inclusive) / olay-tabanlı ağ geçidi | doğrudan yok | **Yok** |
| Ara zamanlayıcı yakalama | `WAIT` (süre) | Var; süre CRM'de doğrulanmış tam sayı, girdi olarak geçer |
| Sınır zamanlayıcı (SLA yükseltmesi) | doğrudan yok → **kalıp:** `FORK_JOIN`[`HUMAN` ‖ `WAIT`→yükselt]; karar gelince CRM `WAIT`'i, süre dolunca `HUMAN`'ı tamamlar (M4'te `CompleteWaitTaskAsync` deseni) | Var (TPD'de `sla` özelliği); `JOIN` koşullu erken çıkışı **spike S1** |
| Mesaj/sinyal yakalama | `WAIT`/`HUMAN` + CRM ilişkilendirme | Sınırlı (kayıt olayı değil, insan/sistem tamamlaması) |
| Çağrı etkinliği / alt süreç | `SUB_WORKFLOW` (yalnız aynı kiracının yayınlanmış süreci, derinlik ≤ 2, sürüm sabit) | Faz 5 |
| Döngü (yapılandırılmış) | `DO_WHILE`, sabit üst sınır | Var, üst sınırlı |
| Çok örnekli (paralel) | `FORK_JOIN_DYNAMIC` | **Yasak** (kaynak tüketimi, dinamik yüzey) |
| Yapısız geri akış, olay alt süreci, telafi/işlem | yok | **Yasak** |
| Havuz/şerit | yalnız görsel; şerit → atanan rol (CRM uzantısı) | Görsel/uzantı |
| Hata sınır olayı | görev yeniden deneme + `failureWorkflow` | Platform sabiti; kullanıcı ayarlamaz |

Sonuç: "tam BPMN 2.0" vaat edilmez; **CRM-BPMN v1 profili** yayınlanır ve içe aktarımda kapsam dışı öğeler ad ad raporlanır. Orkes Conductor'un BPMN içe aktarıcısı vardır ama bu **ticari Orkes** özelliğidir; OSS'te bulunup bulunmadığı doğrulanamadı → dayanılmaz [E8].

## 3. Güvenlik (kilit bölüm): kiracı-yazımlı süreçler = kullanıcı yazımlı kod yolu

### 3.1 Temel gerçek
OSS Conductor'da RBAC, kimlik doğrulama ve çok kiracılılık yoktur (çok kiracılık/RBAC Orkes'e özgü; OSS her zaman `default`) [E7]. Tek paylaşımlı Conductor'da tüm kiracıların tanım ve yürütmeleri bir arada durur; Conductor HTTP görevi için hedef sınırlaması dokümante edilmemiştir [E9]; INLINE görevi GraalJS ile betik çalıştırır, sandbox/limit bilgisi dokümanda yoktur [E10]. Conductor arayüzü (ui-next) ham tanım tasarımcısıdır ve tüm kiracı verisini gösterir [E11]. Bu yüzden **güvenlik sınırı Conductor'da değil, bizim derleyicimiz ve görev çalıştırıcımızdadır**.

### 3.2 Tehdit → kontrol

| # | Tehdit | Kontrol |
|---|---|---|
| S1 | Kiracı HTTP/INLINE/JQ/LAMBDA/KAFKA/EVENT/LLM/MCP vb. görev türü seçip SSRF, betik, DoS yapar | Kapalı katalog derleyici + **ikinci kapı**: kayıt sırasında ve periyodik tarayıcıda Conductor'daki tanımlar beyaz listeye karşı yeniden doğrulanır (`compiled_hash` ile sapma/kurcalama tespiti). İzinli türler: `SIMPLE` (yalnız `crm_*`), `SWITCH` (`value-param`), `FORK_JOIN`/`JOIN`, `WAIT`, `HUMAN`, `TERMINATE`, sınırlı `DO_WHILE`; alt süreç Faz 5 |
| S2 | Kullanıcı metninin Conductor `${...}` ifadesi olarak yorumlanması / parametre enjeksiyonu | JSON'a **kullanıcı metni girmez**; düğüm kimlikleri üretilmiş (`n1…`); tüm parametre (rol, eşik, şablon, süre) `process_versions` anlık görüntüsünde, görev `TrustedTask` ile okur (H1 genellemesi). Yalnız doğrulanmış tam sayı süreler girdi olarak geçer |
| S3 | Tanım ad çakışması/ezme (`crm_deal_approval` üzerine yazma), kiracılar arası tanım sızıntısı | Tanım adı **yapı özetinden** üretilir (`crm_p_<sha256(yapı)[0..16]>`, ayrılmış önek); iki kiracının aynı şekilli süreci **aynı tanımı paylaşır**, tanımda kiracı bilgisi yoktur. Kiracı ayrımı yalnız `(tenantId, instanceId, engineWorkflowId)` doğrulamasıyla (H1) |
| S4 | Görev girdisine güvensizlik / çapraz kiracı yürütme | Mevcut `WorkflowTaskRunner` doğrulaması genelleştirilir: `process_instances` satırı + kiracı süzgeci + `running` + motor kimliği + görev türü/düğüm uyumu; bilinmeyen düğüm → terminal `untrusted_task` |
| S5 | Yükseltme: sistem bağlamıyla çalışan görev, `[RequiresPermission]` atlar | M9G D9/D10 aynen: adımlar **dar Contracts portlarıyla** çalışır, `Processes.*` derlemeleri `IDispatcher`'a bağlanamaz (mimari test); yazar, her adımın gerektirdiği izne kaydederken **sahip olmalıdır** (`role.permission_escalation`); yürütücü kimlik `ProcessActor` ("Süreç: <ad>", `UserId=null`); insan kararı gerçek kullanıcıyla (`crm.approvals.decide`, atanan doğrulaması) |
| S6 | Dış etki (e-posta/webhook) ile veri sızdırma, SSRF | **Yeni çıkış yolu yok:** bildirim = M8A olayı, dış çağrı = M8B'deki *mevcut* abonelik (PII'siz zarf, yalnız Worker, SSRF korumalı); serbest URL/e-posta adımı katalogda yok (M9G D8 ile aynı) |
| S7 | Kaynak tüketimi: sonsuz döngü, çok düğüm, dev yük, kuyruk taşırma, gürültülü komşu | Teknik tavanlar sabit: ≤ 40 düğüm/süreç, `DO_WHILE` ≤ 10 yineleme, `WAIT` ≤ 90 gün, örnek azami yaşı 90 gün, girdi/çıktı ≤ 8 KB, yeniden deneme/zaman aşımı **platform sabiti** (kullanıcı `retryCount`/`timeoutSeconds` veremez). Başlatma tarafı (CRM kontrol eder): kiracı başına eşzamanlı çalışan örnek ve dakikada başlatma kotası (`maxRunningProcessInstances`, hız kovası); Conductor görev tanımı sınırları kiracı bazlı değildir, bu yüzden sınırlama **başlatmada** yapılır |
| S8 | XML saldırıları (XXE, milyar gülüş) ve stored-XSS (etiket/dokümantasyon) | **Sunucu BPMN XML'i ayrıştırmaz**; TPD JSON'u alır (boyut ≤ 256 KB, şema doğrulama, metinler düz metin). XML yalnız istemcide TPD'den üretilir; içe aktarılan `.bpmn` istemcide TPD taslağına çevrilip sunucu doğrulamasından geçer. Gösterimde tüm metin kaçışlanır (spike S2: bpmn-js `documentation` HTML davranışı) |
| S9 | Tuval ↔ gerçek sürecin ayrışması (denetimde yanıltıcı diyagram) | Diyagram TPD'den **deterministik** üretilir; ayrı saklanan XML yok (yerleşim TPD'de `layout`). Ayrışma yapısal olarak imkânsız |
| S10 | Yayın sonrası tanım/parametre kurcalama | Sürümler **değişmez**, `content_hash` ve `compiled_hash` denetim satırına yazılır; yayın dört göz (§4); DB'de doğrudan değişiklik için `xmin` + hash doğrulaması her başlatmada |
| S11 | Conductor'a doğrudan erişim (ağdaki ele geçirilmiş konteyner, uygulama SSRF'i) tanım/örnek değiştirir | **Ön koşul (DevOps):** Conductor önüne kimlik doğrulayan vekil (paylaşılan gizli başlık/mTLS, Docker secret), yalnız `api`+`worker` kaynaklı, yönetim uçları yalnız Api; UI yayınlanmaz (M5 durumu korunur). `hardening-report`'un açık işi "Conductor kimlik doğrulaması" burada zorunlu hâle gelir |
| S12 | Askıdaki/plan dışı kiracı | Mevcut kapı: Worker `tenant_suspended`/`module_disabled` → terminal başarısız; uzun `WAIT`'li örneklerin askı süresince başarısız olması davranışı **spike S1** (öneri: askıda görev *ertelenir*, yalnız uzun askıda başarısız) |
| S13 | Yapısal olarak sınırsız zincir: süreç → M9G kuralı → süreç | `startProcess` eylemi M9G D11 `AutomationContext` (derinlik ≤ 3, zincir) ile sınırlanır; süreç kendi ürettiği olayla kendini başlatamaz |

**Sandbox gerekir mi?** Hayır: kullanıcı kodu çalıştırılmadığı sürece (S1–S2) sandbox aranmaz; koşullar M9B DSL'i ile CRM'de değerlendirilir, Conductor'da ifade motoru kullanılmaz. Bu ilke bozulursa (ör. "betik adımı" talebi) ayrı güvenlik incelemesi ve ayrı süreç (izole worker) gerekir → bu kartın dışında.

### 3.3 Kiracı izolasyonu ve KVKK
- Yeni tablolar (şema `processes`): `process_definitions`, `process_versions`, `process_instances`, `process_step_runs`, hepsi `TenantAggregateRoot`/`TenantEntity` (K2 süzgeci, `(tenant_id, …)` indeks); yeni her varlık için çapraz-kiracı testi (kart kapısı). Aynı adlı tanım paylaşımı (S3) kiracı verisi taşımadığı için izolasyonu bozmaz; bu, `TenantFilterBypassInventoryTests`'e yeni `IgnoreQueryFilters` eklemez (Conductor tanım tablosu CRM'de değil).
- **Conductor'a yalnız kimlik + kod:** M4'te `leadName` gibi ad alanları girdiye yazılıyor (`WorkflowInputs.Build`); süreç stüdyosunda **hiçbir kişisel veri** Conductor girdi/çıktısına yazılmaz (yalnız kayıt kimlikleri, sabit kodlar). Görev sonuçları kod + kimlik taşır (M9G ile aynı).
- İmha: `TenantDataEraser` otomatik (yeni şema); Conductor tarafı mevcut `workflows-conductor` silicisi (`IWorkflowEngine.RemoveAsync`) süreç örneklerini de kapsayacak şekilde genişler; **kişi bazlı silme** (bir lead'in KVKK silmesi) kayıt kimliğine bağlı örnek özetini de silmeli (Faz 1 testi).
- Saklama: örnek ayrıntısı 90 gün, özet satırı denetim süresiyle (`Audit:RetentionDays` 1825) hizalı — PO kararı (§7 K7). Conductor'da biten yürütmelerin otomatik temizliği OSS'te doğrulanamadı → Worker temizlik işi (`RemoveAsync`) varsayılır (spike S1).
- KVKK m.11/1-g (otomatik analizle aleyhe sonuç): katalogda "kişi hakkında otomatik karar/puanlama" adımı yoktur; reddetme/onay her zaman insan adımıdır.

### 3.4 Plan, bayrak, izin
- Kapı modülü `processes` (`GatedModules.Processes`); sınırlar `maxProcessDefinitions` (yayınlı), `maxRunningProcessInstances`, `maxProcessRunsPerDay` (M7/M9G deseni, `[ConsumesLimit]`); `internal` plan limitsiz. Sonraki kuruluşlar için öneri varsayılanlar §7 K10.
- İzinler (yeni, yalnız ekleme): `org.processes.design` (taslak), `org.processes.publish` (yayın onayı), `org.processes.manage` (örnek sonlandır/yeniden dene), `org.processes.audit` (salt okunur). API anahtarı `org.*` taşıyamaz (M8B D10); Standart rol almaz. Mevcut `org.workflows.manage`/`crm.approvals.decide` korunur.

## 4. Sürüm, yayın, onay, geri alma, migrasyon

**Model.** `process_definitions` (anahtar, ad) 1—N `process_versions` (`version`, `state` = `draft|in_review|approved|published|retired`, `content` TPD jsonb, `content_hash`, `compiled_hash`, `structure_hash`, `change_note`, yazar, gözden geçiren, zaman damgaları; yayınlanan sürüm **değişmez**). Bir tanımda tek açık taslak; taslak düzenlemesi `xmin` ile iyimser eşzamanlı.

**Akış.** Taslak → **statik doğrulama (lint)** + **simülasyon** zorunlu geçer → gözden geçirmeye gönder → **dört göz** (yazar ≠ onaylayıcı; `org.processes.publish`) → yayın: derle, `structure_hash` adlı Conductor tanımı yoksa idempotent kaydet (`PUT`), tanımın "güncel sürümü"nü değiştir. **Başlatma her zaman açık sürümle** yapılır (Conductor'un "en yüksek sürüm" varsayılanına güvenilmez; belge: sürüm verilmezse en yüksek sürüm kullanılır, çalışan örnekler kendi sürümünde biter [E12]). Tek yönetici kiracıda kendi kendine onay yapılandırma bayrağıyla ve denetim işaretiyle mümkündür (§7 K5).

**Geri alma.** "Güncel sürüm" işaretçisini önceki yayınlanmış sürüme çevirmek; yeni başlatmalar eski sürümle gider, çalışanlar etkilenmez. Sürüm silinmez (`retired`).

**Çalışan örnek migrasyonu.** Conductor'da bilinen yerinde migrasyon yok; yalnız "çalışanlar kendi sürümünde biter" davranışı doğrulandı [E12]. Öneri v1: **boşalt (drain)** + `WAIT ≤ 90 gün` + örnek azami yaşı; zorunlu geçiş yönetici eliyle sonlandır/yeniden başlat. Otomatik migrasyon (düğüm kimliği eşlemesi + uyumluluk denetimi) Faz 5, önce spike (yeniden başlat `useLatestDefinitions` gibi seçenekler **doğrulanamadı**).

**Etki analizi (belirleyici).** Yayın öncesi rapor: değişen düğümler (sürümler arası fark), etkilenen roller/kullanıcılar (rol var mı, üyesiz rol), bağlı M9G kuralları (`startProcess` verenler), çalışan örnek sayısı (eski sürümde), süre/SLA farkı, sonlanmamış bekleyen onaylar. "AI destekli" özet C-X2 (yerinde model, KVKK) sonrası isteğe bağlı üst katmandır; çekirdek buna bağlı değildir.

**Simülasyon/test modu (D13 deseni).**
1. *Lint:* yapı, erişilebilirlik, katalog, sınırlar, rol/abonelik varlığı, iç içe yapı.
2. *Simüle (yan etkisiz):* TPD üzerinde süreç-içi **yorumlayıcı**; örnek kayıt anlık görüntüsü/varsayımsal alanlarla koşulları M9B `IFilterMatcher` ile değerlendirir, insan adımlarını senaryo seçimiyle (onay/red/zaman aşımı) ilerletir, "olurdu" eylem planı ve yol çıkarır (M9G `/test` ile aynı biçim). Yorumlayıcı ↔ Conductor uyumu için **altın senaryo testleri**: aynı senaryo mevcut `FakeWorkflowEngine` (gerçek tanım + gerçek görevler) ile de koşar, çıktılar eşit olmalı.
3. *Gölge:* M9G `shadow` gibi, gerçek olaylarda örnek `dry_run`, eylemler `planned`.
4. *Canlı deneme:* yalnız test kiracısında/`isTest` bayraklı örnek (yan etkiler no-op).

**SLA ve izleme.** Adım SLA'sı (TPD `sla`: süre + eylem = bildir/yükselt/yeniden ata) birinci sınıf; örnek düzeyi azami süre; izleme sayfası: çalışan örnekler, sürüm, takılan adım (görev yaşı), hata, düğüm başına çevrim süresi (`process_step_runs`). **Ölçek riski:** M4 `ExecutionStatusSyncService` çalışan her yürütmeyi 5 sn'de bir yoklar; uzun ömürlü/binlerce `WAIT`'li örnekte O(N) — süreç için olay güdümlü ilerleme (Conductor `workflowStatusListener` [alan tanımda mevcut, OSS uygulamaları doğrulanamadı]) ya da seyrek süpürme + isteğe bağlı okuma (spike S1). K20 metrikleri: süreç/kiracı kimliği **etiket olamaz**; yalnız düşük kardinaliteli `status|outcome|task_type`; kiracı bazlı SLA raporu DB'den.

**Denetim.** Tanım/sürüm durum değişiklikleri `IAuditLogged` + değişiklik gerekçesi + hash'ler; süreç örneğinin yaptığı kayıt değişiklikleri M9G ile aynı atıf (`"Süreç: <ad>"` + `CorrelationId = process:<instanceId>`); `org.processes.audit` salt okunur; sürüm geçmişi dışa aktarılabilir.

## 5. Önerilen seçenek ve faz planı

### 5.1 Mimari (E)
1. **TPD** (kapalı katalog, sürümlü şema): düğümler `start`, `end`, `approval` (kademeli/paralel, rol veya üye, eşik: ilk-onay/hepsi/çoğunluk, SLA), `task` (M4 `IActivityCreator`), `action` (M9G katalogundan: `updateField`, `assignOwner`, `createNote`, `sendNotification`, `callWebhook`), `decision` (M9B DSL koşulları), `parallel`, `wait`. Tetik: **elle** ("Süreci başlat" düğmesi) ve **M9G `startProcess` eylemi**; ayrı tetik arayüzü yapılmaz (M9G `Kural` bunu zaten çözer; M9G'nin "v1.1 startApproval" notunun gerçekleşmesi).
2. **Derleyici** TPD → Conductor (yalnız yapı, `structure_hash` adı) + **genel görev çalıştırıcıları** (`crm_process_*`, H1 doğrulaması).
3. **Sınır kuralı** (M9G ile birlikte yayınlanır): bekleme/insan/zamanlayıcı > dakikalar → süreç (Conductor); kısa, senkron, ≤ 5 adım → M9G kuralı. M4 `dealApproval` **sistem şablonu** olarak TPD'ye taşınır (Faz 4); `leadAssignment` zaten M9G'ye taşınıyor.
4. **Derleme hedefi alternatifi** (yalnız spike başarısız olursa): tek genel Conductor "yorumlayıcı" workflow'u + CRM'de düğüm yorumlayıcısı; Conductor yalnız dayanıklı sayaç/kuyruk olur, simülasyonla aynı kod koşar ve migrasyon kolaylaşır; bedeli Conductor görünürlüğünün ve fork/join belirtecinin bize geçmesidir (kısmi kendi motorumuz). Varsayılan tercih yerel derleme (K9 ve M4 kalıbı).

### 5.2 Fazlar ve efor (mühendis-hafta, ew; testler dâhil; ±%40; ekip yeteneği varsayımı: M9G kartı kadar sıkı kapı)

| Faz | Kart(lar) | İçerik | Backend | Web | Bağımlılık |
|---|---|---|---|---|---|
| 0 | `C-X1S` spike | S1 Conductor semantiği/ölçek; S2 bpmn-js; S3 TPD→Conductor→FakeEngine iskeleti | 1,5 | 0,5 | — (şimdi, M9G'den bağımsız) |
| 1a | `C-X1A` Süreç çekirdeği | TPD şeması+lint+derleyici, tablolar, örnek/adım modeli, genel görevler, başlatma (elle + `startProcess`), plan/izin/denetim/imha, H1 genellemesi, mimari testler | 7 (+1 Spec) | — | **M9G** (ports, `IFilterMatcher`, `ActionTemplateEngine`, `startProcess` kancası), M8A, M8B, M7, M9H (kayıt görünürlüğü kancası) |
| 1b | `C-X1B` Yönetişim | sürümler, yayın durum makinesi, dört göz, etki analizi, yorumlayıcı-simülasyon, gölge, geri alma, sürüm farkı | 4 | — | X1A |
| 2 | `C-X1C` Katalog + form kurucu (Web) | kataloğu, adım kurucu (senseik "Yeni süreç ekle" tarzı), sürüm/yayın arayüzü, simülasyon paneli, örnek izleme, Onaylarım çok kademeye uyum | 0,5 | 5 | X1A, X1B, M9A (İş Kuyruğu) |
| 3 | `C-X1D` Görsel tasarımcı (Web) | bpmn-js + bpmnlint profili + TPD⇄BPMN adaptörü, yürütme yer paylaşımı (düğüm renkleri), içe aktarma raporu | 0,5 | 5 | X1C, S2 olumlu |
| 4 | `C-X1E` SLA/ölçüm | adım SLA yükseltme eylemleri (gerekirse 1a'da), çevrim süresi/takılma panoları (M9F ile) | 1,5 | 0,5 | X1C, M9F |
| 4 | `C-X1F` Şablonlar/M4 taşıma | `dealApproval` → sistem şablonu, kopyala-düzenle, sektör şablonu iskeleti | 2 | 1 | X1B, C-X7 |
| 5 | `C-X1G` (isteğe bağlı) | çalışan örnek migrasyonu, alt süreç/çağrı etkinliği, AI etki özeti | 3 | 1 | X1F, C-X2 |
| — | Güvenlik incelemesi + DevOps | S11 Conductor vekili + runbook + tarayıcı işi + sızma denemesi kontrol listesi | 1 (DevOps) + 1 (Security) | — | X1A öncesi (vekil) |

Toplam ≈ Faz 0: 2; MVP (1a+1b+2 + DevOps/Security): **~19**; tuval (3): +5,5; Faz 4: +5; Faz 5: +4 → **~33 ew (Faz 5 dâhil ~35)**. Kartlar mevcut panonun **Spec→Backend+Web** kuralını izler: her kart önce `docs/plan/x1*-….md` plan+kontrat belgesi. Sıcak dosyalar (yalnız ekleme): `ModuleCatalog.cs`, Migrator, Worker, `Permissions.cs`, `TestFixture.cs` (Respawn `processes`), SharedResource, web navigasyon/locale.

### 5.3 Neden önce form, sonra tuval?
Tuval tanımı stabil bir TPD'ye ihtiyaç duyar; form kurucu aynı TPD'yi en ucuz ve en erişilebilir (Mantine, i18n) yolla üretir ve gerçek talebin çoğunu (onay zinciri) karşılar. Tuval, "değer kanıtlandı" sinyalinden sonra iletişim/belgeleme ve şablon paylaşımı kazancıyla gelir. Talep kanıtı yoksa Faz 3 hiç yapılmayabilir; Faz 1–2 yine değer üretir.

## 6. Riskler ve spike gerektiren bilinmeyenler

| # | Risk / bilinmeyen | Etki | Azaltma / Spike |
|---|---|---|---|
| R1 | Conductor OSS'te kimlik doğrulama yok; tek paylaşımlı örnek | Kritik | S11 vekil + ağ yalıtımı ön koşul; kiracı-yazımlı süreç bayrağı DevOps bitmeden açılmaz |
| R2 | Beyaz liste kaçağı (yeni Conductor görev türleri; ör. LLM/MCP türleri yeni eklendi) | Yüksek | Derleyici *izin-listesi* (yasak-listesi değil); sürüm yükseltmede türlerin gözden geçirilmesi; periyodik tanım tarayıcısı |
| R3 | Anlamsal boşluk: sınır zamanlayıcı yarışı, `JOIN` erken çıkış, `WAIT` süre biçimi, sürüm belirtme | Yüksek | **S1:** gerçek Conductor 3.32.4'e karşı yarış kalıbı, `WAIT` biçimleri, aynı yapı adlı tanım paylaşımı ve sürüm sabitleme, "askıda erteleme" |
| R4 | Ölçek: uzun ömürlü örnekler, durum yoklaması O(N), Postgres tabanlı kuyruk, tanım sayısı | Yüksek | S1: 5 000 bekleyen örnek, yoklama maliyeti, `workflowStatusListener` seçenekleri; yapı özeti paylaşımıyla tanım sayısı sınırlanır; gerekirse §5.1(4) alternatifi |
| R5 | bpmn-js: CSP (`style-src`), Mantine tema/karanlık kip, tr çeviri, klavye erişilebilirliği, paket boyutu, **filigran**, XSS (dokümantasyon) | Orta | **S2**; olumsuzsa React Flow; PO filigran kararı (K2) |
| R6 | Yorumlayıcı ↔ Conductor davranış ayrışması (simülasyon yalan söyler) | Orta | Altın senaryo testleri (§4), aynı kalıpların FakeEngine'de koşması, "simülasyon = tahmin" uyarısı |
| R7 | Kapsam kayması: "tam BPMN", betik adımı, HTTP adımı, DMN talepleri | Orta | Profil yayınlanır; talep = ayrı güvenlik incelemesi kartı; C seçeneği yalnız DMN/olay alt süreci/telafi ihtiyacı belgelenirse yeniden değerlendirilir |
| R8 | Talep kanıtı zayıf (yalnız varsayım) | Orta | K1 görüşmesi: 3 şirketten süreç envanteri (sayı, kademe, eşik, SLA); Faz 3 kapısı |
| R9 | KVKK: PII'nin Conductor'a sızması (M4 mirası `leadName`) | Orta | Yalnız kimlik/kod ilkesi + test; M4 taşıması sırasında ad alanları girdiden çıkarılır |
| R10 | Onay pratiği: izinli/ayrılmış onaycı, vekâlet; takılan onaylar | Orta | SLA yükseltmesi Faz 1; vekâlet/izin takvimi M9J sonrası (K8) |
| R11 | Lisans: bpmn.io filigranı; Camunda 8 üretim lisansı; Conductor OSS/Orkes yönü (Apache 2.0 korunur; topluluk yönetişimi değişebilir) | Düşük–Orta | Kanonik model TPD → tuval ve motor değiştirilebilir; Conductor'a bağlılık derleyicide izole |
| R12 | Ekip kapasitesi ve mevcut backlog (M9G–M9J) | Orta | Spike düşük maliyetli ve bağımsız; yapım M9G sonrası |

**Spike listesi.** S1 (≈1,5 ew) Conductor semantiği/ölçek; S2 (≈0,5 ew) bpmn-js; S3 (≈0,5 ew) TPD→Conductor→FakeEngine. Çıkış ölçütü: S1'de yarış kalıbı ve paylaşımlı tanım çalışır, 5 000 bekleyen örnekte durum yükü kabul edilebilir; S2'de CSP+tr+klavye tamam ve filigran düzeni kabul edilebilir.

## 7. Ürün sahibinin vereceği kararlar (öneri cevabıyla)

| # | Soru | Öneri |
|---|---|---|
| K1 | Talep gerçek mi? Kiracı yöneticisi kendi süreç yapısını (kademe/paralel/SLA) self-servis mi değiştirecek, yoksa yalnız Algosense mi tasarlayıp şablon mu sunacak? | Önce 3 şirketten süreç envanteri (sayı, kademe, eşik, SLA); onaylanırsa self-servis, ancak yalnız `internal` plan kiracılarında bayrakla açılır |
| K2 | Tuval BPMN (bpmn-js, **kaldırılamaz filigran**) mı, nötr tuval (React Flow) mı? | BPMN kısıtlı profil; S2 olumsuzsa React Flow. Filigran kabulü PO'dan (SaaS markalamada) |
| K3 | "Tam BPMN 2.0" yerine CRM-BPMN v1 kısıtlı profili kabul edilir mi (betik, HTTP, DMN, kapsayıcı geçit, olay alt süreci yok; keyfi `.bpmn` içe aktarımı yok)? | Evet |
| K4 | Motor: Conductor (K9) kalsın mı, Camunda 8/Flowable/Operaton düşünülsün mü? | Conductor kalır; Camunda 8 üretimde ücretli lisans; yalnız DMN/telafi/olay alt süreci ihtiyacı belgelenirse yeniden aç |
| K5 | Yayında dört göz zorunlu mu? | Zorunlu (yazar ≠ onaylayıcı); tek yönetici kiracıda `selfApprove` bayrağı + denetim işareti |
| K6 | Çalışan örnek migrasyonu? | v1 yalnız boşalt (drain), `WAIT ≤ 90 gün`, örnek azami yaşı 90 gün; migrasyon Faz 5 |
| K7 | Saklama: örnek ayrıntısı ve özeti ne kadar? | Ayrıntı 90 gün, özet 5 yıl (`Audit:RetentionDays` ile hizalı); Conductor'da PII yok |
| K8 | Vekâlet/izin sırasında onay devri kapsamda mı? | Faz 1'de yalnız SLA yükseltmesi ve elle yeniden atama; takvim tabanlı vekâlet M9J sonrası |
| K9 | Conductor'un kimlik doğrulayan vekil + ağ yalıtımı arkasına alınması bu kartın **ön koşulu** mu? | Evet, X1A başlamadan (DevOps ~1 ew); yoksa özellik bayrağı kapalı kalır |
| K10 | Plan sınırları varsayılanı? | `internal`: sınırsız; diğer planlar: 20 yayınlı süreç, 500 eşzamanlı örnek, 5 000 çalıştırma/gün, teknik tavanlar sabit (§3.2 S7) |
| K11 | AI destekli etki analizi? | C-X2 sonrasına ertele; çekirdek belirleyici analiz |
| K12 | M4 `dealApproval` ve tetik modeli | `dealApproval` sistem şablonuna taşınır (Faz 4); ayrı tetik arayüzü yok, tetik = elle + M9G `startProcess` |
| K13 | Sıra | Spike hemen (M9G'den bağımsız); yapım M9G merge sonrası; Faz 3 (tuval) ancak K1 ve S2 sonucuna göre |

## 8. Dış kaynaklar (erişim tarihi 2026-09-20)

| Kod | Olgu | Kaynak | Durum |
|---|---|---|---|
| E1 | bpmn.io lisansı: MIT tarzı, filigran zorunlu ve görünür, ticari kullanım serbest | https://bpmn.io/license/ | Doğrulandı (sayfa metni) |
| E3 | Camunda 8 ≥ 8.6: derlenmiş yazılım tescilli lisans; ücretsiz yalnız üretim-dışı, üretim için Enterprise; Camunda License 1.0 (Ekim 2024 →) | https://docs.camunda.io/docs/reference/licenses/ , https://camunda.com/blog/2024/10/camunda-licensing-what-you-need-to-know/ | Doğrulandı (arama özeti; sayfalar ayrıca okunmadı) |
| E4 | Camunda 7 CE ömrünü Ekim 2025'te doldurdu (son sürüm 7.24, 14 Ekim 2025); Operaton Apache 2.0 çatalı | https://forum.camunda.io/t/important-update-camunda-7-community-edition-end-of-life-announced/50921 , https://github.com/operaton/operaton | Doğrulandı (arama özeti) |
| E5 | React Flow MIT; atıf filigranı kaldırılabilir, abonelik yalnız Pro örnekleri/destek | https://reactflow.dev/remove-attribution , https://github.com/xyflow/xyflow/discussions/3397 | Doğrulandı (arama özeti) |
| E6 | Flowable Apache 2.0 açık çekirdek (BPMN/DMN/CMMN, Java + REST); ticari platform ek özellikler | https://www.flowable.com/open-source | Doğrulandı |
| E7 | OSS Conductor: sınırlı güvenlik, RBAC yok, çok kiracılılık Orkes'e özgü (`orgId`; OSS her zaman `default`) | https://orkes.io/blog/differences-between-conductor-oss-vs-orkes-conductor , https://github.com/conductor-oss/conductor/issues/763 | Doğrulandı (dolaylı: Orkes karşılaştırması; OSS'te bir kimlik doğrulama bileşeni olduğuna dair kanıt bulunamadı) |
| E8 | Orkes Conductor'da BPMN içe aktarıcı; OSS'te varlığı | https://orkes.io/content/developer-guides/convert-bpmn-to-workflows | Orkes için doğrulandı; **OSS için doğrulanamadı** |
| E9 | Conductor HTTP görevi: `uri/method/headers/body/timeout`; hedef sınırlaması/izin listesi belgelenmemiş | https://conductor-oss.github.io/conductor/documentation/configuration/workflowdef/systemtasks/http-task.html | Doğrulandı (yokluk kanıtı: dokümanda bahsi yok) |
| E10 | INLINE görevi `graaljs` (önerilen) / `javascript` (eski) / `python` / `value-param`; sandbox/limit bilgisi yok | https://conductor-oss.github.io/conductor/documentation/configuration/workflowdef/systemtasks/inline-task.html | Doğrulandı (yokluk kanıtı) |
| E11 | Conductor lisansı Apache 2.0; yerleşik görsel arayüz (ui-next); yeni görev türleri (`LLM_CHAT_COMPLETE`, `CALL_MCP_TOOL`, `HUMAN_APPROVAL` …) | https://github.com/conductor-oss/conductor | Doğrulandı; sürüm numarası sayfadan **doğrulanamadı** (repo `backend.md`: 3.32.4) |
| E12 | Sürüm belirtilmezse en yüksek sürüm kullanılır; çalışan örnekler atandıkları sürümle devam; `restartable`, `failureWorkflow`, `workflowStatusListenerEnabled` alanları | https://conductor-oss.github.io/conductor/documentation/configuration/workflowdef/index.html | Doğrulandı |

Doğrulanamayanlar (spike'a bırakıldı): Conductor OSS'te çalışan örnek migrasyonu/`restart` ile yeni tanım kullanımı; `JOIN` koşullu erken çıkış; `WAIT` süre biçimi; `workflowStatusListener` OSS uygulamaları; biten yürütme otomatik temizliği; binlerce tanımın metadata performansı; 3.32.4'ün "son kararlı" olduğu; Camunda'nın bpmn.io filigran muafiyeti sattığı; Camunda 8 çok kiracılılığının lisans kapsamı.
