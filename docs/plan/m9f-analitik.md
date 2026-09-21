# Milestone 9F — Rapor ve analitik: rapor oluşturucu, hazır raporlar, pano oluşturucu, hedefler, öngörü (plan + HTTP kontratı)

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Analiz: [zoho-ekran-analizi.md](../analysis/zoho-ekran-analizi.md) ("Raporlar ve analitik" bölümü, M9F satırı, §5 ek notlar; ekran görüntüleri 3–6) · Kararlar: [kararlar.md](../architecture/kararlar.md) (K2, K3, K5, K7, K10, K13, K14, K16, K17, K18, K20) · Önceki: [M3](m3-aktivite-rapor.md), [M6A](m6a-ticaret.md), [M6B](m6b-servis.md), [M6C](m6c-pazarlama.md), [M7](m7-saas-hazirlik.md), [M8A](m8a-bildirimler.md), [M8B](m8b-entegrasyonlar.md), [M8C](m8c-dosya-ekleri.md), [M8D](m8d-ozel-alanlar.md), [M9A](m9a-kabuk.md), [M9B](m9b-listeler.md), [M9C](m9c-envanter.md), [M9D](m9d-aktiviteler.md), [M9E](m9e-alanlar.md), güvenlik: [hardening-report.md](../security/hardening-report.md) · Pano kartı: `C-M9F` (dal `m9/analytics`). Biçim kuralları M2–M9 ile aynı: taban yol `/api/v1`, JSON camelCase, enum'lar camelCase string, `null` alanlar yazılmaz, hatalar ProblemDetails + `code` (+ `args`, doğrulamada `errors`), sayfalı liste `{ items, page, pageSize, totalCount }` (`page` 1'den, `pageSize` varsayılan 25 / en çok 100), `sort` = `alan` veya `-alan`, gün alanları `YYYY-MM-DD`, zaman damgaları ISO 8601 UTC, oluşturma 201, güncelleme/eylem/silme 204.

**Çıktı:** Kullanıcı (1) **Rapor kütüphanesinde** klasörlü, aranabilir, favorili bir listeden ~22 **hazır raporu** (satış hattı, öngörü, kazanılan/kaybedilen, satış döngüsü, kayıp nedenleri, temsilci performansı, durgun fırsatlar, potansiyel kaynağı ve dönüşüm, aktivite, geciken aktivite, talep SLA, talep birikimi yaşlandırma, kampanya ROI, teklif/sipariş/fatura durumu ve yaşlandırma) açar, tarih aralığı/huni/sahip ile çalıştırır, CSV indirir; (2) **rapor oluşturucuda** kapalı bir katalogdan varlık (+ N:1 birleştirilen varlıklar), sütun/ölçü, filtre (M9B DSL), gruplama, toplama, sıralama, grafik türü seçerek özel rapor kurar, klasöre kaydeder, özel/rollere/herkese paylaşır, **CSV dışa aktarır ve zamanlar** (serbest SQL yoktur); (3) **pano oluşturucuda** kişisel veya kiracı panoları kurar (KPI, grafik, tablo, huni, ölçer bileşenleri; kalıcı düzen; bileşen başına izin/plan kapısı; yenileme stratejisi), hazır "Satış / Servis / Aktivite" panolarını kopyalar; (4) **hedefler** tanımlar (kullanıcı/rol/kiracı × dönem × gelir, kazanılan fırsat, tamamlanan aktivite) ve ölçerlerle **gerçekleşmeyi** izler, liderlik tablosu görür; (5) **öngörü** sayfasında aşama olasılığı × kapanış tarihi ile **ağırlıklı satış hattı**, taahhüt/en iyi durum aralığı, hız (run-rate) ve doğrusal eğilim görür — **şeffaf istatistik, opak ML yok** — ve geçmiş öngörünün gerçekleşene göre doğruluğunu izler. Tüm sonuçlar çağıranın **kiracı + kayıt kapsamı + alan izni** içinde hesaplanır; çapraz modül SQL yoktur (K5). M3/M6 rapor uçları **davranışça donmuştur**, aynı sayıları veren hazır raporlarla eşlenir (parite testi).

## Kararların özeti

| # | Konu | Karar | Gerekçe |
|---|---|---|---|
| D1 | Modül | Yeni **çekirdek** (kapı olmayan) modül **`Sense.Crm.Modules.Analytics`** (şema `analytics`, `AnalyticsDbContext`, migration `InitialAnalytics`). Sahiplendikleri: kayıtlı rapor tanımları, klasörler, panolar, hedefler, zamanlamalar, çıktı dosyaları, anlık görüntüler, favoriler, günlük bütçe sayacı. **Varlık verisini sahiplenmez.** Analytics **hiçbir varlık modülüne bağlanmaz**, hiçbir varlık modülü `Analytics.*`'a bağlanamaz (yalnız host'lar ve testler); her şey `Shared.Contracts.Analytics` portlarıyla. | K5. Kapı modülü olsaydı plan kapalıyken ana sayfa/pano `403 plan.module_disabled` alırdı; kapı modüllerinin varlıkları rapor bazında elenir. M3'ün "ayrı Reporting modülü yok (çapraz JOIN gerekirse açılır)" kararının koşulu bu kartla doğdu. |
| D2 | **Yürütme mimarisi** | **Modül başına `IReportSource` portu** (Shared.Contracts.Analytics): Analytics doğrulanmış, tipli bir `ResolvedReportPlan` verir; varlığın sahibi modül **kendi DbContext'inde, kiracı + yumuşak silme + kayıt kapsamı süzgeci altında** çalıştırıp **sınırlı, tipli toplam** (`ReportSourceResult`: ≤ 1000 grup / ≤ 100 satır sayfa) döner. Analytics modüller arası yalnız **anahtar bazında birleştirir** (etiket, bileşik rapor). **Okuma modeli (olay beslemeli raporlama şeması) v1'de yok**; ağır zaman serileri için **modül sahipli günlük anlık görüntüler** (D8). Port arkasında sonradan okuma modeline geçiş yolu açıktır. | Karşılaştırma tablosu aşağıda ("Mimari karar"): tek doğruluk kaynağı (liste sayfalarıyla aynı satırlar), gerçek zamanlı, kayıt kapsamı/alan izni/özel alan/KVKK silme **bedavaya** doğru; okuma modeli her varlığı ve her kapsam değişikliğini çoğaltır. |
| D3 | Rapor tanımı | Tek, **sürümlü** (`"v": 1`) JSON tanım; **kapalı katalog** (varlık × alan × işleç × toplama × zaman kovası). Filtre = **M9B `FilterDefinition` v1** (yeni dil yok). Hazır raporlar aynı modelin **kodda yazılmış örnekleridir** (özelleştirmek = "Kopyasını oluştur"). **Serbest SQL/ifade/formül yok**; hesaplanmış ölçüler (`weightedAmount`, `avgCycleDays`, `winRate` …) katalogda adlı, kodda sabittir. Çok modüllü **bileşik** raporlar yalnız kodda (kullanıcı kuramaz). | Enjeksiyon yüzeyi sıfır; tek doğrulama/derleme hattı; hazır rapor = test edilmiş örnek tanım. |
| D4 | Birleştirme | Yalnız **N:1 birleştirme** (fırsat→aşama/huni/firma/kişi, kişi→firma …; en çok 3): fan-out (çift sayım) yapısal olarak imkânsız. 1:N (kalem) ihtiyacı **çocuk varlığı birincil** yaparak çözülür (Dalga 2: `quoteLine`, `orderLine`, `invoiceLine`). **Modüller arası** referanslar (firma adı ↔ talep, kampanya ↔ fırsat) SQL birleştirilmez: **kimlik boyutu + etiket çözümleme** (`IReportLabelProvider`) ya da kodda bileşik rapor. | K5 ve doğruluk: 1:N + başlık toplamı yanlış sonuç üretir; tek kural hepsini kapatır. |
| D5 | Kapsam ve alan izni | Kaynak modül **yalnız kapsamlı sorgu** görür (`ScopedReportQuery<T>`: kiracı + silinmemiş + `IRecordScopeProvider` (M9B/M9H)); ham `DbSet` almaz (mimari test). Katalog **çağıran başına** süzülür (alan izni: M9H `IFieldAccess`, hassas özel alan yok); gizli alan **boyut, ölçü, filtre, sıralama, sütun** olarak kullanılamaz (`report.unknown_field`, "yok" ile "yasak" ayırt edilmez). Kayıtlı/paylaşılan tanım **izleyicinin** kimliğiyle doğrulanıp çalışır, **kapalı-başarısız**. | Sayım/gruplama üzerinden sızıntı (oracle) yalnız kaynağı kapatarak önlenir; M9B D20 dikişi kullanılır. |
| D6 | Küçük hücre / çıkarım | **Yapısal savunma:** (a) tüm toplamlar yalnız görünen kayıtlar üzerindedir, **kiracı geneli karşılaştırma değeri kapsamlı kullanıcıya verilmez**; (b) hedef/liderlik yalnız **konusu kapsamın içinde** olan satırlar (aksi 404); (c) PII (ad, e-posta, telefon, serbest metin) **gruplanamaz**; (d) grup sayısı ≤ 1000, tarih kovası ≤ 400; (e) sıralama yalnız seçili boyut/ölçüde. **k-anonimlik eşiği (n<5 bastır) yok:** kapsamlı kullanıcı zaten kendi kayıtlarını görebilir, eşik küçük ekibi (3 kişilik satış) işlevsiz yapardı ve "kapsam dışı fark" saldırısını zaten (a) kapatır. | Gerekçe ve saldırı senaryoları "Kapsam ve çıkarım" bölümünde; testler adversarial bölümündedir. |
| D7 | Maliyet kontrolü | **Ayrı, salt okunur bağlantı havuzu** (`ConnectionStrings:Analytics`, yoksa ana dize; `default_transaction_read_only=on`, `application_name`, küçük `Maximum Pool Size`), `statement_timeout` (etkileşimli 15 sn / arka plan 120 sn), iptal jetonu (istemci kopunca sorgu iptal), kiracı başına eşzamanlı ≤ 3 / kullanıcı ≤ 2 / süreç ≤ 12, hız sınırı, **plan günlük bütçesi** (önbelleksiz çalıştırma), sonuç tavanları, **yalnız kimlik+sayı önbelleği** (etiket ve tablo satırı önbelleğe girmez), tek `RepeatableRead` salt okunur transaction (grup + toplam tutarlı). | OLTP'yi rapordan yapısal olarak yalıtır; hatalı tanım sunucuyu değil yalnız kendi bütçesini yakar. |
| D8 | Anlık görüntüler | Modül sahipli **günlük** anlık görüntü (`ISnapshotMetricSource`, 4 metrik: `pipeline.open`, `forecast.month`, `cases.backlog`, `invoices.receivable`), **sahip boyutlu** (`owner_user_id` her satırda; okumada kayıt kapsamı sahip filtresi olarak uygulanır → kapsamlı kullanıcıya kiracı toplamı sızmaz). Saklama 800 gün. Amaç: geçmiş anın durumu **geriye dönük hesaplanamaz** (hat trendi, öngörü doğruluğu, yaşlandırma geçmişi) + ağır seriler. Canlı raporlar anlık görüntüden **beslenmez** (doğruluk = canlı). | Yeniden yapılandırılamayan tek veri "o günkü durum"dur; başka her şey canlı sorgulanır. |
| D9 | Para birimi | Para ölçüsü **her zaman para birimi boyutuyla** döner (`native`): farklı para birimleri **asla toplanmaz**. M9J gelince `currency.mode = "convert"` (hedef para birimi, **tek kur**: rapor bitiş günü/güncel, `IReportCurrencyConverter` ile kur haritası SQL'e `CASE` olarak itilir; kur yoksa grup **çevrilmeden** ayrı kalır + `report.currency_rate_missing` uyarısı). Dönüştürülmüş çıktı `converted: true` + `rateAsOf` taşır. | Sessiz 1:1 toplama yanlış; kur tablosu başka modülde (JOIN yok); işlem tarihli kur v1 dışı (sınırlama belgelenir). |
| D10 | Zaman | Tüm kovalar/aralıklar **`TenantCalendar`** (kiracı saat dilimi; hafta **pazartesi** — M3 ile aynı; mali yıl/hafta başı M9J ayarı `ReportTimeSettings` ile varsayılanlı); kova SQL'de `date_trunc(unit, timezone(zone, col))`; **boş dönemler motor tarafından 0/`null` ile doldurulur**; etiketler `2026-09`, `2026-W38`, `2026-Q3`, `2026-09-20`. | M3 kuralları tek yerde genellenir; boş dönem doldurma sunucudadır (istemci farkı yok). |
| D11 | Kütüphane | Klasörler **düz** (sistem: modül kategorisi kodda; kiracı: `crm.reports.share`; kişisel). Görünürlük **rapor/pano üzerindedir** (`private \| roles \| everyone`, M9B görünüm modeli), klasörde değil. Favoriler ve son erişim `user_item_state`. | Zoho ekranı (klasör + favori + oluşturan + son erişim) karşılanır; klasör-yetki çift katmanı yok. |
| D12 | Pano | Bileşen türleri `kpi`, `chart`, `table`, `funnel`, `gauge`, `goal`, `queue`; kaynak `report` (kayıtlı/sistem) \| `inline` (gömülü tanım) \| `goal` \| `queue` (M9A anahtarı, **istemcide** çözülür). Düzen **akışlı 12 sütun ızgara + boyut ön ayarları** (`w ∈ {3,4,6,8,12}`, `h ∈ {1,2,3}`), sıra dnd-kit; kalıcılık tek `PUT` (`expectedVersion`). **Toplu veri ucu** (bileşen başına bağımsız durum, eşzamanlı ≤ 3). Bileşen başına `access` (`ok\|forbidden\|moduleDisabled\|definitionInvalid`); yasak bileşenin başlığı/yapılandırması yanıttan **çıkarılır**. | Sürükle-bırak serbest yerleşim (`react-grid-layout`) bağımlılık + a11y + mobil yükü; ön ayarlı akış ızgara aynı ihtiyacı karşılar ve klavye ile yönetilir. |
| D13 | M9A ile widget ortaklığı | **Çerçeve ve kayıt ortak, kalıcılık ayrı:** M9A Ana Sayfa'sı sabit katalog + `workspace.user_preferences` (kişinin günü) olarak kalır; M9F panoları `analytics.dashboards`'ta. Ortak: `web/src/config/widget-registry.ts` (M9A `home-widgets.ts` girdileri aynı `WidgetDefinition` tipine taşınır), `DashboardWidget` çerçevesi (yükleniyor/hata/boş/yasak), `queue` kaynağı M9A `GET /work-queues/counts`'u kullanır. Ana Sayfa'ya pano gömme v1 dışı. | İki ayrı persistans, tek görsel dil; Ana Sayfa'nın "benim günüm" sözleşmesi bozulmaz. |
| D14 | Hedefler | Metrik: `revenueWon`, `dealsWon`, `activitiesCompleted` (+ isteğe bağlı `activityType`). Konu: `tenant \| user \| role` ("takım" M9H hiyerarşisine kadar **rol**). Dönem: ay/çeyrek/yıl/özel (kiracı takvimi). Gerçekleşme **canlı**, motorun hazır planlarıyla (yeni port yok); durum eşikleri sabit. Görünürlük: **konusu kapsamın içinde olan** hedefler. | Aynı motor = tek doğruluk kaynağı; ölçer sayısı rapordaki sayıyla birebir eşleşir. |
| D15 | Öngörü / ML | **Yalnız şeffaf istatistik:** ağırlıklı hat (`tutar × aşama olasılığı`, kapanış ayına göre; "gecikmiş kapanış" ve "tarihsiz" **ayrı kovalar**), taahhüt / en iyi durum, hız (run-rate), 6 dönemlik OLS doğrusal eğilim (R² ve artık sapma bandı, "güven aralığı" **denmez**), öngörü doğruluğu (anlık görüntü ↔ gerçekleşen). **ML/yapay zekâ yok** (gerekçe ve yeniden değerlendirme koşulları "Öngörü" bölümünde). | Kiracı başına yüzlerce kazanılan fırsat; K16 (AI dışı), profilleme/KVKK ve kiracılar arası model eğitimi yasağı; açıklanabilirlik. |
| D16 | Dışa aktarma ve zamanlama | Yalnız **CSV** (M9B `CsvSanitizer`, `crm.data.export`, `maxExportRows`, akış). **Zamanlama** Worker'da; e-posta yalnız **bildirim + bağlantı** (M8A D7 ilkesi), dosya Postgres'te geçici (`report_runs`, gzip, ≤ 10 MB, 7 gün); **her alıcı için kendi kimliğiyle ayrı çalıştırma** (kapsam/alan izni alıcıya göre). Dış e-posta adresi yok. | Tek çıktıyı herkese göndermek dar yetkili alıcıya sızıntıdır; e-posta eki PII'nin kutuya kalıcı çıkışıdır. |
| D17 | Özel alanlar (M8D) | `cf.<key>` **boyut** (`singleSelect`, `boolean`, `date` kovalı, `number/decimal` aralık kovalı) ve **ölçü** (`number/decimal`: sum/avg/min/max) olabilir; yalnız yeni **`is_reportable`** işaretli (varlık başına ≤ 10, hassas olamaz), rapor başına toplam ≤ 3 `cf`. Çoklu seçim gruplanmaz (fan-out). | M8D D12 "rapor ayrı kart" borcunu kapatır; maliyet tavanı M8D D7 ile aynı mantık. |
| D18 | İzinler | Yeni: `crm.reports.build`, `crm.reports.share`, `crm.goals.manage`; `crm.reports.read` (mevcut, `Identity.Contracts`) değişmez; dışa aktarma/zamanlama `crm.data.export` (M9B). Standard: `build` alır; `share`, `goals.manage` (ve zaten `data.export`) alamaz. | Tenant'ın paylaşımı ve hedefleri yönetici işi yapması; keşfi (build) herkes yapabilsin. |
| D19 | Plan (M7) | Yeni özellik bayrakları `features["analytics.builder"]`, `["analytics.dashboards"]`, `["analytics.scheduling"]` (M8A `features` mekaniği) ve sınırlar `maxSavedReports`, `maxDashboards`, `maxScheduledReports`, `maxReportRunsPerDay`. Hazır raporlar, sistem panoları, hedefler, öngörü **her planda açık**. | Değişken maliyet yalnız oluşturucu/zamanlama/bütçedir. |
| D20 | M3/M6 uçları | `/reports/sales/*`, `/reports/activities/by-user`, Commerce/Service/Marketing özet uçları **değişmez** (donuk sözleşme); ancak depoları **`RecordScope` uygulayacak şekilde** güncellenir (M9H'den sonra kapsam sızıntısı olmasın). Web Raporlar sayfası yeni kütüphaneye taşınır, eski uçlar `[DeprecatedEndpoint]` işaretlenir, **bir sonraki majör sürümden önce silinmez**. Parite testi: hazır rapor = eski uç sayıları. | Sıfır kırıcı değişiklik; kapsam sızıntısı riski kapanır. |
| D21 | KVKK | Anlık görüntü ve önbellek **kişisel veri taşımaz** (kimlik + sayı); çıktı dosyaları 7 gün; tanım filtre değerleri denetimde `***`; tablo/CSV PII yalnız liste sayfalarıyla aynı izin/kapsam/alan izniyle; kiracı imhası `TenantDataEraser<AnalyticsDbContext>` ile **otomatik**. | K13/K14; okuma modelinin kişi silme yayılımı sorunu yok. |
| D22 | Gözlemlenebilirlik | Düşük kardinaliteli metrikler (`module`, `outcome`); rapor tanımı/filtre değeri/kullanıcı günlüğe/etikete **asla** girmez. | K20. |
| D23 | Kesme çizgisi | Kart kesilmek zorunda kalırsa sözleşme değişmeden sonraya kalabilir: (1) zamanlama (`/schedules`, `/runs`), (2) öngörü doğruluğu + anlık görüntü arayüzü (yakalama işi yine gelir), (3) Commerce/Marketing/Service oluşturucu varlıkları (hazır raporları kalır), (4) sistem panoları. Kesilemez: motor + kapsam/alan izni + Sales/Activities kaynakları + hazır rapor paritesi + kütüphane + kayıtlı raporlar. | Öncelik P0 rapor oluşturucudur (analiz). |

### Varsayımlar ve komşu kartlarla hizalama
- **Ön koşul M9B:** filtre DSL + `IListFilterCompiler`, `IRecordScopeProvider`, `CsvSanitizer`, dışa aktarma sınırları (`maxExportRows`), görünürlük seçicisi ve `FilterBuilder` web bileşeni. Board: "M9B sonrası". M9B'nin **eklemeli** küçük uzantıları bu kartın 1. adımıdır (bkz. "Kartlar arası takip"): rapor-yalnız varlık kaydı, noktalı (`alan.alt`) anahtar, `ListFieldDefinition`'a isteğe bağlı rapor üst verisi.
- **M9A:** `deals.last_activity_at` (durgun fırsat raporu), widget çerçevesi/kaydı, menü. M9A'nın dar `conditions` DSL'i M9B v1'e çevrilir (M9B belgesi); **M9F yalnız M9B biçimini tanır**.
- **M9C:** fatura (`invoiceDate`, `dueDate`, `paidAmount`, `balanceAmount`, etkin durum) — yaşlandırma raporları; `crm.invoices.read`.
- **M9D:** gecikme tanımı (çağrı/toplantı dahil) M9D D3'tür; `activities.by-user` hazır raporu **M9D tanımını** izler (parite testi M9D sonrası güncellenir).
- **M9E:** `deal.campaignId`, `leadSource`, `type`, `expectedRevenue` (türetilmiş, kolon değil) — kampanya ROI ve kaynak raporları.
- **M8D:** `ICustomFieldSchemas` (seçenek etiketleri, hassaslık); bu kart M8D'ye `is_reportable` ekler (Customization migration `AddReportableFlag`).
- **M8A:** bildirim türü `reportReady`, `features` mekaniği, `Worker:Roles=scheduler`, `IRecipientDirectory`. M8A henüz kodda yoksa `EntitlementSnapshot.Features` eklemesini ilk merge eden yapar (M8A D9 ile aynı biçim).
- **M9H (henüz plan yok):** `IRecordScopeProvider` (M9B tanımı, M9H bağlar) ve `IFieldAccess` (bu belgedeki minimal arayüz **geçici**, aşağıda; ilk merge eden sahiplenir). Varsayılanlar bugünkü davranıştır (tümü/tüm alanlar).
- **M9J (henüz plan yok):** `IReportCurrencyConverter`, `ReportTimeSettings` (hafta başı, mali yıl başlangıç ayı, taban para birimi). Varsayılanlar: dönüşüm yok, pazartesi, Ocak, `TRY`.

## Mimari karar: raporlama sorguları nerede çalışır?

Üç seçenek, aynı ölçütlerle:

| Ölçüt | **A) Modül başına `IReportSource` (seçilen)** | B) Olay beslemeli okuma modeli (`reporting` şeması, gerçek/olgu tabloları) | C) Hibrit (A + modül sahipli günlük anlık görüntü) — **v1 = A + C** |
|---|---|---|---|
| Modül kuralı (K5) | Uyar: yalnız `*.Contracts`; JOIN yok | Uyar ama her varlık için olay şeması + kopya | Uyar |
| Doğruluk | **Tek kaynak** = liste/detay ile aynı satırlar, silinmiş/yumuşak silme/sahip değişimi anında doğru | Kopya sapar: olay kaybı/sırası, geri doldurma, silme/sahip değişimi yayılımı, şema sürümleri; "rapor ≠ liste" hatası kaçınılmaz | Canlı = A; anlık görüntü yalnız "o günkü durum" |
| Tazelik | Gerçek zamanlı (yalnız önbellek TTL'i: 60–300 sn) | Eventual (outbox gecikmesi + işleme) | A + görüntü günlük |
| Kayıt kapsamı (M9H) | Sorguda `WHERE owner IN kapsam`; hiyerarşi değişince **geriye dönük doğru** | Kapsam satıra gömülemez (hiyerarşi değişir) → yine sorgu anında çözülür, kopyanın faydası azalır | Anlık görüntü satırı sahip boyutludur; kapsam okumada |
| Alan izni / özel alan | Aynı katalog, aynı `custom jsonb`; ek iş yok | Özel alan = dinamik sütun/jsonb kopyası; alan izni kopyada yeniden uygulanmalı | A ile aynı |
| KVKK | Silinen satır raporda kendiliğinden yok; kişisel veri çoğalmaz | Kopyadaki kimlik/tutarlar için **kişi/kayıt silme yayılımı** + kiracı imhası ek adım (M7 eraser genellenir) | Görüntü kimlik+sayı, PII yok |
| OLTP etkisi | Var → D7 kontrolleri (ayrı havuz, timeout, kotalar, önbellek); sonra **okuma kopyası** (`ConnectionStrings:Analytics`) | Yok (ayrı şema/DB), ama yazma yolu maliyeti + depolama ≈ 2× | Azaltır (ağır seri) |
| Geliştirme/işletme maliyeti | Düşük: portlar + katalog; mevcut ReadStore desenleri | Yüksek: olay katalogu, backfill, drift izleme, tekrar oynatma, şema göçü | Orta (+1 iş) |
| Ölçek (K3: 10M) | Kiracı önekli kapsayıcı indeksler + kova indeksleri; 1M'de hedefler aşağıda; **10M'de** okuma kopyası + anlık görüntü + zaman sınırı | En iyi (bölümleme/kolonlu) | — |

**Karar:** A + C. Port sözleşmesi (`IReportSource`) `ResolvedReportPlan → ReportSourceResult` olduğu için bir varlık **eşik aşılırsa** (aynı tanım: 2 M satır üstünde p95 > 5 sn, bkz. "Performans") uygulamasını okuma modeline çevirebilir; motor, katalog ve web değişmez. Reddedilen: (1) **tek dev sorgu servisi** (Analytics'in tüm şemalara ham SQL'i) — K5 ve M7 ham SQL envanterine aykırı; (2) **materyalize view'lar** — kiracı+kapsam parametreli olamaz, sızıntı/yenileme sorunu; (3) **istemci tarafı toplama** — 1M satır tarayıcıya taşınmaz.

### Yürütme hattı (tek uç: `POST /analytics/reports/{ref}/run`, `POST /analytics/run`, pano toplu ucu, hedef, öngörü aynı hattı kullanır)
1. **Yetki** (`crm.reports.read`; ad-hoc `run` ayrıca `crm.reports.build`) → `IReportAccess.EnsureAsync(entity, ReportAction)`: (1) tür bilinmiyor `404`, (2) varlık okuma izni `403`, (3) kapı modülü `403 plan.module_disabled`, (4) özellik bayrağı `403 plan.feature_disabled`.
2. **Tanım çözümü:** kayıtlı/sistem/ad-hoc → JSON ayrıştırma (`MaxDepth 8`, yinelenen ad reddi, ≤ 16 KiB) → sürüm → şema/limit doğrulaması → **katalog eşleme (çağıran başına süzülmüş)**: alan/işleç/toplama/kova/grafik uyumu, joins N:1, `cf` `is_reportable` → filtre `IListFilterCompiler` → `ResolvedReportPlan` (opak, yalnız kaynak modül okur; `Digest = sha256(kanonik tanım)`).
3. **Bütçe ve hız:** `analytics-run` hız sınırı → önbellek bakışı (anahtar aşağıda) → isabet değilse günlük bütçe (`analytics.run_counters`; aşım `402 plan.limit_exceeded limit=reportRunsPerDay`) → eşzamanlılık kapısı (2 sn bekle, aksi `429 report.busy`).
4. **Yürütme:** kaynak modülün `ExecuteAsync`'ı, `RepeatableRead` salt okunur transaction, `CommandTimeout = timeout + 5 sn` (veritabanı önce keser; SQLSTATE `57014` → `503 report.timeout`), iptal jetonu istek jetonuna bağlı. Bileşik raporda ≤ 3 kaynak paralel; her biri kendi zaman aşımı.
5. **Sonlandırma:** tarih kovası **doldurma**, etiket çözümleme (üye adları `IMemberLookup`, kayıt adları `IReportLabelProvider`; silinmiş → `null` etiket), para birimi uygulaması, yuvarlama (para 2 hane `AwayFromZero`, oran 4 hane; toplam **tam** toplanıp sonda yuvarlanır), kesme bayrağı, uyarılar.
6. **Önbellek:** yalnız **ham kaynak sonucu** (kimlikler + sayılar, etiketsiz), anahtar `an:{tenantId}:{digest}:{scopeKey}:{fieldKey}:{dayBucket}:{ttlClass}` — `scopeKey` = kapsam `All` ise `all`, değilse sahip kümesinin özeti **ve kullanıcı**; `fieldKey` = izin verilen alan kümesinin özeti; **tablo/dışa aktarma satırları önbelleğe girmez.** TTL: `Analytics:Cache:*` (60 sn; `slow` işaretli hazır raporlar 300 sn). `refresh=true` (kullanıcı başına 10 sn'de 1) önbelleği atlar ama bütçeden düşer.

## Kapsam ve çıkarım (kiracı, kayıt kapsamı, alan izni)

**Garantiler** (her biri bir testtir): (G1) **Kiracı:** kaynaklar kiracı filtreli DbContext'ten okur, yeni `IgnoreQueryFilters` yok (M7 envanteri değişmez); önbellek anahtarı `tenantId` içerir; ayrı havuz aynı `ITenantContext`'i kullanır. (G2) **Kayıt kapsamı:** `ReportSourceBase<TContext>.Query<T>()` yalnız kapsam uygulanmış `IQueryable<T>` verir; kapsam alanı katalogda `OwnerField` ile bağlıdır (`ownerUserId`: deal/lead/account/contact/campaign/quote/order/invoice; `assignedUserId`: activity/case) ve **`OwnerField` boş kayıt reddedilir**; `RecordScope.All` değilse alanı kümede olmayan satırlar (atanmamış talep/aktivite dahil) **görünmez**; boş `OwnerUserIds` = 0 satır. (G3) **Alan izni:** doğrulama çağıranın `IFieldAccess.GetReadableAsync(entity)` kümesine karşı; **filtre** ve **sıralama** da kapsamdadır (gizli alan üzerinden `count` yok). (G4) **Etiket sızıntısı:** grup etiketleri (ör. firma adı) yalnız çağıranın **okuma izni olduğu** türlerde çözülür; okuma izni olmayan referans türünde etiket `null` (kimlik dahi yazılmaz, `***` yer tutucu M3 L2 ilkesiyle `label: null`).

**Saldırı senaryoları ve karşılıkları**
| Saldırı | Karşılık |
|---|---|
| Gizli alanı filtre/gruplama/sıralamayla "sayı sorgusu" olarak kullanmak (`amount > X` sayımı) | Alan katalogdan düşer (G3): `report.unknown_field`; kayıtlı tanımda `report.definition_invalid` |
| Kapsamlı kullanıcının kiracı toplamını fark alarak çıkarması (`Herkes` anahtarı, toplam − kendi) | Kiracı geneli değer yok (D6a); pano `Herkes` düğmesi `RecordScope` ile **kesişir** (genişletmez); hedef/liderlik yalnız kapsam içi konular |
| Grup boyutu 1 olan kovada bireyi tanıma | Bireyin kayıtları zaten görünür ise sorun yok (kapsam); kapsam dışı birey toplam/kova içinde **hiç yer almaz**; PII gruplanamaz |
| Doldurulmuş (0) dönemlerin varlığından gizli kayıt çıkarımı | Doldurma yalnız takvim kovasıdır, varlık/yokluk bilgisi kapsamlı kayıtlardan gelir |
| `countDistinct(accountId)` ile kapsam dışını saymak | Ayrık sayı yalnız kapsamlı satırlar üzerinde; `countDistinct` yalnız `ref/user` alanlarında |
| Paylaşılan raporla yazarın izinlerini ödünç almak | Rapor **izleyici** kimliğiyle çalışır; yazar alanı gizli değilse izleyicide `definition_invalid`; sonuç genişlemez |
| Zamanlanmış çıktıyla dar yetkili alıcıya geniş veri | Alıcı başına kendi kimliğiyle üretim (D16) |
| Önbellek zehirlemesi (aynı tanım, farklı kapsam) | Anahtarda `scopeKey`/`fieldKey`; test |
| Anlık görüntüden kiracı toplamı | Görüntü satırı `owner_user_id` taşır; okuma `owner_user_id IN kapsam`; `RequiresFields` alan izni denetlenir |

**Alan izni arayüzü (geçici; M9H sahiplenir):**
```csharp
namespace Sense.Crm.Shared.Contracts.Security;
public interface IFieldAccess { Task<IReadOnlySet<string>?> GetReadableAsync(string entityType, CancellationToken ct = default); }   // null = tüm alanlar (varsayılan uygulama); küme = izinli alan anahtarları (cf.* dahil)
```
Kapalı-başarısız: `IFieldAccess` bir istisna fırlatırsa rapor **çalışmaz** (`503`), tüm alanlar açılmaz.

## Modül ve bağımlılık düzeni

```
Sense.Crm.Modules.Analytics.{Domain, Application, Contracts, Infrastructure, Api}    (./build/new-module.ps1 -Name Analytics; şema analytics)
Sense.Crm.Shared.Contracts/Analytics/         portlar ve tipler (aşağıda)
Sense.Crm.Shared.Infrastructure/Analytics/    ReportSourceBase<TContext>, ScopedReportQuery<T>, ReportDbFunctions (date_trunc), ReportExpressionBuilder<T>, ReportConnectionFactory
```
- Bağımlılıklar: `Analytics.*` → `Shared.*` + `Identity.Contracts` (`IMemberLookup`, `TenantCalendarService`, roller, `IRecipientDirectory` (M8A)). **Kaynaklar:** Sales, Activities, Service, Commerce, Marketing kendi Infrastructure'larında `IReportSource` + `ReportEntityRegistration` + `IReportLabelProvider` + `ISnapshotMetricSource` uygular; `Add<Modül>ContractServices`'e ekler (Worker zaten çağırır). Analytics onları `IEnumerable<>` ile toplar: **yeni varlık = kayıt işi**. Mimari test: `Sense.Crm.Modules.Analytics.*`'ı yalnız `Sense.Crm.Api`, `Sense.Crm.Worker`, `Sense.Crm.Migrator` ve testler referans eder; her `IReportSource` uygulaması `ReportSourceBase<>`'den türer (ham `DbSet` yok); Analytics.Domain yalnız Kernel'e bağlıdır.
- Modül kaydı (sıcak dosyalar, yalnız ekleme): `ModuleCatalog.cs` (`new AnalyticsModule()`), Migrator (`AnalyticsDbContext` + `InitialAnalytics`, plan doğrulaması yeni anahtarlar), Worker (`AnalyticsDbContext`, `AddModuleHandlers`, `OutboxPollingService<AnalyticsDbContext>`, `AnalyticsScheduleService`, `AnalyticsSnapshotService`, `AnalyticsRetentionService` — hepsi `LockedJobService`, rol `scheduler`), `TestFixture.cs` (Respawn `analytics`), `SystemRoleDefinitions.cs` (`StandardExcluded` += `crm.reports.share`, `crm.goals.manage`), `SharedResource{,.en}.resx`, `backend.md`, web sıcak dosyaları.
- `IUsageReporter` (M7): `Module = "analytics"`, metrikler `analytics.saved_reports`, `analytics.dashboards`, `analytics.goals`, `analytics.schedules`, `analytics.snapshot_rows`, `analytics.run_artifact_bytes` (**`.records` üretmez** → `maxRecords`'a sayılmaz).

### `Shared.Contracts.Analytics` (yeni; özet imzalar)
```csharp
public static class ReportEntityTypes { public const string Deal = "deal", Lead = "lead", Account = "account", Contact = "contact", Activity = "activity", Case = "case", Campaign = "campaign", Quote = "quote", Order = "order", Invoice = "invoice"; }

// --- Katalog (kaynak modül kaydeder; ListFieldDefinition = M9B tipi + isteğe bağlı rapor üst verisi) ---
public sealed record ReportEntityRegistration(string EntityType, string Module, string? GatedModule, string ReadPermission,
    IReadOnlyList<ListFieldDefinition> Fields,               // filtre/sütun/boyut/ölçü alanları (anahtar "alan" veya birleştirilen "takma.alan")
    IReadOnlyList<ReportJoinDefinition> Joins,               // yalnız N:1
    IReadOnlyList<ReportMeasureDefinition> Measures,         // adlı hesaplanmış ölçüler (weightedAmount, winRate, avgCycleDays…)
    IReadOnlyList<ReportTimeFieldDefinition> TimeFields, string DefaultTimeField, bool CanTabular, string? OwnerField /* kapsam alanı */);
public sealed record ReportFieldMeta(bool Groupable, IReadOnlyList<string> Aggregates /* sum,avg,min,max,countDistinct */, IReadOnlyList<string> DateBuckets, string? CurrencyField, bool Sensitive);
public sealed record ReportJoinDefinition(string Alias, string TargetEntity, string Cardinality /* "n:1" */, bool Optional);

// --- Yürütme ---
public interface IReportSource {
    IReadOnlyCollection<string> EntityTypes { get; }
    Task<ReportSourceResult> ExecuteAsync(ResolvedReportPlan plan, ReportExecutionContext context, CancellationToken ct);   // yetki denetimi YAPMAZ (Analytics yapar); kapsam/kiracı ZORUNLU uygular
    IAsyncEnumerable<ReportRow> StreamAsync(ResolvedReportPlan plan, ReportExecutionContext context, CancellationToken ct);  // yalnız tabular/dışa aktarma; anahtar kümeli sayfalama
}
public sealed record ReportExecutionContext(Guid TenantId, Guid? UserId, RecordScope Scope, TenantCalendar Calendar, ReportTimeSettings Time, DateTime NowUtc, TimeSpan Timeout, int MaxGroups, CurrencyPlan Currency);
public sealed record ReportSourceResult(IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Totals, bool Truncated);
public interface IReportLabelProvider { IReadOnlyCollection<string> Kinds { get; } Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct); }  // kiracı kapsamlı; okuma izni Analytics tarafında denetlenir

// --- Anlık görüntü (D8) ---
public interface ISnapshotMetricSource { IReadOnlyCollection<string> Metrics { get; } Task<IReadOnlyList<SnapshotRow>> CaptureAsync(string metric, DateOnly snapshotDate, ReportExecutionContext systemContext, CancellationToken ct); }
public sealed record SnapshotRow(Guid? OwnerUserId, string? Dim1, string? Dim2, string? Currency, long Count, decimal Amount, decimal Weighted);

// --- Dikişler (varsayılanlı; M9H/M9J bağlar) ---
public sealed record ReportTimeSettings(DayOfWeek WeekStart /* Mon */, int FiscalYearStartMonth /* 1 */, string BaseCurrency /* TRY */);
public interface IReportTimeSettings { Task<ReportTimeSettings> GetAsync(CancellationToken ct = default); }
public interface IReportCurrencyConverter { Task<IReadOnlyDictionary<string, decimal>?> GetRatesAsync(IReadOnlyCollection<string> from, string to, DateOnly asOf, CancellationToken ct = default); }   // varsayılan: null (dönüşüm yok)
```
Varsayılanlar `AddCrmCore`'da `TryAdd` (M8D/M9B kalıbı): `IReportTimeSettings` (pazartesi/Ocak/TRY), `IReportCurrencyConverter` (null), `IFieldAccess` (null = tüm alanlar). `ReportSourceBase<TContext>`: yalnız `Query<T>(ReportEntityRegistration)` ile kapsamlı sorgu; `ReportDbFunctions.DateTrunc(unit, ts)` (`date_trunc` eşlemesi, birim sabit katalog değeri) ve `ToLocal(zone, ts)` (Sales `SalesDbFunctions.ToLocalTimestamp` genelleştirilir).

**Uygulama riski ve yedek (spike, 1. adım):** dinamik `GroupBy` anahtarı (0–2 boyut, çalışma zamanı tipleri) ve dinamik `Sum/Avg` seçicisi EF Core ifade ağacıyla `ReportKey0/1/2<,>` sabit genel tipleriyle kurulur. Spike başarısız olursa **yedek:** katalog anahtarından türeyen parametreli SQL parçaları (`ReportSqlBuilder`; tanımlayıcılar yalnız katalog sabitleri, değerler parametre, kullanıcı metni asla SQL'e girmez) — ham SQL envanterine tek satır eklenir, tüm diğer garantiler aynı kalır. Spike çıktısı 1. PR'ın kabul koşuludur.

## Rapor tanımı (JSON v1) ve katalog

```json
{ "v": 1, "entity": "deal", "kind": "summary",
  "joins": ["stage", "account"],
  "filter": { "v": 1, "root": { "kind": "group", "op": "and", "items": [ { "kind": "cond", "field": "account.industry", "op": "in", "value": ["Bilişim"] } ] } },
  "time": { "field": "closedAt", "range": { "kind": "rel", "value": { "kind": "lastNMonths", "n": 12 } }, "bucket": "month", "fillGaps": true },
  "dimensions": [ { "field": "stage.kind", "alias": "kind" } ],
  "measures": [ { "key": "count" }, { "key": "sum", "field": "amount", "alias": "total" }, { "key": "weightedAmount" } ],
  "sort": [ { "by": "period", "dir": "asc" } ], "limit": 100,
  "currency": { "mode": "native" },
  "compare": null,
  "chart": { "type": "column", "stacked": true, "series": "kind" } }
```
- `kind`: `summary` (gruplama + toplama) veya `tabular` (kayıt satırları; ≤ 20 sütun, ≤ 10 000 satır, sayfa ≤ 100, **önbelleksiz**). `time`: zaman alanı (katalogdaki `TimeFields`), aralık = **kayan** (`rel`: M9B göreli türleri + `lastNMonths` yalnız rapor için) veya **mutlak** (`{ "from", "to" }` kiracı günü, uçlar dahil, ≤ 10 yıl) veya `"inherit"` (pano süzgeci); `bucket` ∈ `day\|week\|month\|quarter\|year\|fiscalQuarter\|fiscalYear` (yalnız tarih alanında; zaman boyutu **her zaman ilk boyuttur** ve `period` takma adını alır); kova sayısı ≤ 400 (`report.range_too_large`).
- `dimensions` ≤ 2 (birincisi zaman olabilir): `enum/ref/user/bool` alanlar, `groupable` metin (sektör, ülke, şehir, kaynak…), `date` kovalı, `number/money` **aralık kovalı** (`bucketEdges` ≤ 10, artan), `cf.<key>` (D17). **Ad, e-posta, telefon, serbest metin gruplanamaz.** `measures` ≤ 6: `count`, `countDistinct` (`ref/user`), `sum/avg/min/max` (`number/money`), adlı ölçüler (`weightedAmount`, `winRate`, `avgCycleDays`, `avgFirstResponseMinutes`, `avgResolutionMinutes`, `breachRate`, `conversionRate`, `balanceAmount`…); oran ölçüleri payda 0 iken `null`. `avg/min/max` boş değerleri yok sayar; `sum` boşu 0 sayar (M3 ile aynı: `Amount ?? 0`). `sort` ≤ 2 ve yalnız seçili boyut/ölçü; `limit` ≤ 1000 (varsayılan 100), kesme `truncated: true` (**"Diğer" kovası yok**: toplamlanamayan ölçülerde yanlış olurdu; `totals` **tüm** grupları kapsar).
- `chart.type` uyumluluk matrisi (sunucu `422/400 report.chart_incompatible`; web yalnız uyumluları sunar): `table` her şey; `bar/column` 1–2 boyut + 1–3 ölçü; `line/area` zaman boyutu **zorunlu**; `pie/donut` **1** boyut + **1** ölçü, ≤ 12 dilim; `funnel` sıralı boyut (aşama) + 1 ölçü; `gauge/kpi` boyutsuz, 1 ölçü (+ `compare`). Çoklu para birimi çıktısında seri/dilim para birimine ayrılır.
- `compare`: `previousPeriod` \| `previousYear` — ikinci çalıştırma (kaydırılmış aralık, takvim hizalı) → `delta`, `deltaPct` (önceki 0 ise `null`); yalnız `kpi/gauge` ve boyutsuz özetler.
- **Sürüm:** `v` ≠ desteklenen `422 report.version_unsupported`; saklı tanım okuma anında en yeniyle derlenir (yükseltici oku-zamanı, M9B deseni).
- **Doğrulama sınırları** (`Analytics:Definition:*`): JSON ≤ 16 KiB, filtre M9B limitleri (derinlik ≤ 3, yaprak ≤ 20 …), birleştirme ≤ 3, `cf` toplam ≤ 3, takma ad `^[a-z][A-Za-z0-9_]{0,31}$`, alan/işleç/kova adı yalnız katalog anahtarı; hata yolu `errors["definition.<yol>"]` (ör. `definition.dimensions[0].field`).

### Varlık kataloğu (v1)
`bayraklar`: **Z** zaman alanı (* varsayılan), **B** boyut, **Ö** ölçü. "Etiket" = kimlik boyutu, adı `IReportLabelProvider`/`IMemberLookup` ile çözülür.

| `entityType` | Modül (kapı) / okuma izni | Kapsam alanı | Zaman | Boyutlar | Ölçüler | N:1 birleştirmeler |
|---|---|---|---|---|---|---|
| `deal` | sales / `crm.deals.read` | `ownerUserId` | `createdAt`, `closedAt`*, `closingDate` (date) | `stage.name/kind/order`, `pipeline`, `ownerUserId`, `accountId` (etiket), `contactId`, `type`, `leadSource`, `campaignId` (etiket, Marketing), `lostReason`, `currency`, `cf.*` | `count`, `sum/avg/min/max(amount)`, `weightedAmount` (tutar × aşama olasılığı), `winRate` (kazanılan / kapanan), `avgCycleDays` (`closedAt − createdAt`, yalnız kazanılanlar) | `stage`, `pipeline`, `account`, `contact` |
| `lead` | sales / `crm.leads.read` | `ownerUserId` | `createdAt`* | `status`, `source`, `rating`, `ownerUserId`, `industry`, `cf.*` | `count`, `convertedCount`, `conversionRate` | — |
| `account` | sales / `crm.accounts.read` | `ownerUserId` | `createdAt`* | `industry`, `billingCountry`, `billingCity`, `accountType` (M9E), `ownerUserId`, `cf.*` | `count`, `sum(annualRevenue)` (M9E) | — |
| `contact` | sales / `crm.contacts.read` | `ownerUserId` | `createdAt`* | `ownerUserId`, `accountId`, `leadSource` (M9E), `account.industry`, `cf.*` | `count` | `account` |
| `activity` | activities / `crm.activities.read` | `assignedUserId` | `dueAt`, `completedAt`*, `createdAt` | `type`, `status`, `priority`, `assignedUserId`, `relatedType`, `isOverdue` | `count`, `completedCount`, `overdueCount` (M9D D3 tanımı; notlar hariç) | — |
| `case` | service (`service`) / `crm.cases.read` | `assignedUserId` | `createdAt`*, `resolvedAt` | `status`, `priority`, `channel`, `assignedUserId`, `slaState` (hesaplanmış), `cf.*` | `count`, `slaBreachedCount`, `breachRate`, `avgFirstResponseMinutes`, `avgResolutionMinutes` | — |
| `campaign` | marketing (`marketing`) / `crm.campaigns.read` | `ownerUserId` | `startDate` (date)*, `createdAt` | `type`, `status`, `ownerUserId`, `currency`, `cf.*` | `count`, `sum(budget/actualCost/expectedRevenue)` | — |
| `quote` | commerce (`commerce`) / `crm.quotes.read` | `ownerUserId` | `createdAt`*, `validUntil` (date) | `status` (**etkin**, `expired` bugüne göre), `ownerUserId`, `accountId`, `currency`, `cf.*` | `count`, `sum/avg(grandTotal)`, `conversionRate` | — |
| `order` | commerce / `crm.orders.read` | `ownerUserId` | `orderDate` (date)* | `status`, `ownerUserId`, `accountId`, `currency`, `cf.*` | `count`, `sum(grandTotal)` | — |
| `invoice` (M9C) | commerce / `crm.invoices.read` | `ownerUserId` | `invoiceDate` (date)*, `dueDate` (date) | `status` (**etkin**), `agingBucket` (hesaplanmış: `notDue, d1_30, d31_60, d61_90, d90plus`), `accountId`, `ownerUserId`, `currency` | `count`, `sum(grandTotal/paidAmount)`, `balanceAmount` | — |

Kaynak modül her satır için kaydı yazar; katalog **tamlık testi** (M9B ile aynı): her (alan × boyut/ölçü/işleç) için `ToQueryString()` istemci değerlendirmesine düşmemeli. **Dalga 2:** `quoteLine`, `orderLine`, `invoiceLine` (çocuk varlık birincil; başlığa N:1).

## Hazır raporlar (~22; `system:<key>`, kodda)

Her biri yukarıdaki tanım modelinin örneğidir; "eski uç" sütunu donuk M3/M6 ucunu ve **parite** zorunluluğunu gösterir. Parametreler ortak: `range` (kiracı takvimi), `pipelineId`, `ownerScope` (`me\|all\|userId`, kapsamla kesişir), `currency`.

| Anahtar | Kategori (klasör) | Varlık / kısa tanım | Kapı | Eski uç (parite) |
|---|---|---|---|---|
| `sales.pipeline` | Satış | fırsat: aşamaya göre `count`, `sum(amount)`, `weightedAmount` (güncel durum; tüm aşamalar sıralı, boş 0) | — | `GET /reports/sales/funnel` |
| `sales.won-lost` | Satış | fırsat: `closedAt` kovasına göre kazanılan/kaybedilen sayı+tutar, boş dönem 0 | — | `…/won-lost` |
| `sales.forecast` | Satış | öngörü bölümü (bileşik olmayan; `GET /analytics/forecast` ile aynı hesap) | — | — |
| `sales.cycle` | Satış | kazanılan fırsat `avgCycleDays` sahibe/aya göre | — | — |
| `sales.lost-reasons` | Satış | kaybedilen fırsat: `lostReason` başına `count/sum` | — | — |
| `sales.by-owner` | Satış | temsilci performansı (açık, kazanılan, potansiyel) — **liderlik tablosu** | — | `…/by-owner` |
| `sales.stale-deals` | Satış | açık fırsat: `last_activity_at` N gündür yok (M9A), sahibe göre; N parametre (14/30/60) | — | — |
| `leads.by-source` | Satış | kaynağa göre `count`, `convertedCount`, `conversionRate` | — | `…/leads-by-source` |
| `leads.by-status` | Satış | duruma/sahibe göre dağılım | — | — |
| `activities.by-user` | Aktivite | kullanıcı: tamamlanan/açık/geciken (M9D D3) | — | `…/activities/by-user` |
| `activities.weekly` | Aktivite | hafta × tür tamamlanan (çağrı/toplantı/görev) | — | — |
| `activities.overdue` | Aktivite | geciken açık aktiviteler: sahip × yaş kovası (0–7, 8–30, 31+ gün) | — | — |
| `service.summary` | Destek | durum/öncelik, ort. ilk yanıt/çözüm, SLA ihlal oranı | `service` | Service özeti |
| `service.by-assignee` | Destek | temsilci bazlı | `service` | Service temsilci |
| `service.backlog-aging` | Destek | açık talepler yaş kovası × öncelik | `service` | — |
| `marketing.campaign-roi` | Pazarlama | **bileşik** (Marketing × Sales): kampanya `actualCost` + `deal.campaignId` ile **kazanılan** ciro; `roi = (ciro − maliyet) / maliyet`; **yalnız kampanya para biriminde** (diğer para birimleri ayrı satır, `roi: null` + uyarı); tek dokunuş atfı (M9E kampanya kaynağı) | `marketing` | — |
| `marketing.summary` | Pazarlama | mevcut özet + en iyi kampanyalar | `marketing` | Marketing özeti |
| `commerce.quote-funnel` | Ticaret | teklif durum + dönüşüm oranı + ort. kabul süresi | `commerce` | Ticaret özeti (teklif) |
| `commerce.order-status` | Ticaret | sipariş durum tablosu | `commerce` | Ticaret özeti (sipariş) |
| `commerce.invoice-aging` | Ticaret | **yaşlandırma:** açık bakiye `agingBucket` × para birimi; en borçlu 10 firma | `commerce` (M9C) | — |
| `commerce.quote-order-aging` | Ticaret | bekleyen teklif yaşı / süresi dolacak; onaylı ama tamamlanmamış sipariş yaşı | `commerce` | — |
| `goals.leaderboard` | Hedefler | dönem × metrik kullanıcı sıralaması (hedef varsa `%`) | — | — |

**Yaşlandırma tanımı (`invoice`):** `agingBucket` = fatura **etkin** durumu ödenmemiş/kısmi ise (`sent\|partiallyPaid\|overdue`) `dueDate`'e göre **bugünün** (kiracı günü) `gün farkı`: ≤ 0 `notDue`, 1–30 `d1_30`, 31–60 `d31_60`, 61–90 `d61_90`, > 90 `d90plus`; `draft/cancelled/paid` dışarıda; tutar = `balanceAmount` (M9C). Anlık görüntü `invoices.receivable` geçmiş yaşlandırmayı verir. Teklif/sipariş yaşı: `now − createdAt` (kiracı günü, tam gün).

**Bileşik raporlar** (yalnız kodda): `ICompositeReportDefinition` iki–üç `IReportSource` sonucunu **kimlik anahtarıyla** birleştirir (≤ 1000 anahtar/kaynak); eksik taraf → 0/`null`; her parça kendi kapı/izin/kapsam/alan izni denetiminden geçer, herhangi biri yasak ise rapor `403` (kısmi veri yok). Bugün yalnız `marketing.campaign-roi`.

**M3'ten geçiş:** Raporlar sayfası kütüphane olur; eski sekmeler sistem raporlarına eşlenir (`?tab=funnel` → `/app/reports/system:sales.pipeline`, yer imleri korunur). Eski uçlar donuk; parite testleri iki yolun aynı sayıları verdiğini kanıtlar; eski depolar `RecordScope` uygular (D20).

## Panolar

- **Bileşen** (`dashboard_widgets`): `type` ∈ `kpi \| chart \| table \| funnel \| gauge \| goal \| queue`, `title` ≤ 80, `source` (`report`: `{ ref }` kayıtlı Guid veya `system:*`; `inline`: `{ definition }` ≤ 16 KiB; `goal`: `{ goalId? , metric?, subject? }`; `queue`: `{ key }` — M9A anahtarı, yalnız biçim doğrulanır), `presentation` (`chart` geçersiz kılma, `maxRows` ≤ 10, `showTotals`), `layout` `{ w, h }` + sıra, `refreshSeconds` ∈ `{0 (elle), 60, 300, 900}` (varsayılan 300), `respectsDashboardFilters` (varsayılan `true`). Pano başına ≤ 24 bileşen, ≤ 12 gömülü tanım.
- **Pano süzgeci:** `dateRange` (`rel` veya mutlak) ve `ownerScope` (`me \| all`); bileşenin tanımındaki `time.range = "inherit"` ve `ownerField` üzerinden uygulanır, **kayıt kapsamıyla kesişir**.
- **Yenileme stratejisi:** sunucu önbelleği (60 sn) + istemci **görünürlüğe duyarlı yoklama** (sekme gizliyken durur, `refreshSeconds` ± %10 titreşim, pano başına en çok 1 toplu istek/30 sn); "Yenile" düğmesi `refresh=true` (kullanıcı başına 10 sn'de 1, bütçeden düşer). Hata bileşen başınadır (yeniden dene), pano düşmez.
- **Toplu veri** `POST /analytics/dashboards/{id}/data`: bileşenleri **eşzamanlı ≤ 3** yürütür, toplam süre ≤ 20 sn (`Analytics:Dashboard:BatchSeconds`), bitmeyen bileşen `{ status: "pending" }` döner (istemci tek tek ister), sıra bileşen sırasıdır. Her bileşen `access` taşır: `ok`, `forbidden` (varlık okuma yok), `moduleDisabled`, `featureDisabled`, `definitionInvalid` (`issues`), `notFound` (silinmiş rapor), `error` (`code`). **Yasak/plan dışı bileşende `title`, `source` ve `presentation` yanıttan çıkarılır** (yalnız `id`, `layout`, `access`).
- **Paylaşım kuralı:** `visibility != private` pano yalnız izleyici kitlesine **aynı ya da daha geniş** görünürlükte kayıtlı raporlara/hedeflere bağlanabilir (`422 dashboard.report_not_shared`); gömülü tanımlar serbesttir. Bir panoda kullanılan rapor silinemez (`409 report.in_use`, `args.dashboards`).
- **Sistem panoları** (`system:sales-overview`, `system:service-overview`, `system:activity-overview`, kodda; `GET`, `copy` yapılır, düzenlenmez): satış özeti (KPI: açık hat tutarı, ağırlıklı hat, bu ay kazanılan; huni; kazanılan/kaybedilen; temsilci tablosu; hedef ölçer), servis özeti, aktivite özeti. Kapı modülü kapalı bileşenler gizlenir (`moduleDisabled`).
- **Varsayılanlar:** kullanıcı varsayılanı ve kiracı varsayılanı (paylaşılan `everyone`, `crm.reports.share`); çözüm sırası kullanıcı → kiracı → `system:sales-overview`.
- **Düzen kalıcılığı:** tek `PUT /analytics/dashboards/{id}` tüm bileşenleri ve sırayı tümden değiştirir (`expectedVersion`, `409 general.concurrency_conflict`); web düzenleme kipinde yerel taslak tutar, **Kaydet** ile gönderir. Sürükle-bırak yanında yukarı/aşağı düğmeleri (klavye).

## Hedefler

- **Tanım** (`analytics.goals`, `crm.goals.manage`): `metric` ∈ `revenueWon \| dealsWon \| activitiesCompleted`; `activityType?` (`task\|call\|meeting`, yalnız `activitiesCompleted`); `subject` `{ kind: tenant \| user \| role, id? }` (`user` aktif üye, `role` kiracıda var); `period` `{ kind: month \| quarter \| year \| custom, start, end }` (kiracı günü, uçlar dahil; `custom` ≤ 366 gün; ay/çeyrek/yıl için `start` takvim hizalı ve `end` sunucu türetir); `target` > 0 (`decimal(18,4)`); `currency` (`revenueWon` için zorunlu, diğerlerinde yok). Benzersiz `(metric, activityType, subject, start, end)`. `POST /analytics/goals/bulk-periods` `{ template, year, months[] }` ile 12 ayı tek istekte oluşturur (≤ 24 satır). Tekrar/otomatik devir yok.
- **Gerçekleşme (canlı):** `revenueWon` = `deal` + aşama türü `won` + `closedAt` ∈ dönem + toplam `amount` **hedefin para biriminde** (D9: başka para birimi varsa hesaba **katılmaz**, `warnings: report.currency_unconverted` + para birimleri; M9J dönüşümü sonra); `dealsWon` = aynı filtre `count`; `activitiesCompleted` = `activity` + `completedAt` ∈ dönem + notlar hariç (+ `activityType`). Konuya göre süzgeç: `user` → `ownerUserId`/`assignedUserId = id`; `role` → rolün **aktif üyeleri** (`Identity.Contracts.IRoleMemberLookup`); `tenant` → süzgeç yok (kapsam yine uygulanır). **Sahiplik anı:** puan **güncel sahibe** yazılır (Zoho ile aynı); kazanıldıktan sonra sahibi değişen fırsat kredisi taşır — bilinen sınırlama; öneri: Sales'e `deals.closed_owner_user_id` (Açık işler).
- **Durum** (`elapsed = geçen gün / toplam gün`, dönem başlamadan 0, bittiğinde 1; `expected = target × elapsed`; `ratio = actual / expected`): `achieved` (`actual ≥ target`), `notStarted`, `missed` (dönem bitti, hedef tutmadı), `ahead` (`ratio ≥ 1,1`), `onTrack` (`≥ 0,9`), `behind` (aksi). Eşikler `Analytics:Goals:*`. Yanıt: `actual, target, pct (1 = %100, 4 hane), remaining, expected, elapsedFraction, daysLeft, status, asOf`.
- **Görünürlük:** hedef, **konusu çağıranın kayıt kapsamında** ise görünür (`user`: kişi ∈ `OwnerUserIds`; `role`: tüm aktif üyeler ∈ kapsam; `tenant`: kapsam `All`); aksi `404`. Kapsamı geniş (`All`) herkes hepsini görür. Liderlik tablosu yalnız kapsam içi kullanıcıları listeler.
- **Sayfa:** kişisel ölçerler ("Hedefim"), takım/rol tablosu, liderlik; `gauge`/`goal` bileşenleri (yarım daire `SemiCircleProgress`).

## Öngörü (şeffaf istatistik)

**Tanımlar (bağlayıcı; birim testli saf fonksiyonlar `Analytics.Domain.ForecastMath`, `decimal`):**
1. **Ağırlıklı hat:** açık aşamadaki fırsat için `expected = amount × stage.probability / 100`; kapanış ayı = `closingDate`'in kiracı ayı. **Üç ayrı kova:** dönem içi aylar, **`overdueClose`** (`closingDate < bugün`, hâlâ açık — ay içine **sessizce katılmaz**), **`noDate`** (tarihsiz). `amount` boş → 0 (sayı yine sayılır). Toplam **tam toplanıp** 2 hane yuvarlanır; fırsat başına yuvarlanmış `expectedRevenue` (M9E) toplamından ≤ 0,005 × n fark edebilir (belgelenir, golden test).
2. **Aralık** (açık fırsat terimleri yalnız **kapanış tarihi dönem içinde olanlardır**; `overdueClose`/`noDate` dışarıda ve ayrı gösterilir): `won` (dönemde kazanılan, `closedAt`), `commit` (= `won` + aşama olasılığı ≥ `Analytics:Forecast:CommitMinProbability` (90) olan açık tutar), `expected` (= `won` + ağırlıklı açık), `bestCase` (= `won` + tüm açık tutar). İstatistiksel güven aralığı **değildir**; arayüz "senaryo aralığı" der.
3. **Hız (run-rate):** `paceProjection = won / elapsedFraction` (dönem içi; `elapsedFraction < 0,15` iken `insufficient`). Öngörüyle **ayrı** gösterilir, birleştirilmez.
4. **Eğilim:** son `N` (varsayılan 6, 3–24) **tamamlanmış** dönemde `won` serisi; **OLS doğrusal regresyon** (`slope`, `intercept`), `r2` (`SStot = 0` ise `null`), `residualStdDev = √(SSres/(n−2))`, sonraki iki dönem için tahmin ve `± residualStdDev` bandı (negatif tahmin 0'a kırpılır + `clamped: true`); ayrıca 3 dönemlik **hareketli ortalama**. `n < 6` **veya** sıfır olmayan nokta < 4 → `insufficientData: true` (tahmin yok). Yöntem adı (`linearRegression`) ve girdi noktaları yanıtta döner (denetlenebilir).
5. **Doğruluk:** her ayın **ilk anlık görüntüsündeki** `forecast.month` (`expected`) ile ay bitince gerçekleşen `won` karşılaştırılır: `error = expected − actual`, `biasPct`; en çok 12 ay. Görüntü yoksa boş (geriye dönük doldurma **yok**).
6. **Kota ile bağ:** hedef (D14) varsa `attainment = (won + expected açık) / target` ayrı alan (`projectedPct`).

**ML kararı: gerekçelendirilmedi → uygulanmaz.**
| Ölçüt | Değerlendirme |
|---|---|
| Veri | Tipik kiracıda yılda yüzlerce–birkaç bin kazanılan fırsat; aşama geçmişi anlık görüntü öncesinde yok; küçük örnekte model ağırlıklı hattı geçemez, ölçülebilir kazanç kanıtı yok |
| Açıklanabilirlik | Satış yöneticisi "neden bu sayı" sorusunu yanıtlamalı: çarpım + toplam + doğrusal doğru bunu yapar |
| KVKK | Kişi/firma özellikleriyle skorlama **profilleme**dir (KVKK m.11/1-g itiraz hakkı, otomatik analiz); kiracılar **arası** model eğitimi kiracı verisini başka kiracının kararına sokar (K2/K13 ihlali) |
| Altyapı | Eğitim/servis/izleme/geri alma/drift yok; K16 AI'yı kapsam dışı bıraktı |
| Bakım | Opak model = destek yükü |
| **Yeniden değerlendirme koşulu** | Kiracı başına ≥ 24 ay ve ≥ 5 000 kapanmış fırsat, **kiracıya özel**, **isteğe bağlı açık rıza**, açıklanabilir (GLM/gradyan artırma + özellik önemi), ayrı kart + güvenlik/KVKK incelemesi; başarı ölçütü: ağırlıklı hattın MAPE'sini ≥ %15 düşürmek (anlık görüntüyle çevrimdışı ölçülür) |

## Anlık görüntüler (D8)

| Metrik | Kaynak | Satır ayrıntısı (her satırda `owner_user_id`) | `RequiresFields` |
|---|---|---|---|
| `pipeline.open` | Sales | aşama × sahip × para birimi: `count`, `amount`, `weighted` | `amount`, `stage` |
| `forecast.month` | Sales | kapanış ayı (+ `overdueClose`/`noDate`) × sahip × para birimi: `expected`, `commit`, `bestCase` | `amount`, `closingDate` |
| `cases.backlog` | Service | durum × öncelik × atanan: `count` | — |
| `invoices.receivable` | Commerce (M9C) | yaşlandırma kovası × sahip × para birimi: `balance`, `count` | `balanceAmount` |

`AnalyticsSnapshotService` (`scheduler`, `pg_try_advisory_lock`, saatlik tetik): kiracı yerel saati ≥ 01:00 ve **o yerel günün görüntüsü yoksa** kiracıyı `BeginScope` ile yakalar (`snapshot_date` = kapanan yerel gün; M7 `UsageSnapshotService` kiracı listesi deseni), yeniden çalıştırma **idempotent** (`(tenant, date, metric)` sil-yaz tek transaction). Kiracı başına metrik başına ≤ 20 000 satır (aşım kesilir + `analytics.snapshot_truncated` uyarısı/metrik). Askıdaki (`access != Full`) kiracıda atlanır. Okuma `GET /analytics/snapshots/{metric}`: sahip kapsamı + `RequiresFields` denetimi. Saklama `Analytics:Snapshots:RetentionDays` (800).

## Zamanlama ve dışa aktarma

- **Dışa aktarma** `POST /analytics/reports/{ref}/export` (`crm.data.export` + `crm.reports.read` + varlık okuma izni; salt okunur kiracıda çalışır, `IQuery`): akış CSV (UTF-8 BOM, `;`/`,`/tab, **M9B `CsvSanitizer`** tüm metin hücre ve **başlıklarda**, grup etiketleri dahil — `lostReason`, seçenek etiketi, kampanya adı gibi kullanıcı metinleri `=`/`+`/`-`/`@` ile başlayabilir), sabit ASCII dosya adı (`rapor-<yyyyMMdd-HHmm>.csv`; kullanıcı metni dosya adına girmez), `Cache-Control: no-store`, `X-Total-Rows`. Özet raporda gösterilen tablo (≤ 1000 grup + toplam), tabular'da `StreamAsync` anahtar kümeli akış; satır ≤ `min(plan.maxExportRows, 100 000)` (`402 plan.limit_exceeded limit=exportRows` / `422 export.too_many_rows`, **hiç bayt yazılmadan**), kullanıcı başına saatte ≤ 10 (`lists-export` politikası), kiracı başına eşzamanlı ≤ 2, denetim `analytics.export_log` + `AuditLogEntry` (`ReportExport`: `{ entity, rowCount, columns, definitionDigest }`, PII yok).
- **Zamanlama** (`crm.reports.build` + `crm.data.export` + `features["analytics.scheduling"]`): sıklık `daily \| weekly \| monthly` (saat `HH:mm` kiracı yereli, haftalık `weekday` 1–7, aylık gün 1–28 veya `last`), sıklık altı yok. **Alıcılar** yalnız kiracının aktif üyeleridir (≤ 10; varsayılan yalnız sahip, dış e-posta yok). `AnalyticsScheduleService` (`scheduler`): küresel teknik `analytics.schedule_due` tablosundan (`FOR UPDATE SKIP LOCKED`, kira 300 sn) vadesi gelenleri alır, `BeginScope(tenantId)` yapar (yeni `IgnoreQueryFilters` **yok**), her **alıcı için ayrı** çalıştırma: alıcının canlı izin/kapsam/alan izniyle (`IJobActorScope`) CSV üretir → `report_runs` (gzip `bytea`, ≤ 10 MB, 7 gün) → `ReportRunReady` olayı (M8A `reportReady` bildirimi: uygulama içi + e-posta **yalnız bağlantı**, dosya eki yok; M8A yoksa "Çıktılarım" listesi). Alıcı izni kaybolmuşsa satır `skipped` (`no_permission`), sahip pasifse zamanlama `paused` (`owner_inactive`), ardışık 5 hata `paused` (`too_many_failures`), kiracı `access != Full` ise çalıştırma `skipped` (`tenant_status`) ve **yazma yapılmaz**. Kaçırılan slotlar için **tek** telafi çalıştırması; jitter ± 2 dk; sonraki vade kiracı saat dilimiyle (yaz saati boşluğu: ilk geçerli an; belirsiz saat: erken olan) hesaplanır.
- **İndirme** `GET /analytics/runs/{id}/download`: yalnız **alıcı** (`recipient_user_id`), `crm.reports.read` + `crm.data.export` + varlık izni **indirme anında yeniden** denetlenir; süresi dolmuşsa `410 run.expired`; aksi 404; `Cache-Control: no-store`; indirme `AuditLogEntry` (`ReportRun`, `downloaded`). Sahip yalnız çıktı **meta verisini** (durum/satır/zaman) görür, başkasının içeriğini indiremez.

## Veri modeli (`analytics` şeması, migration `InitialAnalytics`; kimlikler `Guid.CreateVersion7`, `ValueGeneratedNever`; aksi belirtilmedikçe `ITenantEntity`, indeksler `tenant_id` ile başlar)

| Tablo (`Tip`) | Kolonlar / notlar |
|---|---|
| `analytics.saved_reports` (`SavedReport`; `TenantAggregateRoot`, `IAuditLogged`, `xmin`) | `id, tenant_id, name varchar(80), name_normalized, description varchar(300)?, folder_id?, owner_user_id, visibility varchar(8) (private\|roles\|everyone), role_ids uuid[] (≤ 10), entity varchar(16), kind varchar(8) (summary\|tabular), definition jsonb (≤ 16 KiB), definition_version int, created_at, created_user_id, modified_date, modified_user_id`. `SensitiveFields = { definition }` (filtre değerleri kişisel veri olabilir → denetimde `***`). Benzersiz `(tenant_id, owner_user_id, name_normalized) WHERE visibility = 'private'`; benzersiz `(tenant_id, name_normalized) WHERE visibility <> 'private'`; `(tenant_id, folder_id)`; `(tenant_id, entity)`; GIN `(role_ids)` |
| `analytics.report_folders` (`ReportFolder`; `IAuditLogged`) | `id, tenant_id, name varchar(60), name_normalized, scope varchar(8) (personal\|tenant), owner_user_id?, created_at …`; benzersiz `(tenant_id, scope, owner_user_id, name_normalized)`; düz (iç içe yok) |
| `analytics.dashboards` (`Dashboard`; `IAuditLogged`, `xmin`) | `id, tenant_id, name varchar(80), description?, owner_user_id, visibility, role_ids uuid[], filters jsonb (dateRange, ownerScope), is_tenant_default bool, layout_version int, created_at …`; benzersiz ad kuralı `saved_reports` gibi; benzersiz `(tenant_id) WHERE is_tenant_default` |
| `analytics.dashboard_widgets` (`DashboardWidget`; denetim panoyla) | `id, tenant_id, dashboard_id, position int, type varchar(8), title varchar(80), source jsonb (≤ 16 KiB), presentation jsonb?, w smallint, h smallint, refresh_seconds int, respects_filters bool`; `(tenant_id, dashboard_id, position)`; panoyla tek transaction'da tümden yazılır |
| `analytics.goals` (`Goal`; `IAuditLogged`, `xmin`) | `id, tenant_id, metric varchar(24), activity_type varchar(8)?, subject_kind varchar(8), subject_id?, period_kind varchar(8), period_start date, period_end date, target decimal(18,4), currency char(3)?, note varchar(300)?, created_at …`; benzersiz `(tenant_id, metric, coalesce(activity_type,''), subject_kind, coalesce(subject_id, empty), period_start, period_end)`; `(tenant_id, subject_kind, subject_id, period_start)` |
| `analytics.report_schedules` (`ReportSchedule`; `IAuditLogged`) | `id, tenant_id, report_ref varchar(60) (Guid veya system:*), owner_user_id, frequency, weekday?, month_day?, at_time time, recipients uuid[] (≤ 10), format varchar(4) 'csv', is_enabled, paused_reason varchar(24)?, failure_count int, last_run_at?, next_run_at, created_at …`; `(tenant_id, owner_user_id)` |
| `analytics.schedule_due` (`ScheduleDue`; **`ITenantEntity` değil**, küresel teknik tablo, M9B `job_queue` deseni) | `schedule_id PK, tenant_id, next_run_at, lease_until?, claimed_by?, attempts`; `(next_run_at) WHERE lease_until IS NULL`; `TenantQueryFilterConventionTests.GlobalEntities`'e eklenir; kiracı imhasında `AnalyticsQueueEraser` parametreli siler |
| `analytics.report_runs` (`ReportRun`; denetimsiz teknik, indirme açık yazılır) | `id, tenant_id, schedule_id?, report_ref, recipient_user_id, status varchar(10) (queued\|running\|succeeded\|failed\|skipped\|expired), error_code varchar(40)?, row_count, size_bytes, content bytea (gzip)?, created_at, finished_at?, expires_at`; `(tenant_id, recipient_user_id, created_at DESC)`; `(tenant_id, schedule_id)` |
| `analytics.metric_snapshots` (`MetricSnapshot`; denetimsiz) | `tenant_id, snapshot_date date, metric varchar(24), owner_user_id?, dim1 varchar(64)?, dim2 varchar(64)?, currency char(3)?, count bigint, amount decimal(18,4), weighted decimal(18,4)`; benzersiz `(tenant_id, snapshot_date, metric, coalesce(owner_user_id, empty), coalesce(dim1,''), coalesce(dim2,''), coalesce(currency,''))`; `(tenant_id, metric, snapshot_date)` |
| `analytics.user_item_state` (`UserItemState`; denetimsiz) | `tenant_id, user_id, item_type varchar(9) (report\|dashboard), item_ref varchar(60), is_favorite bool, last_accessed_at?`; PK `(tenant_id, user_id, item_type, item_ref)` |
| `analytics.run_counters` (denetimsiz) | `tenant_id, day date, uncached_runs int`; PK `(tenant_id, day)`; 40 gün sonra silinir |
| `analytics.export_log` (append-only, denetimsiz) | `id, tenant_id, user_id, report_ref, started_at, finished_at?, status (running\|completed\|aborted), row_count, definition_digest char(64)` |
Modülün `outbox_messages/inbox_messages` tabloları (`AddModuleDbContext`). **Kiracı imhası** `TenantDataEraser<AnalyticsDbContext>` ile ek adımsız (çıktı dosyaları dahil); `schedule_due` küresel olduğundan `AnalyticsQueueEraser` (ham SQL envanterine +1).

**Varlık modüllerinde yalnız indeks migration'ları (`AddReportIndexes`, sahibi modül; mevcut modelle karşılaştırılıp eksik olanlar eklenir, yinelenen eklenmez):**
| Modül | İndeks (kapsayıcı, kısmi) — amaç |
|---|---|
| Sales | `deals (tenant_id, stage_id) INCLUDE (amount, owner_user_id, currency, pipeline_id) WHERE NOT is_deleted` (hat/öngörü); `deals (tenant_id, closed_at) INCLUDE (stage_id, amount, owner_user_id, currency) WHERE closed_at IS NOT NULL AND NOT is_deleted` (kazanılan/kaybedilen, hedef); `deals (tenant_id, closing_date) WHERE NOT is_deleted` (öngörü kovası); `leads (tenant_id, created_at) INCLUDE (source, status, owner_user_id) WHERE NOT is_deleted` |
| Activities | `activities (tenant_id, completed_at) INCLUDE (assigned_user_id, type) WHERE completed_at IS NOT NULL AND NOT is_deleted`; `(tenant_id, assigned_user_id, due_at) WHERE status = 'open' AND NOT is_deleted` (M9A/M9D ile birleştirilir) |
| Service | `cases (tenant_id, created_at) INCLUDE (status, priority, assigned_user_id) WHERE NOT is_deleted` |
| Commerce | `invoices (tenant_id, due_date) INCLUDE (balance_amount, currency, owner_user_id) WHERE status IN (…ödenmemiş…) AND NOT is_deleted`; `quotes/orders (tenant_id, created_at\|order_date)` |
| Marketing | `campaigns (tenant_id, start_date)` |
Customization (M8D): `field_definitions.is_reportable bool NOT NULL DEFAULT false` (`AddReportableFlag`). **Yazma-yolu maliyeti:** her kapsayıcı indeks yazmayı ≈ %3–8 yavaşlatır (küçük ek); `INCLUDE` sütunları güncellenmeyen alanlardır (`amount` hariç: fırsat tutarı güncellemesi indeks satırı yeniden yazar — kabul).

## Plan / limitler (M7 entegrasyonu, D19)
- **Yeni sayısal limitler** (`limits.*`, `int?`; `null`/yok = plan sınırsız, `0` = özellik kapalı): `maxSavedReports`, `maxDashboards` (kiracı toplamı), `maxScheduledReports`, `maxReportRunsPerDay` (**yalnız önbelleksiz** çalıştırma; önbellek isabeti ve hazır rapor önbellek isabetleri bedava). Teknik tavanlar plandan bağımsız: rapor tanımı ≤ 2000/kiracı, kullanıcı başına ≤ 100 rapor + ≤ 20 pano + ≤ 10 zamanlama, hedef ≤ 2000/kiracı, klasör kişisel ≤ 30 / kiracı ≤ 50.
- **Özellik bayrakları** (`features[...]`, M8A biçimi; eksik anahtar = kapalı): `analytics.builder` (özel rapor **oluşturma/düzenleme/ad-hoc run**; kapalıyken kayıtlı raporlar **çalışır**), `analytics.dashboards`, `analytics.scheduling`. Kapalıyken `403 plan.feature_disabled` (`args.feature`); hazır raporlar, sistem panoları, hedefler, öngörü, **dışa aktarma** her planda açıktır (dışa aktarma `maxExportRows` ile sınırlıdır).
- **Örnek plan yapılandırması (ticari karar değil, test verisi):** `internal` hepsi açık/sınırsız; `starter`: builder/dashboards/scheduling **kapalı**, `maxReportRunsPerDay 300`; `business`: hepsi açık, `maxSavedReports 200`, `maxDashboards 20`, `maxScheduledReports 10`, `maxReportRunsPerDay 5000`; `enterprise`: sınırsız. Kiracı istisnası `overrides.*` (M8D deseni), `PUT /platform/organizations/{id}/subscription` ve konsol taşır; Migrator plan doğrulaması ≥ 0; Web `plan-usage.tsx` satırları.
- **Zorlama:** oluşturma komutları `ILimitGuard.EnsureAsync(new LimitDemand(LimitKeys.SavedReports\|Dashboards\|Schedules, "analytics", Delta: 1, Used: mevcutSayı))` (M8D `LimitDemand.Used` eki; ilk merge eden ekler); günlük bütçe handler'da (`402 plan.limit_exceeded limit=reportRunsPerDay`); `Create*Command` kuralı: `[NoPlanLimit("...")]` gerekçeli (kayıt değil; sayı sınırı handler'da). **Plan düşürme:** mevcut rapor/pano/zamanlama silinmez ve çalışır; yeni oluşturma yeni tavana tabidir; builder kapanırsa kayıtlı raporlar çalışır, düzenlenemez. **Salt okunur/askı:** yazma uçları (`reports/dashboards/goals/schedules/folders` CRUD) `403 tenant.suspended`; **run, meta, kütüphane okumaları, dışa aktarma, öngörü, hedef gerçekleşmesi, çıktı indirme** salt okunurda çalışır; favori değiştirme muaf (`[TenantStatusExempt("kişisel arayüz durumu")]` — onaylı liste bu tek satırla genişler, güvenlik incelemesi); `accessLevel none` hepsini keser.

## İzinler
`AnalyticsPermissions` (modül `analytics`, grup `crm`): **`crm.reports.build`**, **`crm.reports.share`**, **`crm.goals.manage`** → `GET /permissions` +3 (test sayı sabitlemez). `crm.reports.read` `Identity.Contracts.CrmPermissions`'ta kalır; dışa aktarma/zamanlama `crm.data.export` (`ListsPermissions`). `SystemRoleDefinitions.StandardExcluded` += `crm.reports.share`, `crm.goals.manage` (Administrator = katalogun tümü; Standard `crm.reports.build` alır); `SystemRolePermissionSynchronizer` mevcut kiracılara yayar (test: eski kiracıda Standard `build` alır, `share`/`goals.manage` almaz).
- **Yetki mimarisi** (M2/M7 kuralı: her komut/sorgu tam bir yetki özniteliği): rapor çalıştırma/kütüphane okuma `crm.reports.read` (+ handler'da **`IReportAccess.EnsureAsync`**: tür → varlık okuma izni → kapı modülü → özellik bayrağı; mimari test her Analytics handler'ının ilk çağrısının bu olduğunu ister); oluşturma/düzenleme `crm.reports.build`; paylaşım/kiracı klasörü/kiracı varsayılan pano `crm.reports.share` (handler'da `visibility != private` için ek); hedef CRUD `crm.goals.manage`, hedef okuma `crm.reports.read`; zamanlama `crm.reports.build` + `crm.data.export`; export `crm.data.export` + `crm.reports.read`.
- **API anahtarı (M8B):** anahtar kapsamı ∩ oluşturanın izinleri; `/analytics/**` çağrılabilir (aynı kotalar, anahtar başına ayrı hız kovası); anahtarla dışa aktarma/zamanlama yalnız `crm.data.export` taşıyorsa.
- **Rol silinmesi/pasif üye:** paylaşım `roleIds` listesinden düşer (hiç geçerli rol kalmazsa yalnız sahip/`share` görür, `issues: report.share_roles_missing`); sahibi pasif üyenin **özel** rapor/panosu görünmez, paylaşılanlar kalır.

## Yapılandırma (`Analytics:*`, başlangıçta doğrulanır — geçersiz değer uygulamayı başlatmaz)
| Anahtar | Varsayılan | Not |
|---|---|---|
| `Definition:MaxBytes / MaxDimensions / MaxMeasures / MaxJoins / MaxCfRefs / MaxColumns` | 16384 / 2 / 6 / 3 / 3 / 20 | tanım limitleri |
| `Query:InteractiveTimeoutSeconds / BackgroundTimeoutSeconds / MaxGroups / MaxTabularRows / MaxTimeBuckets` | 15 / 120 / 1000 / 10000 / 400 | |
| `Concurrency:PerTenant / PerUser / PerProcess / WaitSeconds` | 3 / 2 / 12 / 2 | süreç başına; çok kopyada etkin sınır kopya × değer (belgelenir); veritabanı zaman aşımı asıl güvencedir |
| `Pool:InteractiveMax / BackgroundMax` | 8 / 3 | `ConnectionStrings:Analytics` (opsiyonel; yoksa ana dize) |
| `Cache:TtlSeconds / SlowTtlSeconds / RefreshMinIntervalSeconds` | 60 / 300 / 10 | testte 0 |
| `Dashboard:MaxWidgets / MaxInline / BatchSeconds / BatchConcurrency` | 24 / 12 / 20 / 3 | |
| `Goals:AheadRatio / OnTrackRatio / MaxPerTenant` | 1.1 / 0.9 / 2000 | |
| `Forecast:CommitMinProbability / TrendPeriods / MinTrendPeriods / MinElapsedFraction` | 90 / 6 / 6 / 0.15 | |
| `Snapshots:Enabled / RetentionDays / MaxRowsPerMetric` | true / 800 / 20000 | |
| `Schedules:Enabled / MaxRecipients / MaxPerUser / MaxFailures` | true / 10 / 10 / 5 | |
| `Runs:MaxBytes / RetentionDays` | 10485760 / 7 | |
| `Builder:Enabled` | true | acil kapatma anahtarı (plan bayrağından bağımsız) |
Hız sınırı politikaları (`RateLimitPolicyNames`): `analytics-run` (kullanıcı başına dakikada 120; pano toplu ucu 1 istektir), dışa aktarma `lists-export` yeniden kullanılır.

## HTTP kontratı (`/api/v1/analytics`, Bearer)

**Meta**
- `GET /analytics/entities` → 200 `[ { entityType, module, gated: false, canTabular, joins: [{ alias, entityType }] } ]` — yalnız çağıranın **okuma izni + kapı modülü + planı** uyanlar (aksi hiç yazılmaz). `crm.reports.read`. `Cache-Control: private, no-cache`.
- `GET /analytics/entities/{entityType}/meta` → 200 `{ entityType, fields: [{ key, type, enumValues?, groupable, aggregates[], dateBuckets[], filterOperators[], sortable, money?: { currencyField } }], timeFields: [{ key, isDefault }], measures: [{ key, type, requiresFields[] }], joins, limits, chartMatrix, cf: [{ key, type, labels, isReportable }] }` — **çağıranın alan izniyle süzülmüş** (izinsiz alan hiç yazılmaz); `403 forbidden` / `plan.module_disabled` / `404 report.entity_unknown`.

**Çalıştırma**
- `POST /analytics/reports/{ref}/run` — `{ params?: { range?, ownerScope?, pipelineId?, currency?, page?, pageSize?, refresh? } }` → 200 `ReportResult`. `ref` = Guid (kayıtlı) veya `system:<key>`. `IQuery` (salt okunur kiracıda çalışır). Görünmeyen/olmayan rapor `404 not_found`.
- `POST /analytics/run` — `{ definition, params? }` → 200 `ReportResult` (oluşturucu önizlemesi; `crm.reports.build` + `features["analytics.builder"]`; **hiçbir şey kaydedilmez**, önbellek anahtarı aynı, bütçeden düşer).
```json
{ "report": { "ref": "system:sales.won-lost", "name": "…", "entity": "deal", "kind": "summary", "chart": { "type": "column" } },
  "columns": [ { "key": "period", "kind": "dimension", "type": "month" }, { "key": "count", "kind": "measure", "type": "int" }, { "key": "total", "kind": "measure", "type": "money", "currencyColumn": "currency" }, { "key": "currency", "kind": "dimension", "type": "currency" } ],
  "rows": [ { "period": { "key": "2026-09", "label": "2026-09" }, "count": 12, "total": 150000.00, "currency": "TRY" } ],
  "totals": { "count": 12, "total": 150000.00 },
  "range": { "from": "2026-01-01", "to": "2026-09-20", "timeZone": "Europe/Istanbul" },
  "truncated": false, "rowCount": 9, "computedAt": "2026-09-20T10:15:00Z", "cached": false,
  "scope": "all", "currencies": [ "TRY" ], "converted": false,
  "warnings": [ { "code": "report.currency_unconverted", "args": { "currencies": [ "USD" ] } } ],
  "compare": null, "page": 1, "pageSize": 100, "totalCount": 9 }
```
`scope`: `all` \| `restricted` (arayüz "Kapsam: görebildiğiniz kayıtlar"); `rows[]` boyut hücreleri `{ key, label }` (etiket çözülemezse `label: null`), ölçü hücreleri düz sayı; `totals` yalnız toplanabilir ölçüler + oran/ortalama ölçüler **bütünden** hesaplanır (grup ortalamalarının ortalaması değil); tabular'da `rows[]` düz hücre nesneleri + `id`/`entityType` (**yalnız** varlık okuma izni varsa; detay bağlantısı için). Sayfalama yalnız tabular (`page ≤ 100 pageSize`, `offset ≤ 10 000`). `ownerScope`/`pipelineId` yalnız tanımda `ownerField`/`pipeline` alanı olan varlıklarda anlamlıdır (aksi yok sayılmaz: `validation errors["params.pipelineId"]`).
- `POST /analytics/reports/{ref}/export` — bkz. "Zamanlama ve dışa aktarma".

**Kütüphane**
- `GET /analytics/reports?kind=all\|system\|mine\|shared\|favorite&folderId&folderKey&entityType&q&sort=name\|-updatedAt\|-lastAccessedAt&page&pageSize` → sayfalı `[ { ref, name, description?, kind: "system"\|"custom", folder: { key\|id, name }, entity, reportKind, chartType, ownerName?, visibility?, isFavorite, lastAccessedAt?, updatedAt?, canEdit, available, unavailableReason? } ]` (`available=false`: `module_disabled` / `forbidden` / `feature_disabled`; **yasak rapor adı yazılmaz**: `forbidden` olanlar listede hiç yer almaz, `module_disabled` olanlar kapı modülü adıyla gösterilir). `q` ad + açıklama (`ILIKE`, kaçışlı).
- `GET /analytics/reports/{ref}` → 200 tanım (`definition`, `issues[]`, `version`, `canEdit`, `usedByDashboards`); sistem raporunun tanımı **salt okunur** (kopya için).
- `POST /analytics/reports` `{ name*, description?, folderId?, visibility?, roleIds?, definition* }` → **201** (+ `Location`). `PUT /analytics/reports/{id}` (tam değiştirme, `expectedVersion*`) → 204. `DELETE /analytics/reports/{id}` → 204 (fiziksel + denetim; `409 report.in_use`). `POST /analytics/reports/{ref}/copy` `{ name? }` → 201 (kaynak sistem/paylaşılan/kendi). `PUT /analytics/reports/{id}/folder` `{ folderId? }` → 204. `PUT /analytics/items/{type}/{ref}/favorite` / `DELETE` → 204 (favori; `type` = `report\|dashboard`).
- Oluşturma/güncelleme: `definition` **tam doğrulanır** (biçim, katalog, alan izni, filtre, grafik uyumu, plan/özellik) — geçersizse **kaydedilmez**: `400 validation errors["definition.<yol>"]`; paylaşılan (`roles/everyone`) `crm.reports.share`; ad benzersizlik `409 report.name_taken`; limit `422 report.limit_reached` (`args.scope`) / `402 plan.limit_exceeded`.
- Klasörler: `GET /analytics/folders` (sistem + kiracı + kişisel, sayılarla), `POST /analytics/folders` `{ name, scope }` (kiracı klasörü `crm.reports.share`), `PUT /analytics/folders/{id}` `{ name }`, `DELETE /analytics/folders/{id}` (boş olmalı: `409 folder.not_empty`; sistem klasörü `422 report.system_readonly`).

**Panolar**
- `GET /analytics/dashboards` → `[ { id\|ref, name, description?, visibility, ownerName?, isSystem, isDefault, isTenantDefault, isFavorite, widgetCount, canEdit, updatedAt } ]` (sistem panoları `system:*` + benimkiler + bana görünen paylaşılanlar).
- `GET /analytics/dashboards/{id}` → 200 `{ …, filters: { dateRange?, ownerScope }, widgets: [ { id, type, title, source, presentation, layout: { w, h }, refreshSeconds, respectsFilters, access, issues? } ], version }` — **yasak/plan dışı bileşende `title/source/presentation` yok**.
- `POST /analytics/dashboards` `{ name*, description?, visibility?, roleIds?, filters?, widgets[] }` → 201 (`crm.reports.build` + `features["analytics.dashboards"]`; paylaşım `crm.reports.share`); `PUT /analytics/dashboards/{id}` (tam değiştirme, `expectedVersion*`) → 204; `DELETE` → 204; `POST /analytics/dashboards/{ref}/copy` → 201; `PUT /analytics/dashboards/{id}/default` → 204 (kullanıcı; `id` Guid veya `system:*`); `PUT /analytics/dashboards/{id}/tenant-default` (`share`; `everyone` olmalı `422 dashboard.tenant_default_requires_everyone`) → 204.
- `POST /analytics/dashboards/{ref}/data` `{ filters?: { dateRange?, ownerScope? }, widgetIds?: [...], refresh?: false }` → 200 `{ computedAt, widgets: [ { id, status: "ok"\|"pending"\|"error", access, result?: ReportResult\|GoalAttainment, error?: { code } } ] }`. Bileşen başına hata yanıtı düşürmez; toplam bütçe 20 sn.

**Hedefler** (`crm.reports.read` okuma; `crm.goals.manage` yazma; kapsam kuralı D14)
- `GET /analytics/goals?metric&subjectKind&subjectId&periodStart&periodEnd&periodKind&page&pageSize` → sayfalı (yalnız **kapsamdaki** konular).
- `GET /analytics/goals/{id}` → tanım; `POST /analytics/goals` `{ metric*, activityType?, subject*, period*, target*, currency?, note? }` → 201; `PUT /analytics/goals/{id}` (tam, `expectedVersion`) → 204; `DELETE` → 204; `POST /analytics/goals/bulk-periods` `{ metric, activityType?, subject, year, target[12] | targetPerMonth, currency? }` → 201 `{ created: n, skipped: [ { period, code } ] }` (≤ 24, mevcut çakışmalar atlanır).
- `GET /analytics/goals/{id}/attainment` → 200 `GoalAttainment`; `POST /analytics/goals/attainment` `{ goalIds: [≤ 20] }` → toplu (pano bileşenleri); `GET /analytics/goals/leaderboard?metric&periodStart&periodEnd&subject=user\|role&limit≤50` → `[ { subject: { kind, id, name }, actual, target?, pct?, rank } ]` (yalnız kapsam içi; **sıralama kapsamdaki satırlar arasında**, kiracı sırası verilmez).
```json
{ "goalId": "…", "metric": "revenueWon", "subject": { "kind": "user", "id": "…", "name": "Ada Lovelace" }, "period": { "kind": "month", "start": "2026-09-01", "end": "2026-09-30" },
  "target": 500000.00, "actual": 310000.00, "currency": "TRY", "pct": 0.62, "remaining": 190000.00, "expected": 333333.33, "elapsedFraction": 0.6667, "daysLeft": 10, "status": "onTrack",
  "warnings": [ { "code": "report.currency_unconverted", "args": { "currencies": [ "USD" ], "count": 3 } } ], "asOf": "2026-09-20T10:15:00Z" }
```

**Öngörü** (`crm.reports.read` + `crm.deals.read`)
- `GET /analytics/forecast?periodKind=month\|quarter&periodStart&pipelineId&ownerScope=me\|all` → 200
```json
{ "period": { "kind": "month", "start": "2026-09-01", "end": "2026-09-30" }, "asOf": "…", "scope": "restricted",
  "method": { "weighting": "stageProbability", "commitMinProbability": 90 }, "warnings": [],
  "currencies": [ { "currency": "TRY",
    "summary": { "won": 310000.00, "commit": 420000.00, "expected": 505000.00, "bestCase": 640000.00, "openCount": 41, "paceProjection": 465000.00, "paceStatus": "ok" },
    "stages": [ { "stageId": "…", "name": "Teklif", "probability": 60, "count": 12, "amount": 300000.00, "expected": 180000.00 } ],
    "buckets": { "overdueClose": { "count": 5, "amount": 90000.00, "expected": 40000.00 }, "noDate": { "count": 3, "amount": 25000.00, "expected": 10000.00 } },
    "months": [ { "month": "2026-10", "count": 9, "amount": 200000.00, "expected": 110000.00 } ],
    "goal": { "goalId": "…", "target": 600000.00, "projectedPct": 0.84 } } ] }
```
Para birimi başına ayrı gövde: `currencies` **her zaman dizidir** (tek para biriminde tek öğe); farklı para birimleri toplanmaz (D9; M9J dönüşümü `currency` parametresiyle tek öğeye indirger, `converted: true`). `goal` yalnız o para biriminde hedef varsa yazılır.
- `GET /analytics/forecast/trend?metric=won\|dealsWon&periods=6..24&periodKind=month` → `{ points: [ { period, actual } ], movingAverage: [ … ], fit: { method: "linearRegression", n, slope, intercept, r2, residualStdDev }, projection: [ { period, value, low, high, clamped } ], insufficientData }`.
- `GET /analytics/forecast/accuracy?months=1..12` → `[ { month, forecastAtStart: { expected, commit, bestCase }, actual, error, biasPct, snapshotDate } ]`.
- `GET /analytics/snapshots/{metric}?from&to&groupBy=date\|dim1\|owner&ownerUserId?` → sahip kapsamı ve alan izni uygulanmış seri (`metric` bilinmiyor `404 snapshot.metric_unknown`; `RequiresFields` gizli `403`).

**Zamanlama**
- `GET/POST /analytics/schedules`, `GET/PUT/DELETE /analytics/schedules/{id}` (`PUT` tam, `expectedVersion`), `POST /analytics/schedules/{id}/pause`, `/resume`, `/run-now` (kullanıcı başına saatte 5; `202` + `runIds`). Gövde: `{ reportRef*, frequency*, weekday?, monthDay?, atTime*, recipients[]?, format: "csv" }`. Alıcı aktif üye olmalı (`400 errors["recipients[i]"] = schedule.recipient_not_member`); rapor **sahibin** okuyabildiği olmalı.
- `GET /analytics/runs?scheduleId&status&page&pageSize` (kendi çıktılarım + sahibi olduğum zamanlamanın meta verisi), `GET /analytics/runs/{id}/download`.

## Hata kodları (metinler `SharedResource{,.en}.resx` tr/en; alan mesajları `errors` içinde çevrilir)
| code | HTTP | Ne zaman |
|---|---|---|
| `validation` | 400 | Girdi geçersiz: `errors["definition.<yol>"]`, `["params.<alan>"]`, `["recipients[i]"]`, `["widgets[i].<yol>"]`, `["period"]`, `["target"]` …; mesaj anahtarları `report.malformed`, `report.unknown_property`, `report.too_large`, `report.unknown_field` (yok **veya** yasak), `report.operator_not_supported`, `report.aggregate_not_supported`, `report.bucket_not_supported`, `report.group_not_allowed` (PII/`multiSelect`), `report.join_not_available`, `report.chart_incompatible`, `report.too_many_dimensions`, `report.too_many_measures`, `report.range_too_large`, `report.bucket_edges_invalid`, `report.cf_not_reportable`, `report.cf_too_many` + M9B `filter.*` |
| `forbidden` | 403 | İzin yok (eylem/varlık/paylaşım) |
| `plan.module_disabled` | 403 | Varlığın kapı modülü kapalı (`args.module`) |
| `plan.feature_disabled` | 403 | Özellik bayrağı kapalı (`args.feature`); M8A tanımladıysa aynı kod |
| `plan.limit_exceeded` | 402 | `savedReports/dashboards/schedules/reportRunsPerDay/exportRows` (`args.limit`, `max`, `used`) |
| `tenant.suspended` | 403 | Salt okunur kiracıda yazma (`args.reason`) |
| `not_found` | 404 | Rapor/pano/hedef/zamanlama/çıktı/klasör yok, görünmez ya da başka kiracıda (varlık sızdırılmaz); kapsam dışı hedef |
| `report.entity_unknown` | 404 | Kayıtsız varlık türü |
| `snapshot.metric_unknown` | 404 | Bilinmeyen metrik |
| `report.definition_invalid` | 422 | Kayıtlı/paylaşılan tanım artık geçersiz (alan gizlendi/silindi/arşivlendi, rol/izin değişti): `issues[]` (`path`, `code`) — **kapalı-başarısız**, kısmi veri yok |
| `report.version_unsupported` | 422 | Tanım `v` desteklenmiyor (`args.v`) |
| `report.name_taken` / `folder.name_taken` | 409 | Ad kapsamda kullanımda |
| `report.in_use` | 409 | Panoda kullanılan rapor silinemez (`args.dashboards`) |
| `folder.not_empty` | 409 | Dolu klasör silinemez |
| `general.concurrency_conflict` | 409 | `version` çakışması |
| `report.limit_reached` / `dashboard.limit_reached` / `goal.limit_reached` / `schedule.limit_reached` | 422 | Teknik tavan (`args.scope`, `args.max`) |
| `report.system_readonly` | 422 | Sistem rapor/pano/klasörünü düzenleme/silme |
| `dashboard.report_not_shared` | 422 | Paylaşılan panoda kitleye görünmeyen rapor (`args.path`) |
| `dashboard.tenant_default_requires_everyone` | 422 | Kiracı varsayılanı `everyone` olmalı |
| `goal.duplicate` | 409 | Aynı metrik/konu/dönem hedefi var |
| `goal.subject_invalid` | 400 | Konu aktif üye/rol değil (`errors["subject"]`) |
| `report.busy` | 429 | Eşzamanlılık kapısı (`args.reason`: `tenant\|user\|process`) veya `analytics-run` hız sınırı (`general.rate_limit_exceeded`) |
| `report.timeout` | 503 | Sorgu zaman aşımı (`Retry-After: 30`); yeni `ErrorType.Unavailable` (`ErrorHandling`'e tek satır eşleme) |
| `report.unavailable` | 503 | `IFieldAccess`/kaynak hata verdi (kapalı-başarısız) |
| `export.too_many_rows` / `export.column_not_allowed` | 422 / 400 | Dışa aktarma sınırları (M9B kodları yeniden kullanılır) |
| `run.expired` | 410 | Çıktı dosyasının süresi doldu |
| `schedule.recipient_not_member` | 400 | Alıcı aktif üye değil |
Uyarılar (`200` gövdede `warnings[]`, hata değil): `report.currency_unconverted`, `report.currency_rate_missing`, `report.truncated`, `analytics.snapshot_truncated`.

## Olaylar, denetim, KVKK, günlük
- **Olaylar** (`Analytics.Contracts`): `ReportRunReady(TenantId, RunId, RecipientUserId, ReportName, ExpiresAt)` (M8A `reportReady` tüketir; ad kullanıcı metni olduğundan e-postaya **girmez**, yalnız uygulama içi bildirim başlığında). Başka olay yok; webhook izin listesi (M8B) değişmez.
- **Denetim** (`IAuditLogged`): `SavedReport`, `ReportFolder`, `Dashboard` (+ bileşenler panonun farkında), `Goal`, `ReportSchedule`; dışa aktarma `ReportExport`, indirme `ReportRun`; `GET /audit?entityType=…` (`org.audit.read` veya `crm.reports.read`; `IAuditEntityPermissions` `AnalyticsAuditEntities`). `definition` `SensitiveFields` → `***`.
- **KVKK:** anlık görüntü/önbellek kimlik+sayı; tablo/CSV yalnız liste sayfalarıyla aynı izin/kapsam/alan izni ve denetimle; çıktı dosyaları ≤ 7 gün + `AnalyticsRetentionService` (günlük süpürme; süresi dolan `content` silinir, satır `expired`); kiracı imhası otomatik; kişi silme (gelecek) yayılım gerektirmez (kopya yok); tanım filtre değerleri kullanıcı yapılandırmasıdır (kişisel veri girilmemesi arayüzde uyarılır). **Günlük/metrik:** rapor tanımı, filtre değeri, grup etiketi, hedef adı yazılmaz; yalnız `module`, `outcome`, süre, satır sayısı, `digest`.
- **Metrikler** (K20; `Sense.Crm` Meter, düşük kardinalite): `crm_analytics_runs_total{module,outcome}` (`outcome` ∈ `ok\|cached\|timeout\|busy\|budget\|error\|forbidden`), `crm_analytics_run_duration_seconds{module,outcome}`, `crm_analytics_active_runs` (gauge), `crm_analytics_snapshot_rows_total{module}`, `crm_analytics_schedule_runs_total{outcome}`. Runbook (yeni bölüm): `ConnectionStrings:Analytics`/havuz, okuma kopyası, zaman aşımı alarmı, anlık görüntü işi, çıktı temizliği, `Builder:Enabled` acil anahtarı; nginx: dışa aktarma için `proxy_buffering off`, `proxy_read_timeout 300s` (M9B ile aynı `location`).

## Performans: 1 M kayıtlı kiracı için sağlamlık sayıları
Tahmin hedefleridir; **Testcontainers kıyas testiyle (`Category=Perf`, gecelik, CI varsayılanı dışı) doğrulanır** ve sapmada sınırlar/indeksler düzeltilir. Varsayım: 1 M fırsat, 3 M aktivite, 1 M potansiyel, tek kiracı, `shared_buffers` ≥ 25 % RAM, indeksler yukarıdaki gibi.
| Sorgu | Plan | Sıcak / soğuk hedef (p95) |
|---|---|---|
| Hat: aşamaya göre `count/sum/weighted` (açık ≈ 300 k) | `(tenant, stage_id) INCLUDE(...)` yalnız indeks taraması (≈ 15–25 MB) | 0,1–0,3 sn / ≤ 1,5 sn |
| Kazanılan/kaybedilen 12 ay, ay kovası (≈ 200 k satır) | `(tenant, closed_at) INCLUDE` aralık + `date_trunc` + hash toplama (kova ifadesi indekslenmez, aralık daraltır) | 0,2–0,6 sn / ≤ 2 sn |
| Aktivite: kullanıcıya göre tamamlanan 12 ay (≈ 800 k) | `(tenant, completed_at) INCLUDE(assigned_user_id, type)` | 0,4–1,2 sn / ≤ 3 sn |
| Potansiyel kaynağı (≈ 250 k) | `(tenant, created_at) INCLUDE` | 0,15–0,5 sn / ≤ 1,5 sn |
| `countDistinct(accountId)` (1 M) | hash | 0,5–1,5 sn |
| `cf.<key>` boyutu (jsonb, indekssiz, 1 M) | kiracı önekli tarama | 0,8–2,5 sn (bu yüzden ≤ 3 `cf`, zaman aşımı 15 sn) |
| Tabular ilk sayfa (100 satır, `created_at DESC`) / derin sayfa (offset 9 900) | `(tenant, created_at DESC)` | < 50 ms / 0,1–0,3 sn (10 k tavanı) |
| Pano 8 bileşen (eşzamanlı 3) | 3 tur × ≈ 0,5 sn | 1,5–3 sn soğuk / < 300 ms önbellekli |
| Anlık görüntü yakalama (4 metrik, kiracı başına) | 4 gruplu tarama | 2–8 sn, günde bir |
| Hedef gerçekleşmesi (aylık) | `closed_at` aralığı | < 0,3 sn |
**Tavanlar:** zaman aşımı 15 sn ≈ tipik ağır sorgunun 10×'ü; sonuç ≤ 1000 grup × ≈ 200 B = 200 KB; toplu pano yanıtı ≤ 24 bileşen. **Eşik ve kaçış yolları:** > 2 M satırda p95 > 5 sn → (1) okuma kopyası (`ConnectionStrings:Analytics`), (2) o varlığın kaynağını okuma modeline çevirme (port değişmez), (3) planı sınırlama. **K3 (10 M kayıt):** aynı desenle çalışır ama ağır serilerin canlı sorgusu anlık görüntüye taşınır ve okuma kopyası zorunlu sayılır; ilk sürüm hedefi 1 M'dir. Statik hesap: 12 kova × 3 boyut × 5 ölçü = küçük; darboğaz satır taramasıdır, sonuç boyutu değil.

## Web (React, Mantine 9; `web/**`)
- **Rotalar:** `/app/reports` (kütüphane), `/app/reports/new` ve `/app/reports/:ref/edit` (oluşturucu), `/app/reports/:ref` (görüntüleyici), `/app/dashboards` + `/app/dashboards/:ref`, `/app/goals`, `/app/forecast`, `/app/analytics/runs` (çıktılarım). Menü: M9A **Raporlar** grubu (Rapor kütüphanesi, Panolar, Hedefler, Öngörü); görünürlük `crm.reports.read` (+ özellik/plan); eski `/app/reports?tab=…` bağlantıları yeni rotaya yönlendirilir. Sıcak dosyalar: `App.tsx`, `navigation.ts`, `i18n.ts`, `locales/*/{common,navigation}.json` (+ yeni `analytics.json`), `package.json` (**yeni bağımlılık yok**: `@mantine/charts` (Bar/Line/Area/Pie/Donut/FunnelChart), `SemiCircleProgress`, `@dnd-kit/sortable` M8D'den).
- **Kütüphane** (ekran görüntüsü 3): klasör ağacı (sistem/kiracı/kişisel), arama, kolonlar ad/açıklama/klasör/oluşturan/son erişim, favori yıldızı, "Rapor Oluştur", satır menüsü (aç, kopyala, taşı, paylaş, sil, zamanla, dışa aktar); kapı modülü kapalı raporlar soluk + ipucu; boş/yükleniyor/hata durumları.
- **Görüntüleyici:** tarih aralığı (M3 `report-range` önayarları + özel; URL `?range=&from=&to=`), sahip kapsamı (`Benim/Tümü`), huni/para birimi, `Tablo/Grafik` anahtarı (**her grafiğin tablo alternatifi + `aria-label` özet**; renk tek başına anlam taşımaz), "Kapsam: görebildiğiniz kayıtlar" şeridi (`scope=restricted`), uyarılar (kısmi/çevrilmemiş para birimi), kesme uyarısı, "hesaplandı: hh:mm" + Yenile, CSV indir, "Kopyasını oluştur", zamanla. Tabular sayfalı; kimlik varsa satır detay bağlantısı.
- **Oluşturucu** (sihirbaz, tek sayfa adımlar): (1) varlık (`GET /analytics/entities`) + birleştirmeler, (2) tür (özet/tablo), sütun veya boyut+ölçü (toplama seçici tipe göre), (3) filtre — **M9A/M9B `FilterBuilder`** bileşeni, `meta` alanlarıyla (noktalı birleştirilen alanlar dahil), (4) zaman alanı + aralık + kova, (5) sıralama/limit, (6) grafik (uyumsuzlar devre dışı + gerekçe), (7) **canlı önizleme** (`POST /analytics/run`, gecikmeli, iptal edilebilir), (8) kaydet (ad, klasör, görünürlük — M9B görünürlük seçicisi). Sunucu hataları `errors["definition.…"]` → ilgili adım/alan. Kaydedilmemiş değişiklik uyarısı.
- **Pano:** ızgara tuval (12 sütun, ön ayarlı boyutlar), düzenleme kipi (bileşen ekle: rapor seç / gömülü oluştur / hedef / kuyruk; sürükle-bırak + yukarı/aşağı + boyut menüsü), `DashboardWidget` çerçevesi (M9A ortak; yükleniyor iskeleti/hata+yeniden dene/boş/**yasak** ("Bu bileşeni görüntüleme izniniz yok" — başlıksız)/modül kapalı/tanım geçersiz), pano süzgeç çubuğu (tarih + Benim/Tümü), görünürlüğe duyarlı yoklama, kaydet (`expectedVersion` çakışmasında birleştirme iletisi), paylaş, varsayılan yap. `queue` bileşeni M9A kancalarını kullanır.
- **Hedefler:** benim hedefim ölçerleri, takım/rol tablosu, liderlik, oluştur/düzenle diyaloğu (metrik, konu, dönem seçici, hedef, para birimi), "12 aya çoğalt". **Öngörü:** dönem seçici, senaryo aralığı (taahhüt/beklenen/en iyi) çubukları, aşama tablosu, **gecikmiş kapanış** ve **tarihsiz** uyarı kartları, hız ayrı gösterim, eğilim grafiği (bant + "istatistiksel eğilimdir, garanti değildir" notu + R²), doğruluk tablosu; `insufficientData` durumunda açıklayıcı boş durum.
- TR/EN metinler (`analytics` ad alanı; sistem rapor/pano/klasör adları i18n anahtarı `analytics:system.<key>.name`); büyük sayı/para biçimi mevcut `report-format`; erişilebilirlik: klavye ile tüm düzen işlemleri, ızgara `role="list"`, grafiklerde odaklanılabilir tablo eşdeğeri, kontrast Mantine tema renkleriyle.
- **M3 sayfası:** mevcut `reports.tsx` sekmeleri yerine kütüphane; `components/reports/*` grafik bileşenleri yeniden kullanılır (`report-tabs`, `marketing-report-tab`, `commerce-report`, `service-report` → sistem rapor görüntüleyicisine taşınır, testler güncellenir).

## Uygulama sırası (her adım tek başına birleşebilir; kesme çizgisi D23)
1. **Sözleşme + motor çekirdeği:** `Shared.Contracts.Analytics`, `ReportSourceBase`, doğrulayıcı/derleyici, `ForecastMath`, zaman/doldurma/para saf işlevleri; **EF dinamik GroupBy spike'ı**; M9B uzantıları (kayıt, noktalı anahtar, `CsvSanitizer` paylaşımı) — saf birim testli.
2. **Sales + Activities kaynakları** (deal/lead/account/contact/activity), indeks migration'ları, eski depolara `RecordScope`, hazır rapor paritesi (`sales.*`, `leads.*`, `activities.*`).
3. **Analytics modülü + kütüphane + kayıtlı raporlar + klasör + favori + dışa aktarma** (HTTP + web kütüphane/görüntüleyici/oluşturucu).
4. **Service, Commerce (M9C), Marketing kaynakları** + kalan hazır raporlar + bileşik `campaign-roi` + Raporlar sayfası geçişi.
5. **Pano** (+ sistem panoları, toplu veri ucu, web tuval).
6. **Hedefler** + liderlik + ölçer bileşenleri.
7. **Öngörü + anlık görüntü yakalama** (+ doğruluk arayüzü).
8. **Zamanlama + çıktılar** (M8A bildirimi ile).
Merge sırası önerisi: M9B → **M9F 1–2** → M9H (kapsam/alan izni bağı) → M9F 3–8; M9J gelince yalnız `IReportTimeSettings`/`IReportCurrencyConverter` uygulamaları takılır (M9F kodu değişmez).

## Testler

**Altın veri (doğruluk):**
- **Sabit tohum veri seti** (`AnalyticsGoldenData`: 3 kiracı, 2 saat dilimi (`Europe/Istanbul`, `Pacific/Kiritimati` UTC+14), 2 para birimi, silinmiş/yumuşak silinmiş satırlar, boş dönemler, yıl sınırı ISO haftası `2025-W53/2026-W01`, ay sonu 23:30 yerel kapanış, artık gün) ve **elle hesaplanmış beklenen sonuçlar** (`golden/*.json`, kod incelemesinden geçer). Her hazır rapor ve her oluşturucu (varlık × ölçü × kova) kombinasyonu için.
- **Oracle testi:** aynı tohum (seed'li RNG, ≈ 500 kayıt/kiracı) için rastgele üretilen geçerli tanımlar motor ile **bağımsız bellek içi LINQ** hesabında karşılaştırılır (toplamlar, kova sayıları, doldurma, kapsam, oran boşluğu).
- **Parite:** her eski uç (`funnel`, `won-lost`, `leads-by-source`, `by-owner`, `activities/by-user`, Commerce/Service/Marketing özet) = ilgili hazır rapor (aynı sayılar, aynı yuvarlama).
- **Formül vektörleri:** ağırlıklı hat (`gecikmiş`/`tarihsiz` kovaları, boş tutar, toplam-sonra-yuvarla farkı), hız (`elapsedFraction` sınırları 0, 0,15, 1), OLS (`y = 2x + 1` tam, sabit seri `r2 = null`, `n < 6`, dışlayıcı, negatif tahmin kırpma), hedef durum eşikleri (0,9/1,1 sınırları), yaşlandırma sınırları (`dueDate = bugün`, +1, +30, +31, +90, +91), `winRate` payda 0, `avgCycleDays` yalnız kazanılanlar, para yuvarlama `AwayFromZero`, karışık para birimi hiçbir yolda toplanmaz, kur eksik → uyarı + ayrı satır.
- **Zaman:** kiracı saat dilimi kova sınırları, haftalık kova = ISO (pazartesi), `weekStart` Pazar ayarında kaydırma (M9J varsayılan dışı), mali yıl kovası, yaz saati geçişi (`Europe/Berlin`) günlük/haftalık kova, boş dönem doldurma (tek boyut + seri boyutu), kova sayısı > 400 reddi.

**Kiracı ve kapsam sızıntısı (zorunlu; adversarial):**
- İki kiracı (A, B) **aynı kimlik/ad/yapı** ile tohumlanır: her uç (`run`, `export`, pano `data`, hedef gerçekleşmesi/liderlik, öngörü, anlık görüntü, zamanlama Worker'ı, çıktı indirme) için A oturumu **hiçbir B satırını/toplamını/etiketini** görmez; B kimlikleriyle `404 not_found`; pano bileşeni B raporuna bağlanamaz; Worker `BeginScope` dışında `IgnoreQueryFilters` yok (envanter testi değişmez).
- **Kaynağı doğrudan çağırma:** `IReportSource.ExecuteAsync` yanlış/boş `ITenantContext` ile → hata (varsayılan kapalı); kapsam `OwnerUserIds = ∅` → 0 satır; `ReportSourceBase` dışı `DbSet` erişimi mimari testle reddedilir.
- **Kayıt kapsamı (M9H sahte sağlayıcısı):** kısıtlı kullanıcı toplamı = kendi kayıtları toplamı; grup/kova/`countDistinct`/`totals` yalnız görünenler; liderlik yalnız kapsam içi; kapsam dışı hedef `404`; `ownerScope=all` kapsamı **genişletmez**; kiracı toplamı fark saldırısı (toplam − kendi) yapılamaz; anlık görüntü sahip filtresi; eski (M3) uçlar kapsamı uygular.
- **Alan izni (M9H sahte `IFieldAccess`):** gizli alan boyut/ölçü/filtre/sıralama/sütun/birleştirilen `account.<alan>`/`cf.*` olarak `report.unknown_field` (yok ile aynı ileti/kod); kayıtlı paylaşılan rapor izleyicide `422 report.definition_invalid` (kısmi veri yok); pano bileşeni `access: definitionInvalid/forbidden` ve `title` yok; zamanlanmış çıktı alıcının alan izniyle; `IFieldAccess` istisnası → `503`, tüm alanlar **açılmaz**; hassas `cf` hiçbir yerde kullanılamaz.
- **Önbellek:** iki kiracı aynı `digest`; `All` kapsam kullanıcısı ve kısıtlı kullanıcı aynı TTL içinde farklı sonuç; alan izni farkı; önbellekte **etiket/PII yok** (anahtar/değer denetimi); tabular hiç önbelleğe girmez.
- **Etiket sızıntısı:** ilişkili tür okuma izni yoksa `label: null`, kimlik/ad yok.

**Enjeksiyon ve kötüye kullanım:**
- Tanım alan/işleç/takma ad/kova/sıralama alanlarında `"amount); DROP TABLE"`, EF üye zinciri (`Deal.Account.Owner`), `__proto__`, Unicode homoglif/sıfır genişlikli, çok uzun ad, yinelenen anahtar, JSON derinlik/boyut bombası, `bucketEdges` taşması/sıralı olmayan → `400`, sunucu hatası yok; `sort` seçili olmayan alana → `400`.
- **CSV enjeksiyonu:** `lostReason`/`cf` seçenek etiketi/kampanya adı/rapor adı `=cmd|'/C calc'!A0`, `+SUM(...)`, `@`, TAB ile başlayan değerler dışa aktarımda `'` önekli; başlık hücreleri dahil; sayı/tarih hücreleri öneksiz.
- **Kaynak tüketimi:** eşzamanlı 50 çalıştırma → `≤ 3/kiracı` çalışır, kalanı `429 report.busy`; test yapılandırmasıyla 1 sn zaman aşımı + iri tohum → `503 report.timeout`, bağlantı/transaction sızıntısı yok (bir sonraki çalıştırma sağlam, `pg_stat_activity` boş), istemci kopunca sorgu iptal; **havuz yalıtımı:** analytics havuzu 2'ye çekilip doyurulurken OLTP uçları (liste, giriş) yanıt verir; günlük bütçe aşımı `402` ve önbellek isabeti bütçeden düşmez; pano 24 bileşen → en çok 3 eşzamanlı (ölçüm); `MaxGroups` kesme bayrağı ve `totals` tüm grupları kapsar; `day` kovası + 10 yıl → `report.range_too_large`; iki boyutlu çarpım tavanı; ad-hoc `run` özellik bayrağı kapalıyken `403`.
- **Zamanlama:** iki Worker aynı vadeyi alır → tek çalıştırma (`SKIP LOCKED`); alıcı başına ayrı kimlik/kapsam (dar alıcı dar çıktı alır); alıcı izni kaybı `skipped`; sahip pasif `paused`; ardışık 5 hata `paused`; kiracı salt okunur → `skipped`, yazma yok; yaz saati boşluğu/belirsiz saatte vade; 7 gün sonra süpürme ve `410`; **başkasının çıktısı `404`**, sahip yalnız meta veri; indirmede izin yeniden denetimi; `schedule_due` küresel tablosu kiracı imhasında temizlenir.
- **Plan/özellik:** starter'da ad-hoc/builder/dashboards/scheduling `403 plan.feature_disabled`, hazır raporlar/hedef/öngörü/export açık; kapı modülü kapalı varlık (`commerce`) raporları `403 plan.module_disabled` ve kütüphanede `available=false`; plan düşürmede mevcut raporlar çalışır; `maxSavedReports` sınırı; salt okunur kiracıda run/export/öngörü çalışır, CRUD `403 tenant.suspended`.
- **Kütüphane/pano:** görünürlük (`roles` kesişimi, silinen rol), paylaşım kuralı `dashboard.report_not_shared`, `report.in_use`, `expectedVersion` çakışması (iki sekme), yasak bileşen başlığı yanıttan çıkarılmış, pano toplu ucunda bir bileşen hata → diğerleri döner, sistem pano/rapor salt okunur, `copy` yetkileri.
- **Mimari testler:** Analytics referans kuralı; her `IReportSource` `ReportSourceBase` türevi; her Analytics handler'ı `IReportAccess` ile başlar; yeni `IgnoreQueryFilters` yok (envanter **+0**); ham SQL envanteri (**+2**: `schedule_due` alma, `schedule_due` imha; spike yedeği devreye girerse +1); `RequestAuthorizationTests`/`Create*Command` kuralı; `TenantQueryFilterConventionTests.GlobalEntities` `schedule_due`; `[TenantStatusExempt]` onaylı listesi yalnız `ToggleFavoriteCommand` kadar genişler.
- **Kıyas (gecelik):** yukarıdaki performans tablosu 1 M tohumla; p95 hedefleri; `EXPLAIN` planında kapsayıcı indeks kullanımı (seq scan uyarısı).
- **Web:** kütüphane filtre/arama/favori/klasör; oluşturucu adımları + sunucu hata eşleme + grafik uyumluluğu + önizleme iptali; görüntüleyici URL↔aralık, `Tablo/Grafik` anahtarı, `scope=restricted` şeridi, uyarılar, kesme; pano düzenleme (yukarı/aşağı/boyut), yasak/modül kapalı/geçersiz bileşen durumları, görünürlüğe duyarlı yoklama (sekme gizli → istek yok), `expectedVersion` çakışması; hedef ölçer ve liderlik; öngörü `insufficientData`, gecikmiş/tarihsiz kartlar; eski `?tab=` yönlendirmesi; TR/EN anahtar tamlığı; a11y (tablo eşdeğeri, klavye).

## Notlar (dosya kapsamı ve birleşme)
- **Backend:** `src/Modules/Analytics/**`; `src/Shared/Sense.Crm.Shared.Contracts/Analytics/**`, `Shared.Infrastructure/Analytics/**`; her kaynak modülde `*ReportSource.cs`, `*ReportCatalog.cs`, `*ReportLabelProvider.cs`, `*SnapshotMetricSource.cs`, `AddReportIndexes` migration; Sales/Commerce/Service/Marketing/Activities eski rapor depolarında `RecordScope`; Customization `AddReportableFlag`; Platform (`PlanLimits`/`EntitlementSnapshot`/`Features`/DTO/`PlatformOptions`); Identity (`SystemRoleDefinitions`); Api/Migrator/Worker kayıtları; test projesi `Sense.Crm.Modules.Analytics.Tests`; Respawn `analytics`; runbook bölümü; `docs/architecture/backend.md` (M3 §11'e "eski uçlar donuk" notu + yeni bölüm).
- **`ModelSnapshot` çakışması:** Sales/Commerce/Service/Marketing/Activities'e M9A–M9E ve M8D de migration ekler; **sonra birleşen kart kendi migration + snapshot değişikliğini geri alıp birleşmiş dal üzerinde yeniden üretir**; kısmi/kapsayıcı indeks ifadeleri elle korunur.
- **Web:** `web/src/pages/analytics/**`, `web/src/components/analytics/**`, `web/src/hooks/use-analytics-*.ts`, `web/src/config/widget-registry.ts` (M9A `home-widgets.ts` ile birleşir), `web/public/locales/*/analytics.json`.
- Türkçe/İngilizce `SharedResource.resx`: `report.*`, `dashboard.*`, `goal.*`, `schedule.*`, `folder.*`, `run.*` anahtarları + `permission.*` + `field.analytics.*`.

## Kapsam dışı
Serbest SQL/formül/ifade editörü; kullanıcı tanımlı hesaplanmış alan/ölçü; 1:N birleştirme ve çapraz modül birleştirme (bileşik raporlar yalnız kodda); makine öğrenmesi/skorlama/otomatik öneri; kiracılar arası karşılaştırma/benchmark; grup konsolidasyonu (K4); kohort, anomali, bölge, çeyrek daire bileşenleri (analiz §3); XLSX/PDF/yazdırma; dış e-posta alıcıları ve e-postaya dosya eki; gerçek zamanlı itme (WebSocket); pano gömme Ana Sayfa'ya; serbest yerleşim (piksel); mobil düzen; çok adımlı/koşullu pano süzgeçleri; işlem tarihli kur (yalnız tek kur, M9J sonrası); kalem düzeyi raporlar (Dalga 2); tekrarlayan/otomatik devreden hedefler; ekip hiyerarşisi hedefi (M9H); atfetme modelleri (çok dokunuşlu); rapor abonelikleri (başkasının raporuna abone); rapor yorumları/anotasyon; okuma modeli (olay beslemeli raporlama şeması); geriye dönük anlık görüntü doldurma.

## Kartlar arası takip ve açık işler
- **M9B (küçük eklemeler, M9F 1. adımında):** `ListFieldDefinition`'a isteğe bağlı `Report: ReportFieldMeta`; alan anahtarında `takma.alan` (noktalı) izni; `ListEntityRegistration.Capabilities.ListPage=false` ile **rapor-yalnız varlık** kaydı (`invoice`); `CsvSanitizer`'ın `Shared.Infrastructure/Export`'ta bulunması (Lists'e gömülüyse taşınır); görünürlük seçicisi ve `FilterBuilder` web bileşenlerinin dışa açık kullanılabilirliği; `IRecordScopeProvider` sözleşmesinin **değişmemesi**.
- **M9H:** `IRecordScopeProvider` bağı; `IFieldAccess` (bu belgedeki minimal arayüz geçici, ilk merge eden sahiplenir); "takım" hiyerarşisi gelince hedef konusu `hierarchy` eklenir; M9H testleri M9F kaynaklarını **kapsam sızıntısı** için tarar (sahte sağlayıcı zaten burada).
- **M9J:** `IReportTimeSettings` (hafta başı Pazar/Pazartesi, mali yıl, taban para birimi), `IReportCurrencyConverter` (tek kur haritası), çalışma günü (hedef `elapsedFraction` iş gününe geçebilir).
- **M9A:** `deals.last_activity_at`; `home-widgets.ts` → paylaşılan `widget-registry.ts`; Raporlar menü grubu; M9A DSL'inin M9B v1'e çevrilmesi (M9F yalnız M9B'yi tanır).
- **M9C:** fatura alanları/etkin durum/`balanceAmount` ve `crm.invoices.read` adlarının bu belgedeki ile uyumu; `AddReportIndexes` M9C indeksleriyle birleştirilir.
- **M9D:** gecikme tanımı; `activities.by-user` parite testi M9D sonrası güncellenir.
- **M9E:** `deal.campaignId/leadSource/type`, `account.accountType/annualRevenue` alan adları; `expectedRevenue` türetilmiştir (rapor `weightedAmount` ile aynı hesap, yuvarlama farkı belgelidir).
- **M8A:** `reportReady` bildirim türü (uygulama içi + e-posta yalnız bağlantı), `features` mekaniği, `Worker:Roles`; **M8D:** `is_reportable`, seçenek etiketi çözümü; **M7/Platform:** yeni limit/özellik anahtarları, plan doğrulaması, kullanım satırları.
- **Sales (açık iş):** kazanan sahibi kalıcılığı (`deals.closed_owner_user_id`; hedef kredisinin sahip değişiminden etkilenmemesi) — küçük migration + `MoveToStage(won)`'da yazım; kart sonrası karar.
- **Açık iş:** işlem tarihli kur; çok dokunuşlu kampanya atfı; kohort/anomali; okuma modeli eşik denetimi; eğilim için mevsimsellik; pano Ana Sayfa gömme; kalem düzeyi rapor varlıkları; rapor sonuçlarına yorum; PostgreSQL RLS (K2) ile ikinci savunma hattı (analytics bağlantısı da kapsar).
