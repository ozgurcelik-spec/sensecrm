# Milestone 8D — Özel alanlar: kiracı bazlı alan tanımları, `custom jsonb` değer depolama, tüm ana varlıklarda entegrasyon (plan + HTTP kontratı)

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Kararlar: [kararlar.md](../architecture/kararlar.md) (K2, K3, K5, K7, K10, K14, K18) · Önceki: [M2 kontrat](m2-api-kontrat.md), [M6A](m6a-ticaret.md), [M6B](m6b-servis.md), [M6C](m6c-pazarlama.md), [M7](m7-saas-hazirlik.md), güvenlik: [hardening-report.md](../security/hardening-report.md) · Mimari resim: sol sütun "Özel Modüller" + "CRM Arayüzü: role-based, kişiselleştirilebilir UI" · Pano kartı: `C-M8D` (dal `m8/custom-fields`). Biçim kuralları M2–M7 ile aynı: taban yol `/api/v1`, JSON camelCase, enum'lar camelCase string, `null` alanlar yazılmaz, hatalar ProblemDetails + `code` (+ `args`, doğrulamada `errors`), sayfalı liste `{ items, page, pageSize, totalCount }` (`page` 1'den, `pageSize` varsayılan 25 / en çok 100), `sort` = `alan` veya `-alan`, `q` = `ILIKE` + kaçışlı parametre. Gün alanları `YYYY-MM-DD`, zaman damgaları ISO 8601 UTC.

**Çıktı:** Kiracı yöneticisi (`org.customization.manage`) Ayarlar → "Özel alanlar"da sekmelerle şu varlık türleri için alan tanımlar: firma, kişi, potansiyel müşteri, fırsat, talep, teklif, sipariş, kampanya. Alanlar 11 tiptedir (metin, uzun metin, tam sayı, ondalık, tarih, evet/hayır, tekli seçim, çoklu seçim, URL, e-posta, telefon); zorunlu/varsayılan/yardım metni/doğrulama kuralı/hassas işareti/bölüm ve sıra ayarlanır; seçenekler kararlı anahtarlıdır ve silinmez, arşivlenir. Tanımlı alanlar ilgili varlığın oluşturma/düzenleme diyaloğunda ve detay bilgi panelinde otomatik görünür; API'de `customFields: { anahtar: değer }` olarak okunur/yazılır; sınırlı sayıda alan liste sütunu, süzgeç ve sıralama olur. Değerler varlığın kendi satırında (`custom jsonb`) durur → aynı transaction, aynı denetim kaydı, aynı KVKK imhası. Alan sayısı M7 plan limitiyle (`maxCustomFieldsPerEntity`) sınırlanır.

## Kararların özeti

| # | Konu | Karar | Gerekçe |
|---|---|---|---|
| D1 | Modül | Yeni modül **`Sense.Crm.Modules.Customization`** (şema `customization`, `CustomizationDbContext`, migration `InitialCustomization`). **Yalnız alan tanımlarını, bölüm yerleşimini ve şema sürümünü sahiplenir; değerleri sahiplenmez.** Yaprak modüldür: `Shared.*` dışında hiçbir modüle bağlı değildir, hiçbir varlık modülü `Customization.*`'a referans vermez. | Tanım yönetimi (yönetici işi, nadir yazma) ile değer yazımı (her kayıt kaydı) farklı sıklık/yetki/tutarlılık sınırlarıdır. Modül sınırı (K5) korunur. |
| D2 | **Değer depolama** | **`custom jsonb NOT NULL DEFAULT '{}'` kolonu, her kapsamlı varlığın kendi tablosunda (varlık modülü sahibi).** Doğrulama/varsayılan/zorunluluk kuralları `Shared.Contracts.CustomFields.ICustomFieldValidator` portuyla (Customization uygular) varlık handler'ından çağrılır. EAV (Customization'da değer tablosu) **reddedildi** (aşağıda karşılaştırma). | Aynı transaction + aynı denetim + listede ek sorgu yok + süzme/sıralama modülün kendi sorgusunda + KVKK imhası bedava. |
| D3 | Ortak port yeri | Portlar/tipler `Shared.Contracts.CustomFields` (ve `Shared.Kernel`'de `CustomFieldValues`) altında; varsayılan (boş) uygulamalar `AddCrmCore`'da `TryAdd` (M7 `ITenantEntitlements` kalıbı). | Varlık modülleri yalnız `Shared.*`'a bağlı kalır; Customization yüklü olmayan host/birim testlerinde davranış değişmez. |
| D4 | Tip seti | `text`, `longText`, `number` (tam sayı), `decimal` (ölçekli), `date`, `boolean`, `singleSelect`, `multiSelect`, `url`, `email`, `phone`. **Yok:** `currency` (ondalık + `unit` gösterim ipucu karşılar), `datetime` (saat dilimi anlamı; `date` CRM ihtiyacını karşılar), lookup/formül. | YAGNI; her tipin süzme/sıralama/doğrulama/denetim yükü vardır. |
| D5 | Anahtarlar | Alan `key` (`^[a-z][a-z0-9_]{0,39}$`) kiracı+varlık başına benzersiz (arşivlenmişler dahil), **değişmez**; seçenek anahtarı (`^[a-z0-9][a-z0-9_]{0,39}$`) alan içinde benzersiz, değişmez. Etiketler (`labels: { tr, en }`) her zaman düzenlenebilir. **`dataType` ve `isSensitive` oluşturmadan sonra değişmez** (veri var mı diye başka modülün tablosunu taramak yerine daha katı ve basit kural). Alan silinmez, **arşivlenir**. | Senseik'te seçenekler düz metindi (yeniden adlandırma değerleri yetim bırakırdı) → kararlı anahtar. Tip/hassaslık değişimi geçmiş değerleri ve denetim geçmişini sessizce yanlış yorumlatır. |
| D6 | Yazma semantiği | `customFields` **yama (merge)**'dır: yalnız gönderilen anahtarlar değişir, `null` temizler, hiç gönderilmezse dokunulmaz. `PUT`'un "tam değiştirme" kuralı özel alanlara **uygulanmaz** (arşivli alan değerleri, eski istemciler ve yeni eklenen alanlar korunur). Varsayılan yalnız **oluşturmada** uygulanır; zorunluluk oluşturmada tüm etkin alanlarda, güncellemede yalnız gönderilen anahtarlarda denetlenir; sistem kaynaklı oluşturma yolları (lead dönüştürme, teklif→sipariş kopyası) zorunluluğu **denetlemez**. | Geriye uyum: `customFields` bilmeyen mevcut istemciler/entegrasyonlar güncellemede bozulmaz; zorunluluk bir girdi formu kuralıdır, geçmişe dönük veri değişmezi değildir. |
| D7 | Süzme/sıralama | `cf.<anahtar>[.<op>]` parametreleri, **beyaz liste + tip başına işleç kümesi**; süzülebilir alan sayısı ≤ 10, liste sütunu/sıralanabilir alan ≤ 5, istek başına ≤ 3 `cf.` süzgeci + ≤ 1 `cf.` sıralaması. İndeks: tablo başına tek **GIN `jsonb_path_ops`** (eşitlik/`in`/çoklu seçim içerme); aralık ve sıralama indekssizdir (sınırlı alan sayısı + kiracı önekli tarama). **Dinamik DDL (alan başına indeks) yok.** | Bin kiracı × alan başına indeks felaketi; GIN tek sefer, tanımdan bağımsız. Sınırlar maliyeti bağlar. |
| D8 | Denetim | `AuditLogInterceptor` `ICustomFieldsHolder` alanını **anahtar anahtar** düzleştirir: `customFields.<anahtar>: { old, new }`; `isSensitive` alanların değeri `***`. Hassaslık kaynağı `ICustomFieldSchemas` (arşivlenmişler dahil; bilinmeyen anahtar → maskelenir, **kapalı-başarısız**). | Mevcut mekanizma (aynı transaction, `audit_log_entries`, `SensitiveFields`) genişler; unutulamaz. |
| D9 | Plan | `limits.maxCustomFieldsPerEntity` (etkin alan sayısı / varlık türü; `null`/yok = sınırsız, `0` = alan açılamaz). Ek teknik tavan (plandan bağımsız): varlık türü başına en çok 200 tanım (arşivliler dahil). Customization çekirdek modüldür, **kapı modülü değildir** (`GatedModules` değişmez); varlık türünün ait olduğu modül kapalıysa o türün tanım/şema uçları `403 plan.module_disabled`. | M7 zorlama mekanizması yeniden kullanılır. |
| D10 | Yerleşim | Basit **bölümler** (kiracı+varlık başına ≤ 10) + alan sırası; tek uç `PUT …/layout` (tümünü değiştir, `expectedVersion` ile). Sürükle-bırak yerleşim oluşturucu, kolon/satır düzeni, koşullu görünürlük **yok**. | İhtiyacın %90'ı "gruplu, sıralı form". |
| D11 | Alan düzeyi izin | **Kapsam dışı.** Erişim kaydın izniyle aynıdır (`crm.<kaynak>.read/write`); tek ek koruma `isSensitive` (denetim maskesi). Tüm okuma/yazma yolları iki porttan (`ICustomFieldSchemas`, `ICustomFieldValidator`) geçtiği için ileride alan başına `readPermission/writePermission` tek noktaya eklenebilir. | Kısmi görünürlük süzgeç/sıralama üzerinden değer sızdırır (oracle) ve her okuma yolunda redaksiyon ister; K7'nin kayıt sahipliği görünürlüğü zaten sonraki aşamada. E-posta/telefon da bugün yalnız denetimde maskeli (M2). |
| D12 | Rapor/CSV | **Kapsam dışı.** Sales/Commerce/Service/Marketing rapor sorgularına özel alan boyutu eklenmez. `custom jsonb` kolonu ileride varlık modülünün kendi rapor sorgusunda `custom->>'anahtar'` ile gruplamaya uygundur (EAV'da imkânsız olurdu). | Rapor tasarımı ayrı kart; alan ekranlarını geciktirmesin. |
| D13 | Önbellek | Şema (kiracı+varlık) HybridCache'te 60 sn; her tanım komutu aynı süreçte anında geçersiz kılar; varlık türü başına `version` (ETag). Çok kopyada diğer kopyalar ≤ 60 sn + L2 bayat (runbook §9.1'e satır). | Her kayıt yazımı ve okuması şemaya bakar; DB'ye gitmemeli. |
| D14 | Regex | Metin alanında isteğe bağlı `pattern` **yalnız** .NET `RegexOptions.NonBacktracking` (doğrusal zaman) ile; ≤ 200 karakter, girdi ≤ 500 karakter, tam eşleşme (`^(?:…)$`), ek güvence olarak 100 ms eşleşme zaman aşımı; NonBacktracking'in desteklemediği yapılar (geri başvuru, lookaround, atomik grup, koşullu) tanım anında reddedilir. İstemci `pattern` **çalıştırmaz** (tarayıcıda ReDoS yüzeyi açmamak için); hata sunucudan alan altında gösterilir. | ReDoS'u yapısal olarak yok eder (kısıtlı regex + doğrusal motor). |

### Senseik'ten alınanlar ve bilerek değiştirilenler
Alınan: `key` deseni ve değişmezliği, `dataType` değişmezliği, "silme yok, pasifleştir", tipe göre değer zorlama (coercion) fikri, `isRequired`/`isSensitive`, seçenek sayısı üst sınırı, yönetici tanım sayfası + tipe göre uyarlanan diyalog, tipe göre girdi bileşeni seçimi (`CustomFieldValueInput`), bölümlere göre gruplu gösterim. **Değiştirilen:** değerler string değil **tipli JSON** (çoklu seçim JSON-içinde-string değil dizi); seçenekler düz metin değil **kararlı anahtar + tr/en etiket + arşiv**; EAV değil `jsonb` (senseik tek varlık, tek modül; burada 8 varlık/4 modül); etiketler çok dilli; ayrı "değer düzenleme diyaloğu" yerine **mevcut oluşturma/düzenleme diyaloğuna gömülü form**. İK'ya özgü hiçbir şey (self-servis düzenleme, onay, çalışan bölümleri, `employee.*` izinleri) alınmadı.

## Kapsam: desteklenen varlık türleri

Kod düzeyinde açık kayıt listesidir (`CustomEntityTypes`); bilinmeyen tür `404 customization.entity_type_unknown`. Her varlık modülü kendi türünü `CustomEntityRegistration` ile kaydeder (`IAuditEntityPermissions` kalıbı; Customization hangi modüllerin yüklü olduğunu buradan öğrenir → Customization hiçbir varlık modülüne bağlanmaz).

| `entityType` | Modül (plan bayrağı) | Tablo | Okuma / yazma izni | Denetim adı | Gerekçe |
|---|---|---|---|---|---|
| `account` | sales (çekirdek) | `sales.accounts` | `crm.accounts.read/write` | `Account` | Zoho'da en çok özelleştirilen. |
| `contact` | sales | `sales.contacts` | `crm.contacts.read/write` | `Contact` | ↑ |
| `lead` | sales | `sales.leads` | `crm.leads.read/write` | `Lead` | ↑ (kaynak/segment alanları). |
| `deal` | sales | `sales.deals` | `crm.deals.read/write` | `Deal` | ↑ |
| `case` | service | `service.cases` | `crm.cases.read/write` | `Case` | Destek sınıflandırma alanları. |
| `quote` | commerce | `commerce.quotes` | `crm.quotes.read/write` | `Quote` | Başlık düzeyi (teslim şartı vb.); kalemlere özel alan **yok**. |
| `order` | commerce | `commerce.sales_orders` | `crm.orders.read/write` | `SalesOrder` | Teklif→sipariş dönüşümünde aynı anahtar+tipteki değerler kopyalanır. |
| `campaign` | marketing | `marketing.campaigns` | `crm.campaigns.read/write` | `Campaign` | Kampanyaya özgü meta. |

**Bilerek dışarıda:** `product` (katalog; sonra kayıt + kolon eklemek yeter), `activity` (çok biçimli: görev/arama/toplantı), `pipeline`/aşama (yapılandırma), kampanya üyeleri, talep yorumları, teklif/sipariş kalemleri, kullanıcı/rol/organizasyon (Identity). Her ek tür = tek `CustomEntityRegistration` + tek kolon/migration + DTO/handler/liste/web bağlaması; kart kesilmek zorunda kalırsa **Dalga 2** (quote, order, campaign) sözleşme değişmeden sonraya kalabilir (kayıtsız tür `404 customization.entity_type_unknown` döner).

## Depolama kararı: `custom jsonb` (varlık tablosunda) ↔ EAV (Customization'da)

| Ölçüt | A) `custom jsonb` varlık tablosunda | B) EAV `customization.field_values` |
|---|---|---|
| Yazma tutarlılığı | Varlıkla **tek `SaveChanges`/transaction** (kayıt + değerler birlikte; xmin çakışması dahil) | İki DbContext = iki transaction; ya kayıp/yetim değer riski ya UoW hilesi |
| Denetim (K14) | Aynı agregat, aynı interceptor, aynı transaction; `GET /audit?entityType=…&entityId=…` özel alan farkını da gösterir | Değer varlık agregatının parçası değil → ayrı denetim tablosu/varlık türü; kayıt bazlı denetim ikiye bölünür |
| Liste/detay okuma | Değer satırla gelir, **ek sorgu yok** | Sayfa başına ek toplu sorgu (port), her modülde eşleştirme, N+1 riski |
| Süzme | Modülün kendi sorgusunda EF koşulu; GIN ile hızlanır | Modül sınırı yüzünden yalnız "eşleşen id kümesi" döndürülebilir → sayfalama/sıralama/`AND` bozulur |
| Sıralama | `ORDER BY custom->>'k'` aynı sorguda | Çapraz modül join yok → pratikte imkânsız |
| Rapor (sonraki kart) | Modül rapor sorgusunda `custom->>'k'` ile gruplama | Çapraz şema join yasak (K5) → raporlama Customization'a taşınır |
| İndeks | Tablo başına 1 GIN (`jsonb_path_ops`); anahtar sayısından bağımsız | Tip başına `(tenant_id, field_id, değer)` btree'ler (sağlam), ama süzme id-kümesi sorununu çözmez |
| KVKK imhası (M7) | Değer satırla birlikte gider (`TenantDataEraser<TContext>` tüm `ITenantEntity` tablolarını zaten siler); ek adım yok | Customization'ın eraser'ı yeterli olur ama yumuşak silinen varlık değerleri ayrı temizlik ister |
| Migration | Her varlık modülünde bir `AddCustomFields` (sabit maliyet; `ADD COLUMN … DEFAULT '{}'` PG ≥ 11'de meta-veri işlemi, yeniden yazım yok) | Varlık tablolarına dokunmaz; yeni tür = sıfır migration (tek üstünlüğü) |
| Modül bağı | Varlık → yalnız `Shared.Contracts` portları | Varlık → yine bir port (değer okuma/yazma) + eşleştirme kodu |
| Zayıflıklar (A) | Tüm `custom` değeri her güncellemede yeniden yazılır (16 KiB tavanla sınırlı); seçenek anahtarı/tip bütünlüğü DB'de değil uygulamada; arşivli/yetim anahtarlar satırda kalır; alan başına indeks otomatik yok | — |

**Karar: A.** Belirleyici üç neden: (1) **atomiklik + denetim** — özel alan değeri kaydın verisidir; ayrı transaction kayıt-bazlı geçmişi ve KVKK maskesini bozar; (2) **süzme/sıralama/sayfalama** modül sınırı içinde çözülebilen tek seçenek; (3) **imha ve okuma maliyeti sıfır ek iş**. A'nın zayıflıkları bilinçli olarak sınırlandı (yük tavanı, doğrulama portu, GIN + kısıtlı süzme, tip/anahtar değişmezliği). B'nin tek avantajı (migration'sız yeni tür) ~8 türlük sabit kapsamda önemsizdir.

## Modül ve bağımlılık düzeni

```
Sense.Crm.Modules.Customization.{Domain, Application, Contracts, Infrastructure, Api}    (./build/new-module.ps1 -Name Customization; şema customization)
```
- Bağımlılıklar: `Customization.*` → `Shared.*` yalnız. Varlık modülleri (Sales, Service, Commerce, Marketing) `Customization.*`'a **referans veremez**; yalnız `Shared.Contracts.CustomFields` + `Shared.Kernel` + `Shared.Infrastructure`'ı kullanır. Mimari test (yeni kural, `PlatformArchitectureTests` yanına): `Sense.Crm.Modules.Customization.*`'ı yalnız `Sense.Crm.Api`, `Sense.Crm.Worker`, `Sense.Crm.Migrator` ve testler referans eder.
- `Customization.Contracts`: `CustomizationPermissions`, `CustomizationAuditEntities`, integration event `CustomFieldDefinitionChanged`. Portlar burada **değil** `Shared.Contracts`'tadır (D3).
- Modül kaydı: `ModuleCatalog.cs` (`new CustomizationModule()`), Migrator (`CustomizationDbContext` + `InitialCustomization`), Worker (`AddModuleDbContext<CustomizationDbContext>` + `AddModuleHandlers` + `OutboxPollingService<CustomizationDbContext>` + `AddCustomizationContractServices()`; Worker validator'ı kullanmaz ama `IUsageReporter` kaydı için gerekir), `TestFixture` Respawn şeması `customization`.
- `IUsageReporter` (M7 kuralı: her modülün tam bir reporter'ı): `Module = "customization"`, metrikler `customization.fields.<entityType>` (etkin alan sayısı; **`.records` anahtarı üretmez** → `maxRecords`'a sayılmaz). `GET /subscription` yanıtına `usage.customFields: { "account": 3, … }` bu metriklerden eklenir.

## Alan tanımı modeli

### Ortak özellikler
| Alan | Kural |
|---|---|
| `key` | `^[a-z][a-z0-9_]{0,39}$`; kiracı+varlık türü başına benzersiz (arşivliler dahil, büyük/küçük harf yok); **değişmez**; `customFields` JSON anahtarı ve `cf.<key>` parametresidir |
| `dataType` | 11 tipten biri; **değişmez** |
| `labels` | `{ "tr": "…", "en": "…" }`; en az biri dolu (≤ 100); yanıtta istemci `labels[dil] ?? labels[diğeri] ?? key` çözer |
| `helpText` | isteğe bağlı `{ tr?, en? }` (≤ 300) |
| `isRequired` | bool (varsayılan false); yalnız oluşturma/gönderilen-anahtar denetimi (D6) |
| `defaultValue` | isteğe bağlı; tipin kurallarına uymalı (tanım anında doğrulanır; seçimde **etkin** seçenek anahtarı); yalnız **oluşturmada** uygulanır, mevcut satırlara geriye dönük yazılmaz |
| `constraints` | tipe özgü, aşağıdaki tablo; tabloda olmayan anahtar `validation` (`errors["constraints.<ad>"]`) |
| `options` | yalnız `singleSelect`/`multiSelect`; `[{ key, labels, position, isArchived }]`, ≤ 100 (arşivliler dahil), ≥ 1 etkin |
| `sectionKey` / `position` | bölüm anahtarı (`null` = bölümsüz, formda sonda "Diğer") + bölüm içi sıra |
| `isSensitive` | bool; **değişmez**; denetim değeri `***`; (D11: API'de görünürlüğü kısıtlamaz) |
| `showInList` | bool; liste sütunu (varlık başına ≤ 5); `longText` için geçersiz; tipi sıralanabilirse `cf.<key>` sıralaması da açılır |
| `isFilterable` | bool; `cf.<key>` süzgeci (varlık başına ≤ 10); `longText` için geçersiz |
| `isArchived` | bool; arşivli alan formda/şemada (varsayılan) görünmez, değer yazılamaz, satırdaki değer **silinmez**; geri yüklenince değerler geri gelir |

`sortable` türetilir: `showInList && dataType ∈ {text, number, decimal, date, boolean, url, email, phone}` (`singleSelect`/`multiSelect`/`longText` sıralanmaz — seçim sıralaması etiket sırasına değil anahtara düşerdi, kullanıcı için anlamsız).

### Tip matrisi (değer biçimi, kısıtlar, kurallar, işleçler)
`customFields` içindeki değerin JSON biçimi **katıdır** (sayı için sayı, `"12"` değil; uymayan biçim `custom_field.type_mismatch`). Temizleme = `null` (veya çoklu seçimde boş dizi); saklanan biçimde **anahtar silinir** (JSON `null` saklanmaz) → "değer yok" tek anlama gelir.

| Tip | JSON değeri | `constraints` | Doğrulama kuralları / kanonik biçim | `cf.` işleçleri | Sıralanır |
|---|---|---|---|---|---|
| `text` | string | `minLength` (0…`maxLength`, vars. 0), `maxLength` (1…500, vars. 255), `pattern` (D14) | Kırpılır; kırpma sonrası boşsa "değer yok"; kontrol karakteri (< 0x20) reddedilir; uzunluk aralığı; `pattern` tam eşleşme | `eq`, `contains`, `isnull`, `notnull` | evet |
| `longText` | string | `minLength`, `maxLength` (1…4000, vars. 2000) | CRLF→LF; kırpılır; satır sonu serbest; pattern yok | — | hayır |
| `number` | JSON tam sayı | `min`, `max` (±(2^53−1)) | Kesirli/üstel biçim reddedilir (`custom_field.not_integer`); aralık | `eq`, `gt`, `gte`, `lt`, `lte`, `isnull`, `notnull` | evet |
| `decimal` | JSON sayı | `scale` (0…6, vars. 2), `min`, `max`, `unit` (≤ 16, yalnız gösterim: "TRY", "kg", "%") | Ondalık basamak > `scale` **reddedilir** (yuvarlanmaz, `custom_field.too_many_decimals`); toplam anlamlı basamak ≤ 15 (`\|değer\| < 10^(15−scale)`; JS `number` ile kayıpsız); kanonik: sondaki sıfırlar atılır | tam sayı ile aynı | evet |
| `date` | `"YYYY-MM-DD"` | `min`, `max` (tarih), yıl 1900–2100 | Takvimce geçerli gün; saat dilimi yok | `eq`, `gt`, `gte`, `lt`, `lte`, `isnull`, `notnull` | evet |
| `boolean` | `true`/`false` | — | Üç durum: `true`, `false`, değer yok | `eq`, `isnull`, `notnull` | evet |
| `singleSelect` | string (seçenek anahtarı) | — | Anahtar **etkin** seçenek olmalı; arşivli seçenek yalnız kaydın **mevcut** değeriyse kabul (değişmemiş) | `eq`, `in` (≤ 20), `isnull`, `notnull` | hayır |
| `multiSelect` | string dizisi | `maxSelections` (1…seçenek sayısı, vars. seçenek sayısı) | Tekilleştirilir, seçenek `position` sırasına dizilir; boş dizi = temizle; arşivli seçenek yalnız kaydın mevcut dizisinde varsa korunur | `in` (herhangi biri, ≤ 20), `contains` (hepsi, ≤ 20), `isnull`, `notnull` | hayır |
| `url` | string | `maxLength` (≤ 500, vars. 500) | Mutlak `http`/`https` (`javascript:` vb. reddedilir; C-SEC L7 ile aynı kural) | `eq`, `contains`, `isnull`, `notnull` | evet |
| `email` | string | — | ≤ 254, mevcut `OptionalEmail` kuralı; küçük harfe çevrilir | `eq`, `contains`, `isnull`, `notnull` | evet |
| `phone` | string | — | ≤ 32, yalnız `[0-9+()\-. ]`, ≥ 3 rakam; kırpılır, **normalize edilmez** (firma/kişi telefonuyla aynı) | `eq`, `contains`, `isnull`, `notnull` | evet |

Süzgeç kuralları: `eq` çoklu değerli verilirse `in` sayılmaz (`400`); `contains` metinde alt dize (`ILIKE`, `%`/`_`/`\` kaçışlı, M2 `SearchPattern`), çoklu seçimde "hepsini içerir"; değerler tipe göre ayrıştırılır (bozuk değer `400 validation`, `errors["cf.<key>"]`); `eq` metin eşitliği büyük/küçük harf **duyarlıdır** (GIN uyumu).

### Değer yükü tavanları (yumuşak sınır)
Yazma sonrası **birleşik** `custom` nesnesinin JSON boyutu ≤ 16 KiB (`Customization:Limits:MaxValuesPayloadBytes`); istekteki anahtar sayısı ≤ 100; tek tek değerler yukarıdaki tip sınırlarıyla sınırlı. Aşımda `400 validation`, `errors["customFields"]` = `custom_field.payload_too_large`. Ayar düşürüldüğünde mevcut büyük satırlar okunur; yalnız yeni yazımda denetlenir.

## Değer yazma kuralları (varlık uçları; `ICustomFieldValidator.ValidateAsync`)

Girdi: `entityType`, istekteki `customFields` (yok = `null`), kaydın saklı `custom`'ı (oluşturmada boş), mod (`Create`/`Update`). Çıktı: birleşmiş yeni `custom` **veya** alan hataları. Handler hata varsa `ValidationException` fırlatır (Commerce `LineProductVerifier` deseni → `400 validation`, `errors["customFields.<key>"]`, kalan hatalarla aynı yanıtta); başarıda `entity.SetCustomFields(...)`.

1. `customFields` yok: **oluşturmada** varsayılanlar uygulanır ve zorunluluk denetlenir (zorunlu + varsayılansız etkin alan varsa `custom_field.required`); **güncellemede** hiçbir şey değişmez ve zorunluluk denetlenmez.
2. Gönderilen her anahtar için: tanımsız → `custom_field.unknown`; **arşivli → `custom_field.archived`** (yayımlanmış eski form yanlışlıkla arşivli alanı yazamaz; istemci şemayı yeniden çekip tekrar dener); `null`/boş → temizle (zorunluysa `custom_field.required`); aksi halde tip matrisine göre doğrula/kanonikleştir (yukarıdaki hata anahtarları).
3. **Oluşturmada** gönderilmeyen etkin alanlardan varsayılanı olanlar varsayılanla dolar; **açıkça `null` gönderilen** alan varsayılanı **bastırır** (kullanıcı bilerek boş bıraktı).
4. **Oluşturmada** gönderilmeyen zorunlu (varsayılansız) etkin alan → `custom_field.required`. **Güncellemede** yalnız gönderilen anahtarlar denetlenir.
5. Saklı olup istekte olmayan anahtarlar (arşivli/yetim dahil) **aynen korunur** (merge).
6. Seçim alanlarında arşivli seçenek: değer kaydın saklı değeriyle **aynıysa** kabul; değilse `custom_field.option_archived`. Bilinmeyen anahtar `custom_field.option_unknown`.
7. Birleşik yük tavanı (`custom_field.payload_too_large`, `errors["customFields"]`).
8. **Sistem kaynaklı yollar** zorunluluk ve varsayılan uygulamaz: `ConvertLeadHandler` (firma/kişi/fırsat özel alansız açılır; lead'in değerleri lead'de kalır — Zoho "alan eşleme" kapsam dışı), Conductor görev işleyicileri, `SalesOrder` teklif kopyası (aşağıda).
9. **Teklif → sipariş** (`ConvertQuoteHandler`, tek transaction): `ICustomFieldValidator.MapAsync("quote", "order", quote.Custom)` — hedefte **etkin, aynı `key` ve aynı `dataType`** alan varsa ve değer hedefin kısıtlarına uyuyorsa kopyalanır; aksi halde sessizce atlanır. Zorunluluk denetlenmez.
10. Eşzamanlılık: özel alan güncellemesi kaydın diğer alanlarıyla aynı `UPDATE`'tir (`xmin` taşıyan Case/Quote/Order'da çakışma `general.concurrency_conflict` 409; diğerlerinde son yazan kazanır — mevcut davranış).

### Yanıt biçimi
- **Detay** (`GET /accounts/{id}` vb.): `customFields: { key: değer }` **her zaman** yazılır (boşsa `{}`); yalnız **etkin** alan anahtarları ve değeri olanlar; arşivli/yetim anahtarlar dönmez. Değerler kanonik JSON tipleridir.
- **Liste** (`GET /accounts` vb. ve alt listeler): `customFields` yalnız `showInList` etkin alanların anahtarlarını içerir (boşsa `{}`).
- Yeni alan eklenince mevcut satırlar `customFields: {}` (migration `DEFAULT '{}'`); varsayılan mevcut satırlara **yazılmaz** (formda yalnız yeni kayıt önerisi olarak görünür).
- `GET /deals/board` kartları ve M3 aktivite `relatedName` çözümlemeleri özel alan taşımaz.

## Değişiklik kuralları (tanım güncelleme)

| Değişiklik | Kural |
|---|---|
| `key`, `dataType`, `isSensitive` | Değişmez; `PUT` gövdesinde farklı değerle gelirse `422 customization.immutable_property` (`args.property`) |
| `labels`, `helpText`, `unit` | Serbest |
| `isRequired`, `defaultValue`, `constraints` | Serbest ve **geriye dönük değil**: mevcut değerler yeni kısıta uymasa da okunur; yalnız o anahtar sonraki yazımda gönderilirse doğrulanır |
| `showInList`, `isFilterable` | Serbest; varlık başına üst sınır aşılırsa `422 customization.list_columns_limit` / `customization.filterable_limit` |
| Arşivle | Serbest, idempotent (204); arşivli alanın tanımı düzenlenemez (`409 customization.field_archived`, önce geri yükle); liste/süzgeç/sıralama/`cf.` kullanımı kapanır |
| Geri yükle | Idempotent; etkin alan limiti dolmuşsa `402 plan.limit_exceeded`; bölümü yoksa bölümsüz |
| Seçenek ekle | Serbest (alan başına ≤ 100); anahtar benzersiz (`409 customization.option_key_taken`) |
| Seçenek yeniden adlandır | Yalnız `labels`; anahtar sabit |
| Seçenek "sil" | **Yalnız arşiv**; alandaki son etkin seçenek arşivlenemez (`422 customization.last_active_option`); varsayılan değer olan seçenek arşivlenemez (`422 customization.option_is_default`); arşivli seçenek yeni yazımlarda seçilemez, mevcut değerlerde geçerli kalır, geri yüklenebilir |
| Silme | **Yok** (alan ve seçenek): silinen anahtarın yeniden farklı tiple açılması eski değerleri yanlış yorumlatırdı |
| Bölüm/sıra | Yalnız `PUT …/layout` (aşağıda); alan `position`ı bölüm içindeki dizin |

Tanım yazan her komut aynı transaction'da: (a) kiracı+varlık türü başına `pg_advisory_xact_lock` (`IModuleUnitOfWork.AcquireAdvisoryLockAsync("customization:{tenant}:{entityType}")`; limit sayımı ve `version` artışı aynı kilitle serileşir), (b) `EntitySchemaState.Version++`, (c) denetim kaydı (`CustomFieldDefinition`/`CustomFieldSection`, `IAuditLogged`), (d) `CustomFieldDefinitionChanged` outbox olayı, (e) commit sonrası şema önbelleği geçersiz kılma.

## Plan limiti (M7 entegrasyonu)

- **Plan yapılandırması:** `Platform:Plans[].limits.maxCustomFieldsPerEntity` (int?; yok/`null` = sınırsız, `0` = özel alan açılamaz). Örnek (ticari karar değil, test verisi): `internal` yok (sınırsız), `starter` 5, `business` 30, `enterprise` 100. Migrator plan doğrulaması: ≥ 0.
- **Kiracı istisnası:** `overrides.maxCustomFieldsPerEntity` — `maxUsers` gibi **anahtar varlığı esastır** (`null` = açıkça sınırsız). `PUT /platform/organizations/{id}/subscription` ve konsol diyaloğu bunu taşır; doğrulama `errors["overrides.maxCustomFieldsPerEntity"]`.
- **Sayım ve zorlama:** limit **etkin (arşivsiz) alan sayısıdır, varlık türü başına**. Alan oluşturma ve geri yükleme, kilit altında, kendi sayımıyla `ILimitGuard.EnsureAsync(new LimitDemand(LimitKeys.CustomFields, "customization", 1, Scope: entityType, Used: etkinSayı))` çağırır → `402 plan.limit_exceeded` (`args`: `limit=customFields`, `entityType`, `max`, `used`). **Sert** limittir (kullanıcı limiti gibi kilitli; eşzamanlı 20 oluşturma limitin 1 altındayken tam 1 başarı). Arşivleme yer açar. `null` limit için sayım sorgusu **yapılmaz** (`internal` sıfır ek iş).
- **Plan düşürme:** mevcut alanlar silinmez/arşivlenmez, okunur ve değer yazımı sürer; yalnız yeni alan/geri yükleme bloklanır (M7 "veri silinmez" ilkesi). Özel alan aşımı `overLimit` listesine **girmez** (M7 `overLimit` yalnız kullanıcı/kayıt); `GET /subscription` `usage.customFields` ve `limits.maxCustomFieldsPerEntity` ile çubuk çizilir.
- **Salt okunur / askı / silme bekleyen kiracı:** tanım komutları ve varlık yazımları `EntitlementBehaviour` tarafından zaten reddedilir (`tenant.suspended`); şema **sorgusu** salt okunurda çalışır (form/panel görünür), `none` erişimde 403.
- **Modül kapalı:** `case`/`quote`/`order`/`campaign` türlerinin tanım ve şema uçları, ilgili kapı modülü planda kapalıysa `403 plan.module_disabled` (`args.module`); `GET /customization/entities` bu türleri `moduleEnabled: false` işaretler.
- **Ek (küçük) Platform değişikliği:** `PlanLimits`/`TenantOverrides`/`EffectiveLimits`/`EntitlementSnapshot` (`MaxCustomFieldsPerEntity`, **son parametre, varsayılan `null`**), `PlanLimitsDto`/`EffectiveLimitsDto`/`SubscriptionLimitsDto`, `EffectiveEntitlements.Merge`, `PlatformOptions` doğrulaması, `LimitGuard` yeni `LimitKeys.CustomFields` kolu; JSON'da yeni anahtar eksikse eski planlar/sıra dışı satırlar **sınırsız** okunur (geriye uyum; migration gerekmez, `platform.plans.limits` jsonb'dir).

## Sorgulama: liste süzme, sıralama, indeks stratejisi

### Sözleşme
- Süzgeç: `cf.<key>=değer` (eşitlik; birden çok değer = `in`), `cf.<key>.<op>=değer`, `op ∈ {eq, in, contains, gt, gte, lt, lte, isnull, notnull}` (tipe göre yukarıdaki tablo; `isnull`/`notnull` değersiz, `=true`). Örnek: `GET /accounts?cf.risk_level=high,medium&cf.credit_limit.gte=5000&cf.vip=true`.
- Sıralama: `sort=cf.<key>` / `sort=-cf.<key>` (`showInList` + sıralanabilir tip); başka alanlarla birlikte kullanılabilir (`sort=-cf.credit_limit,name`), **en çok bir** `cf.` anahtarı; boş değerler **her iki yönde de sonda**; her zaman `Id` ile kararlı.
- **Sıkılık (bilinçli sapma):** M2'nin "bilinmeyen alan yok sayılır" kuralı sabit alanlarda sürer; ama `cf.*` dinamik olduğundan yazım hatası "süzgeç yok" gibi sessiz sonuç doğurmasın diye **`400 validation`** döner: bilinmeyen/arşivli/`isFilterable` olmayan/sıralanamaz alan, tipe uymayan işleç, bozuk değer, `cf.` süzgeç sayısı > 3, `in` değer sayısı > 20, ikinci `cf.` sıralaması (`errors["cf.<key>"]` veya `errors["sort"]`).
- `q` özel alanlarda **aramaz**.

### `PagedQueryBinder` değişikliği (Shared.Web, yalnız ekleme)
Mevcut bağlayıcı anahtarı **son noktadan** böler ve tanınmayan işleci sessizce atar → `cf.risk_level` (`field=cf`, `op=risk_level`) düşerdi. Yeni kural: `cf.` önekli anahtar önce ayrıştırılır: `cf.<key>` (= `eq`) veya `cf.<key>.<op>` (anahtar nokta içermez); `Field = "cf.<key>"`, `Op` = eşlenen `FilterOp`; geçersiz `op` → ModelState hatası (400). Diğer parametrelerin davranışı değişmez. (`sort=cf.<key>` zaten `SortClause.Field = "cf.<key>"` olarak gelir.)

### Uygulama deseni (her varlık modülünün `*ReadStore`'u, imza değişmez — `PagedQuery.Filters` zaten içeride)
1. Store `ICustomFieldQueryPlanner.PlanAsync(entityType, paging)` çağırır: şemaya karşı doğrular (yukarıdaki 400'ler) ve tipli, beyaz listeden bir `CustomFieldQueryPlan` döner (`Filters`, `Sort?`); anahtarlar `^[a-z][a-z0-9_]{0,39}$`'a **zaten uyar** ve yine de her zaman **parametre** olarak gider (SQL'e birleştirilmez).
2. Ortak eklenti (`Shared.Infrastructure/Querying/CustomFieldListQuery`): `query.ApplyCustomFilters(e => e.Custom, plan)` ve sıralama birleştirme; EF çevirisi kayıtlı DbFunction'larla (`SalesDbFunctions.ToLocalTimestamp` kalıbı): eşitlik/`in`/çoklu-içerme `custom @> @p::jsonb` (`@p` `System.Text.Json` ile üretilir, dize birleştirme yok; `in` = `OR`, çoklu "hepsi" = tek `@>`), metin `contains` `(custom ->> @k) ILIKE @pat ESCAPE '\'`, aralık/sıralama `((custom ->> @k)::numeric)` / `(...)::date` **`jsonb_typeof(custom -> @k) = 'number'|'string'` korumasıyla** (bozuk değer sorguyu asla düşürmez), `isnull` `NOT (custom ? @k)`.
3. Mevcut `GridFieldMap` beyaz listeleri (sabit alanlar) **değişmez**; `cf.` sıralaması onun **yanında** kullanıcı sırası korunarak ORDER BY'a girer (`NULLS LAST`), sonda `Id`.
4. Ham SQL/`IgnoreQueryFilters` **eklenmez**: M7 envanter testi (`TenantFilterBypassInventoryTests`) değişmez; JSON işlevleri EF/DbFunction ile çevrilir. Zorunlu kalırsa envanter bilinçli güncellenir ve güvenlik incelemesine girer.

### İndeks stratejisi ve sınırlar
- Her kapsamlı tabloya **tek** indeks: `CREATE INDEX ix_<tablo>_custom ON <şema>.<tablo> USING gin (custom jsonb_path_ops)` (model: `HasIndex(x => x.Custom).HasMethod("gin").HasOperators("jsonb_path_ops")`). Planlayıcı bunu mevcut `(tenant_id, …)` btree indeksleriyle `BitmapAnd` eder. `jsonb_path_ops` `@>`'yı hızlandırır (eşitlik, `in`, çoklu seçim içerme); `isnull`, `ILIKE contains`, aralık ve sıralama **indekssizdir** (kiracı önekli tarama + `LIMIT`; alan sayısı ve istek başına süzgeç sayısıyla sınırlı). Büyük tablolarda `CREATE INDEX CONCURRENTLY` için migration `suppressTransaction` ile elle uyarlanabilir (pilot tabloları küçüktür; zorunlu değil).
- **Yapılmayanlar (bilinçli):** kiracı/alan başına ifade veya kısmi indeks (dinamik DDL; 1.000 kiracı × 10 alan = 10.000 indeks), `btree_gin` eklentisi (operasyonel bağımlılık). Ölçülmüş yavaş büyük kiracı çıkarsa **operasyon işi** olarak `CREATE INDEX CONCURRENTLY … ((custom ->> 'anahtar')) WHERE tenant_id = '…'` elle eklenir; bu kart otomatikleştirmez.
- Sorgu üst sınırı mevcut Npgsql komut zaman aşımına (30 sn) tabidir; sayfa boyutu üst sınırı (100) geçerlidir.

## Denetim, KVKK ve silme

- **Kayıt bazlı denetim:** `AuditLogInterceptor` (Shared.Infrastructure) `ICustomFieldsHolder` uygulayan agregatta `Custom` özelliğini tek "custom" alanı olarak değil **anahtar anahtar** yazar: oluşturmada her anahtar `customFields.<key>: { old: null, new: v }`, güncellemede yalnız değişenler, silmede (yumuşak silme) her anahtar `old: v, new: null`. Yalnız `Custom` değişen güncelleme de bir `updated` denetim kaydıdır. `GET /audit?entityType=Account&entityId=` ve `GET /organization/audit` bu satırları olduğu gibi gösterir.
- **Hassaslık:** anahtar, `ICustomFieldSchemas.GetAsync(entityType)` (arşivliler dahil) `isSensitive` ise değer (`old` ve `new`) `***` yazılır; şemada bulunmayan (yetim) anahtar **maskelenir**; şema okunamazsa **tüm** özel alan değerleri maskelenir (kapalı-başarısız). `SensitiveFields` (statik, sabit alanlar) etkilenmez. `isSensitive` değişmez olduğundan geçmiş denetim satırları hiçbir zaman sonradan hassaslaşmaz (bilinçli; geriye dönük redaksiyon işi yoktur).
- **Tanım denetimi:** `CustomFieldDefinition`, `CustomFieldSection` `IAuditLogged`; `EntityType` = bu adlar; `GET /audit?entityType=CustomFieldDefinition&entityId={id}` `org.customization.manage` (veya `org.audit.read`) ile (`CustomizationAuditEntities` + `IAuditEntityPermissions`). Seçenek değişiklikleri tanımın `options` alan farkı olarak görünür. Tanımlarda kişisel veri yoktur (etiket/kısıt/seçenek metinleri): maskelenen alan yok.
- **Olay:** `CustomFieldDefinitionChanged` (aşağıda) outbox'a yazılır; M8D'de tüketici yok (önbellek geçersiz kılma doğrudan aynı süreçte yapılır; olay ileride workflow/arama/önbellek yayılımı içindir).
- **İmha (M7 KVKK):** değerler varlık satırının parçası olduğundan `TenantDataEraser<TContext>` tüm varlık tablolarıyla (yumuşak silinenler dahil) **ek adımsız** siler; tanımlar/bölümler/şema sürümü `TenantDataEraser<CustomizationDbContext>` ile otomatik silinir (yeni modül yeni `ITenantEntity` tabloları ile kapsanır). `AddModuleDbContext` eraser'ı kendisi kaydeder. Şema önbelleği `tenant:{T}` etiketiyle imhada geçersiz kılınır.

## Veri modeli (`customization` şeması, migration `InitialCustomization`; kimlikler `Guid.CreateVersion7`, `ValueGeneratedNever`; hepsi `ITenantEntity`)

### `customization.field_definitions` (`CustomFieldDefinition`; `TenantAggregateRoot`, `IAuditLogged`; yumuşak silinmez; `xmin` eşzamanlılık belirteci)
| Kolon | Not |
|---|---|
| `id uuid PK`, `tenant_id uuid` | |
| `entity_type varchar(24)`, `key varchar(40)` | |
| `data_type varchar(16)` | camelCase enum string |
| `labels jsonb`, `help_text jsonb?` | `{ tr?, en? }` |
| `is_required bool`, `default_value jsonb?` | |
| `constraints jsonb` | `{}` varsayılan; tipe özgü beyaz liste |
| `options jsonb?` | `[{ key, labels, position, isArchived }]` (≤ 100) |
| `section_key varchar(40)?`, `position int` | |
| `is_sensitive`, `show_in_list`, `is_filterable`, `is_archived bool`, `archived_at?` | |
| `created_at`, `created_user_id`, `modified_date`, `modified_user_id` | `IAuditable` |
İndeksler: **benzersiz** `(tenant_id, entity_type, key)` (arşivliler dahil, filtre yok); `(tenant_id, entity_type, is_archived, section_key, position)` (şema yükleme).

### `customization.layout_sections` (`LayoutSection`; `TenantAggregateRoot`, `IAuditLogged`)
`id`, `tenant_id`, `entity_type`, `key varchar(40)`, `labels jsonb`, `position int`. Benzersiz `(tenant_id, entity_type, key)`; `(tenant_id, entity_type, position)`.

### `customization.entity_schema_states` (`EntitySchemaState`; `TenantEntity`, denetimsiz teknik tablo)
`id`, `tenant_id`, `entity_type`, `version int` (satır ilk tanım/yerleşim yazımında `1` ile oluşur; satır yokken şema ucu `version: 0` döner), `updated_at`. Benzersiz `(tenant_id, entity_type)`. Tüm tanım/yerleşim yazımlarında kilit altında `version++`.

### Varlık modüllerindeki eklemeler (migration `AddCustomFields`, modül başına bir tane)
Tüm kapsamlı tablolara `custom jsonb NOT NULL DEFAULT '{}'::jsonb` + `ix_<tablo>_custom` GIN indeksi (yukarıda): Sales `accounts, contacts, leads, deals`; Service `cases`; Commerce `quotes, sales_orders`; Marketing `campaigns`. Domain: agregat `ICustomFieldsHolder` uygular (`CustomFieldValues Custom { get; }`, `SetCustomFields(CustomFieldValues)`); `Create(...)`'e isteğe bağlı `custom` parametresi eklenir (varsayılan boş); EF eşlemesi `Shared.Infrastructure`'daki ortak `CustomFieldValuesConverter` + değer karşılaştırıcı (derin eşitlik → `Modified`/denetim farkı doğru çalışır). `SalesDocument` tabanında tanımlanır (Quote/Order ortak).

## Contracts eklemeleri (tam liste)

1. **`Shared.Kernel`** (`Domain/CustomFields.cs`): `CustomFieldValues` (sealed, değişmez, `IReadOnlyDictionary<string, JsonElement>`, anahtarlar `Ordinal` sıralı, kanonik JSON, `ByteSize`, `With/Without`, `Empty`) ve `ICustomFieldsHolder`.
2. **`Shared.Contracts.CustomFields`** (yeni; Customization uygular, varsayılanlar `AddCrmCore`'da `TryAdd`):
   ```csharp
   public static class CustomEntityTypes { public const string Account = "account", Contact = "contact", Lead = "lead", Deal = "deal", Case = "case", Quote = "quote", Order = "order", Campaign = "campaign"; /* All */ }
   public sealed record CustomEntityRegistration(string EntityType, string Module, string AuditEntityName, string ReadPermission, string WritePermission);   // her varlık modülü Add<Modül>ContractServices'te kaydeder
   public interface ICustomEntityRegistry { IReadOnlyList<CustomEntityRegistration> All { get; } CustomEntityRegistration? Find(string entityType); CustomEntityRegistration? FindByAuditName(string auditEntityName); }

   public enum CustomFieldWriteMode { Create, Update }
   public sealed record CustomFieldFailure(string Key /* "" = tüm nesne */, string MessageKey, IReadOnlyDictionary<string, object?>? Args = null);
   public sealed record CustomFieldWriteResult(CustomFieldValues Values, IReadOnlyList<CustomFieldFailure> Failures) { public bool IsValid => Failures.Count == 0; }
   public interface ICustomFieldValidator
   {
       Task<CustomFieldWriteResult> ValidateAsync(string entityType, IReadOnlyDictionary<string, JsonElement>? requested, CustomFieldValues existing, CustomFieldWriteMode mode, CancellationToken ct = default);
       Task<CustomFieldValues> MapAsync(string fromEntityType, string toEntityType, CustomFieldValues source, CancellationToken ct = default);   // teklif→sipariş
   }

   public sealed record CustomFieldSchema(Guid Id, string Key, string DataType, IReadOnlyDictionary<string, string> Labels, /* … tam alan listesi: tanım modeli */ bool IsSensitive, bool ShowInList, bool IsFilterable, bool IsArchived);
   public sealed record CustomEntitySchema(string EntityType, int Version, IReadOnlyList<CustomFieldSchema> Fields /* arşivliler dahil */)
   { public IReadOnlySet<string> SensitiveKeys { get; } public CustomFieldValues ForDetail(CustomFieldValues stored); public CustomFieldValues ForList(CustomFieldValues stored); }
   public interface ICustomFieldSchemas { Task<CustomEntitySchema> GetAsync(string entityType, CancellationToken ct = default); }   // HybridCache'li, kiracı kapsamlı

   public sealed record CustomFieldFilter(string Key, string DataType, FilterOp Op, IReadOnlyList<JsonElement> Values);
   public sealed record CustomFieldQueryPlan(IReadOnlyList<CustomFieldFilter> Filters, (string Key, string DataType, bool Descending)? Sort);
   public interface ICustomFieldQueryPlanner { Task<CustomFieldQueryPlan> PlanAsync(string entityType, PagedQuery paging, CancellationToken ct = default); }   // hata → ValidationException
   ```
   Varsayılan (Customization yok): `ICustomFieldValidator` yalnız boş/`null` `customFields` kabul eder, başka her anahtar `custom_field.unknown`; şema boş; plan boş.
3. **`Shared.Contracts.Entitlements`:** `LimitKeys.CustomFields = "customFields"`; `EntitlementSnapshot`'a son parametre `int? MaxCustomFieldsPerEntity = null`; `LimitDemand`'a isteğe bağlı `string? Scope = null, long? Used = null` (sahip modül kendi sayımını verir; verilmezse özel alan anahtarı için `Success`); `EntitlementErrors.Exceeded(...)`'a `entityType` argümanlı aşırı yükleme.
4. **`Shared.Infrastructure`:** `CustomFieldValuesConverter`/karşılaştırıcı, `CustomFieldDbFunctions` (`jsonb_typeof`, `->>`, `@>`, `?`), `CustomFieldListQuery`, `CustomFieldWriter` (handler yardımcısı: doğrula → `ValidationException` `CustomFields.<key>` → `SetCustomFields`), `AuditLogInterceptor` genişlemesi, `AddCrmCore` varsayılanları.
5. **`Shared.Web`:** `PagedQueryBinder` (`cf.` önek kuralı).
6. **`Customization.Contracts`:** `CustomizationPermissions`, `CustomizationAuditEntities`, `CustomFieldDefinitionChanged`.
7. **Varlık modülleri:** her `Add<Modül>ContractServices` bir `CustomEntityRegistration` (Sales 4, Service 1, Commerce 2, Marketing 1) ekler.

## İzinler

`CustomizationPermissions`: **`org.customization.manage`** (grup `org`, modül `customization`) → `GET /permissions` +1 (test sayı sabitlemez). **`SystemRoleDefinitions` değişmez:** Administrator = kataloğun tümü (anahtarı alır); Standard = `crm.*` + `org.users.read` (almaz — alan tanımı yönetici işidir). Mevcut kiracılara `SystemRolePermissionSynchronizer` yayar (test: eski kiracının Administrator'ında görünür, Standard'da yok).
- Tanım uçları (aşağıda) hepsi `org.customization.manage`; **şema ucu** `[AnyAuthenticatedUser]` (gerekçeli istisna, `GetEntityAudit` gibi) ve handler'da tür başına kontrol: çağıranın o türün `ReadPermission`ı **veya** `org.customization.manage`; `includeArchived=true` yalnız `manage` ile.
- Doğrulama yetkiden önce çalışır (M2 kuralı). Varlık uçlarının izinleri **değişmez** (`crm.<kaynak>.write` oluşturma/güncellemede özel alanı da kapsar; ek izin yok — D11).
- `RequestAuthorizationTests`: `CreateFieldDefinitionCommand` `[NoPlanLimit("kayıt değil; alan limiti handler'da CustomFields anahtarıyla denetlenir")]` taşır (M7 `Create*Command` kuralı); yeni `[TenantStatusExempt]` **yok** (onaylı liste değişmez).

## HTTP kontratı — Customization (`/customization/**`)

Yanıtlarda `labels`/`helpText` nesneleri `{ tr?, en? }`.

### Şema (SPA'nın tek okuma ucu; önbellekli)
- `GET /customization/schema/{entityType}?includeArchived=false` → 200. Başlıklar: `ETag: "<entityType>-<version>"`, `Cache-Control: private, no-cache` (tarayıcı koşullu ister), `If-None-Match` eşleşirse **304**. Yetki: yukarıdaki kural; tür bilinmiyor `404 customization.entity_type_unknown`; modül kapalı `403 plan.module_disabled`; yetkisiz `403 forbidden`.
  ```json
  { "entityType": "account", "version": 7,
    "limits": { "maxFields": 30, "activeFields": 12, "maxListColumns": 5, "maxFilterable": 10, "maxOptionsPerField": 100, "maxSections": 10 },
    "sections": [ { "key": "finance", "labels": { "tr": "Finans", "en": "Finance" }, "position": 0 } ],
    "fields": [
      { "id": "…", "key": "credit_limit", "dataType": "decimal", "labels": { "tr": "Kredi limiti", "en": "Credit limit" },
        "helpText": { "tr": "TL cinsinden" }, "isRequired": true, "defaultValue": 1000,
        "constraints": { "scale": 2, "min": 0, "max": 1000000, "unit": "TRY" },
        "sectionKey": "finance", "position": 0, "isSensitive": false, "showInList": true, "isFilterable": true, "sortable": true, "isArchived": false },
      { "id": "…", "key": "risk_level", "dataType": "singleSelect", "labels": { "tr": "Risk" }, "isRequired": false,
        "options": [ { "key": "low", "labels": { "tr": "Düşük", "en": "Low" }, "position": 0, "isArchived": false },
                     { "key": "old_band", "labels": { "tr": "Eski bant" }, "position": 1, "isArchived": true } ],
        "sectionKey": null, "position": 0, "isSensitive": false, "showInList": false, "isFilterable": true, "sortable": false, "isArchived": false } ] }
  ```
  `fields`: bölüm sırası → bölüm içi `position`; bölümsüzler sonda. `limits.maxFields` `null` ise yazılmaz (sınırsız). Arşivli alan yalnız `includeArchived=true`; arşivli **seçenekler** her zaman gelir (mevcut değeri olan kaydın formu etiketi gösterebilsin) ama `isArchived: true`.
- `GET /customization/entities` (`manage`) → `[ { "entityType": "account", "module": "sales", "moduleEnabled": true, "version": 7, "activeFields": 12, "archivedFields": 1, "maxFields": 30 } ]` (kayıtlı türler, `CustomEntityTypes` sırasıyla; ayarlar sekmelerini besler).

### Alan tanımı
- `POST /customization/entities/{entityType}/fields` (`manage`) → **201** + `Location: /api/v1/customization/schema/{entityType}?includeArchived=true` + alan gövdesi (şemadaki alan biçimi). Gövde:
  ```json
  { "key": "credit_limit", "dataType": "decimal", "labels": { "tr": "Kredi limiti", "en": "Credit limit" }, "helpText": { "tr": "…" },
    "isRequired": false, "defaultValue": 1000, "constraints": { "scale": 2, "min": 0 },
    "options": null, "isSensitive": false, "showInList": false, "isFilterable": false }
  ```
  Seçim tiplerinde `options: [{ key?, labels }]` (≥ 1; `key` yoksa `tr` etiketinden `snake_case` türetilir, çakışırsa `_2`…); yeni alan **bölümsüz, sonda** açılır. Hatalar: `400 validation` (`errors["key"]`, `["labels"]`, `["dataType"]`, `["constraints.maxLength"]`, `["constraints.pattern"]` (geçersiz/desteklenmeyen regex), `["defaultValue"]`, `["options[i].key"]`, `["showInList"]`…), `409 customization.key_taken`, `402 plan.limit_exceeded`, `422 customization.hard_cap_reached` (varlık türü başına 200 tanım), `422 customization.list_columns_limit`, `422 customization.filterable_limit`, `403 plan.module_disabled`, `404 customization.entity_type_unknown`.
- `PUT /customization/fields/{id}` (`manage`) → 204. Gövde: `labels`, `helpText?`, `isRequired`, `defaultValue?`, `constraints`, `showInList`, `isFilterable` (tam değiştirme; gönderilmeyen isteğe bağlı temizlenir); `key`, `dataType`, `isSensitive`, `options` yok (seçenekler ayrı uçlarda). `key`/`dataType`/`isSensitive` gönderilirse ve farklıysa `422 customization.immutable_property`. `409 customization.field_archived`, `409 general.concurrency_conflict` (`xmin`), `404 not_found`.
- `POST /customization/fields/{id}/archive`, `POST /customization/fields/{id}/restore` → 204 (idempotent); geri yükleme `402 plan.limit_exceeded` verebilir.

### Seçenekler (yalnız `singleSelect`/`multiSelect`; başka tipte `409 customization.not_a_select`)
- `POST /customization/fields/{id}/options` `{ key?, labels }` → **201** `{ key, labels, position, isArchived }` (sona eklenir); `409 customization.option_key_taken`, `422 validation` (≤ 100).
- `PUT /customization/fields/{id}/options/{optionKey}` `{ labels }` → 204.
- `POST /customization/fields/{id}/options/{optionKey}/archive`, `…/restore` → 204 (idempotent); `422 customization.last_active_option`, `422 customization.option_is_default`.
- `PUT /customization/fields/{id}/option-order` `{ keys: [ … ] }` → 204 (tüm seçenek anahtarlarını, arşivliler dahil, tam bir kez içermeli; aksi `400 validation`).

### Yerleşim (bölümler + sıra)
- `PUT /customization/entities/{entityType}/layout` (`manage`) → 204. Gövde (tümünü değiştirir):
  ```json
  { "expectedVersion": 7,
    "sections": [ { "key": "finance", "labels": { "tr": "Finans", "en": "Finance" }, "fieldKeys": ["credit_limit", "payment_terms"] } ],
    "unsectionedFieldKeys": ["risk_level"] }
  ```
  Kurallar: bölüm anahtarı `^[a-z][a-z0-9_]{0,39}$`, ≤ 10 bölüm, her bölümün `labels`ı en az bir dilde dolu; **her etkin alan anahtarı bölümlerde/`unsectionedFieldKeys`'te tam bir kez** geçer (arşivliler listelenmez; son bölümlerini korur, geri yüklenince bölümü yoksa bölümsüz olur); gövdede yer almayan mevcut bölüm silinir (o bölümdeki etkin alanlar zaten başka bir bölümde/`unsectionedFieldKeys`'te listelenmek zorundadır, yani alan kaybı olmaz). `expectedVersion` şema sürümüyle eşleşmezse **`409 customization.stale_layout`**; başarıda sürüm +1 ve tek `CustomFieldDefinitionChanged` (`layout_changed`).
- Bölümler ayrı CRUD ucu **yok** (tek uç: minimal).

### Olay (`Customization.Contracts`, K10; işlemle aynı `SaveChanges`'te Customization outbox'ına)
```csharp
public sealed record CustomFieldDefinitionChanged(Guid TenantId, string EntityType, string? FieldKey, string Change, int SchemaVersion, Guid? ActorUserId = null)
    : IntegrationEvent(TenantId, ActorUserId);   // Change ∈ field_created | field_updated | field_archived | field_restored | option_changed | layout_changed
```

## HTTP kontratı — mevcut uçlara değişiklikler (bağlayıcı, modül modül)

Ortak: **istek** gövdesine isteğe bağlı `customFields?: { "<key>": <değer> }` (D6 merge); **yanıt** DTO'suna `customFields: { … }` (detayda tüm etkin, listede `showInList`); `errors["customFields.<key>"]` / `errors["customFields"]`; liste uçları `cf.*` süzgeç/sıralama kabul eder; denetim farkı `customFields.<key>`. Var olan alanlar, izinler, hata kodları, durum makineleri **değişmez**.

| Modül | Uç | İstek değişikliği | Yanıt değişikliği | Liste (`cf.*`) | Notlar |
|---|---|---|---|---|---|
| **Sales** | `POST/PUT /accounts` | `AccountRequest` + `customFields` | `AccountDto` + `customFields` (detay ve liste) | `GET /accounts` | `GET /accounts/{id}/contacts|deals` alt listeleri `ContactDto`/`DealDto`'nun **liste projeksiyonunu** kullanır |
| | `POST/PUT /contacts` | `ContactRequest` + `customFields` | `ContactDto` + `customFields` | `GET /contacts` | |
| | `POST/PUT /leads` | `LeadRequest` + `customFields` | `LeadDto` + `customFields` | `GET /leads` | Dönüşmüş lead `PUT` → `409 lead.already_converted` (özel alan dahil değişmez); `POST /leads/{id}/convert` özel alan taşımaz/kopyalamaz |
| | `POST/PUT /deals` | `DealRequest` + `customFields` | `DealDto` + `customFields` | `GET /deals` | `POST /deals/{id}/stage` ve `GET /deals/board` değişmez |
| **Service** | `POST /cases`, `PUT /cases/{id}` | `CreateCaseRequest`, `UpdateCaseRequest` + `customFields` | `CaseDetailDto` + `customFields`; `CaseListItemDto` (liste projeksiyonu) | `GET /cases` (`slaState`, `status` vb. ile birlikte) | Durum/öncelik/atama/yorum uçları değişmez; `PUT` aktif olmayan talepte `case.not_active` özel alanı da kapsar; `xmin` çakışması mevcut davranış |
| **Commerce** | `POST/PUT /quotes` | `QuoteRequest` + `customFields` (yalnız `draft`; diğer durum `quote.not_editable`) | `QuoteDto` + `customFields`; `QuoteSummaryDto` (liste projeksiyonu) | `GET /quotes` (`status=expired` türetimi ile birlikte) | `POST /quotes/{id}/convert`: aynı `key`+`dataType` değerleri siparişe kopyalanır (madde 9), zorunluluk denetlenmez |
| | `POST/PUT /orders` | `OrderRequest` + `customFields` (yalnız `draft`) | `OrderDto` + `customFields`; `OrderSummaryDto` | `GET /orders` | Kalemler ve toplamlar değişmez |
| **Marketing** | `POST/PUT /campaigns` | `CreateCampaignRequest`, `UpdateCampaignRequest` + `customFields` | `CampaignDto` + `customFields` (detay ve liste) | `GET /campaigns` | Üyeler ve kayıt bazlı üyelik uçları değişmez |
| **Identity** | `GET /me`, `GET /permissions` | — | `permissions`/katalog `org.customization.manage` | — | `GET /permissions` +1 |
| **Platform** | `GET /subscription`; `/platform/organizations/{id}`, `/subscription`, `/plans` | `overrides.maxCustomFieldsPerEntity` | `limits.maxCustomFieldsPerEntity`; `usage.customFields` | — | Bkz. "Plan limiti" |
| **Audit** | `GET /audit?entityType=CustomFieldDefinition\|CustomFieldSection` | — | mevcut biçim | — | `manage` veya `org.audit.read` |

**Örnek — firma oluşturma:**
```json
POST /api/v1/accounts
{ "name": "Acme A.Ş.", "customFields": { "credit_limit": 5000.5, "risk_level": "low", "vip": true, "tags": ["b2b", "istanbul"], "renewal_date": "2026-12-31" } }
→ 201 { "id": "…", "name": "Acme A.Ş.", …, "customFields": { "credit_limit": 5000.5, "risk_level": "low", "vip": true, "tags": ["b2b","istanbul"], "renewal_date": "2026-12-31" } }
```
**Örnek — doğrulama hatası (400 `validation`):**
```json
{ "code": "validation", "errors": { "customFields.credit_limit": ["En çok 2 ondalık basamak girilebilir."], "customFields.risk_level": ["Zorunlu alan."], "customFields.old_field": ["Bu alan arşivlenmiş."] } }
```
**Örnek — güncelleme:** `PUT /accounts/{id}` gövdesinde `customFields: { "risk_level": null }` yalnız `risk_level`'i temizler; `customFields` hiç yoksa tüm özel değerler korunur.

## Hata kodları (metinler `SharedResource{,.en}.resx` tr/en; alan mesajları `errors` içinde çevrilir)

| code | HTTP | Ne zaman |
|---|---|---|
| `validation` | 400 | Girdi geçersiz. Varlık uçlarında `errors["customFields.<key>"]`/`["customFields"]`; liste uçlarında `errors["cf.<key>"]`/`["sort"]`; tanım uçlarında `errors["key"]`, `["labels"]`, `["constraints.<ad>"]`, `["options[i].key"]`, … |
| `forbidden` | 403 | Şema/tanım izni yok |
| `plan.module_disabled` | 403 | Türün modülü planda kapalı (`args.module`) |
| `plan.limit_exceeded` | 402 | Etkin alan limiti (`args`: `limit=customFields`, `entityType`, `max`, `used`) |
| `tenant.suspended` | 403 | Salt okunur/engelli kiracıda tanım/varlık yazımı |
| `customization.entity_type_unknown` | 404 | Kayıtsız/desteklenmeyen tür |
| `not_found` | 404 | Alan/kayıt yok veya başka kiracıya ait (varlık sızdırılmaz) |
| `customization.key_taken` | 409 | Alan anahtarı kullanımda (arşivliler dahil) |
| `customization.option_key_taken` | 409 | Seçenek anahtarı alanda kullanımda |
| `customization.field_archived` | 409 | Arşivli alan düzenlenemez (önce geri yükle) |
| `customization.not_a_select` | 409 | Seçenek ucu seçim olmayan alanda |
| `customization.stale_layout` | 409 | `expectedVersion` güncel değil (başkası değiştirdi) |
| `general.concurrency_conflict` | 409 | Tanım `xmin` çakışması (mevcut kod) |
| `customization.immutable_property` | 422 | `key`/`dataType`/`isSensitive` değiştirme girişimi (`args.property`) |
| `customization.hard_cap_reached` | 422 | Tür başına 200 tanım (arşivliler dahil) |
| `customization.list_columns_limit` / `customization.filterable_limit` | 422 | `showInList` ≤ 5 / `isFilterable` ≤ 10 |
| `customization.last_active_option` / `customization.option_is_default` | 422 | Son etkin seçenek / varsayılan seçenek arşivlenemez |

**Alan düzeyi (yalnız `errors` mesaj anahtarları, ayrı HTTP kodu yok):** `custom_field.required`, `.unknown`, `.archived`, `.type_mismatch`, `.too_short`/`.too_long` (`{minLength}`/`{maxLength}`), `.pattern_mismatch`, `.out_of_range` (`{min}`/`{max}`), `.not_integer`, `.too_many_decimals` (`{scale}`), `.invalid_date`, `.invalid_email`, `.invalid_url`, `.invalid_phone`, `.option_unknown`, `.option_archived`, `.too_many_selected` (`{maxSelections}`), `.payload_too_large`, süzgeç: `cf.unknown_field`, `cf.not_filterable`, `cf.not_sortable`, `cf.operator_not_supported`, `cf.bad_value`, `cf.too_many_filters`, `cf.too_many_values`, `cf.multiple_sorts`. Alan adları için `field.customFields`; izin adı `permission.org.customization.manage`.

## Web (React, Mantine 9; `web/**`)

Yeni bağımlılık: **`@dnd-kit/sortable`** (`@dnd-kit/core@6.3` ile uyumlu sürüm; `web/package.json` ve `pnpm-lock.yaml` sıcak dosya). Sürükleme yoksa/erişilemezse **yukarı/aşağı düğmeleri** (pipelines sayfası deseni; klavye ve dokunmatik) her zaman vardır.

Yeni dosyalar: `web/src/types/customization.ts` (+ `types/index.ts` dışa aktarımı, `PERMISSIONS.orgCustomizationManage`), `web/src/services/customization.service.ts`, `web/src/hooks/use-custom-fields.ts`, `web/src/lib/custom-fields.ts` (saf işlevler: varsayılan değerler, yük üretimi, istemci doğrulaması, biçimleme, `cf.` parametre adları, düzen indirgeyicisi), `web/src/components/customization/{custom-fields-form, custom-fields-panel, custom-field-input, custom-value, custom-columns, custom-filters, field-definition-dialog, constraints-editor, options-editor, sections-editor, field-list, schema-preview}.tsx`, `web/src/pages/settings/custom-fields.tsx`, i18n `web/public/locales/{tr,en}/customization.json`. Sıcak dosyalara **yalnız ekleme**: `App.tsx` (rota), `config/navigation.ts`, `i18n.ts` (ad alanı), `locales/*/navigation.json` (`customFields`), `web/README.md`, `package.json`/`pnpm-lock.yaml`.

- **Ayarlar → "Özel alanlar"** (`/app/settings/custom-fields`, `SlidersHorizontal` simgesi, `org.customization.manage`; yetkisiz `NoAccess`, menüde gizli; plan kapısı yok — çekirdek): üstte **varlık sekmeleri** (`GET /customization/entities`; modülü kapalı türlerin sekmesi gizli; sekme URL'de `?entity=account`). Sekme içinde: (1) kullanım göstergesi "12 / 30 etkin alan" (`limits.maxFields`; %80 turuncu, %100 kırmızı, sınırsız için sayaç) ve **"Yeni alan"** (limit dolu + `402` toast'ı + plan bağlantısı), (2) **alan listesi** bölümlere gruplu, sürükle-bırak (bölüm içi ve bölümler arası) + yukarı/aşağı düğmeleri; satır: etiket (dile göre), `key` (monospace), tip rozeti, rozetler (zorunlu, hassas, liste sütunu, süzgeç), arşivli soluk; satır eylemleri: Düzenle, Arşivle (onay)/Geri yükle; "Arşivlileri göster" anahtarı, (3) **bölümler**: bölüm ekle/yeniden adlandır/sil (yalnız boşsa), (4) sürükleme/sıra değişikliği yerel taslakta birikir, üstte **"Yerleşimi kaydet / Vazgeç"** çubuğu (`PUT …/layout` `expectedVersion` ile; `customization.stale_layout` → uyarı + şemayı yeniden çek; kaydetmeden çıkışta onay), (5) **Önizleme** düğmesi: mevcut taslak yerleşimi ve tanımlarıyla gerçek `CustomFieldsForm`'u (yalnız bellekte, kaydetmeden) diyalogda gösterir.
- **Alan diyaloğu** (`FieldDefinitionDialog`, oluştur/düzenle): önce **tip seçici** (oluştururken; düzenlemede kilitli + "tip değiştirilemez" ipucu); `key` (etiket `tr`'den otomatik önerilir, oluştururken düzenlenebilir, sonra kilitli; anlık desen doğrulaması); etiketler tr/en; yardım metni tr/en; **tipe uyarlanan kısıt paneli** (`ConstraintsEditor`: metin → min/maks uzunluk + isteğe bağlı desen ("basit desenler önerilir; sunucu doğrular"), tam sayı/ondalık → min/maks (+ ondalık: ölçek, birim), tarih → min/maks, çoklu seçim → en çok seçim, url/e-posta/telefon → yalnız uzunluk); varsayılan değer girdisi (tipe göre `CustomFieldInput`); zorunlu; hassas (yalnız oluştururken; "sonradan değiştirilemez" açıklaması); liste sütunu; süzgeç (tavanlar dolunca pasif + neden ipucu; uzun metinde ikisi de gizli); seçenek tipinde **`OptionsEditor`** (satır: `key` (oluştururken `tr` etiketinden otomatik, sonra kilitli), tr/en etiket, sürükle/yukarı-aşağı, arşivle/geri yükle, varsayılan işareti; son etkin seçenek arşivlenemez ipucu). Sağda tek alanlık **canlı önizleme**. Sunucu hataları alanlara eşlenir (`applyValidationErrors` + `constraints.*`, `options[i].key`); `customization.key_taken` → `key` alanı; `plan.limit_exceeded` → toast + bağlantı, diyalog açık kalır.
- **`CustomFieldsForm`** (yeniden kullanılabilir; `entityType`, RHF `control`, `mode: "create"|"edit"`, `values` (edit'te kaydın `customFields`'ı)): `useCustomFieldSchema(entityType)` ile şemayı çeker (react-query anahtarı `["customization","schema",entityType]`, `staleTime` 5 dk, tarayıcı ETag ile koşullu doğrular; tanım komutları ve giriş/organizasyon değişiminde geçersiz kılınır), bölümlere göre başlıklı iki sütunlu ızgarada (`longText` tam genişlik) tipe uygun bileşenleri çizer (`CustomFieldInput`: text/url/email/phone → `TextInput` (uygun `type`), longText → `Textarea`, number/decimal → `NumberInput` (`decimalScale`, birim `rightSection`), date → mevcut `DatePicker`, boolean → üç durumlu `SegmentedControl` ("—/Evet/Hayır"), singleSelect → `Select` (temizlenebilir), multiSelect → `MultiSelect`; **etiketler dil değişince** yeniden çözülür). Oluşturmada `defaultValue`'lar ön doldurulur. Arşivli seçenek yalnız kaydın mevcut değerindeyse "(arşiv)" ekiyle listelenir. Alan adı `customFields.<key>`; **istemci doğrulaması** (`validateCustomFields`: zorunlu, tip, uzunluk, aralık, ondalık, seçenek — `pattern` **çalıştırılmaz**) gönderimden önce çalışır, hatalar `setError("customFields.<key>")` ile alanın altına düşer. Şema yüklenirken yalnız bu bölüm iskelet gösterir; şema yoksa/`403`/`404`/modül kapalıysa bölüm **hiç çizilmez** ve çekirdek form etkilenmez (özel alan hatası çekirdek kaydı asla engellemez). Yük üretimi `toCustomFieldsPayload(schema, values)`: şemadaki **her** anahtar gönderilir, boşluk `null` (D6/madde 3), sayılar sayı, tarih `YYYY-MM-DD`, çoklu seçim dizi.
- **`CustomFieldsPanel`** (salt okunur): `record.customFields`'ı şemaya göre bölümlere gruplu `dt/dd` satırlarında gösterir (`InfoPanel` altında ayrı kart; boş değer `—`; boolean Evet/Hayır, çoklu seçim rozetler, tarih `formatCalendarDate`, ondalık birimiyle, url/e-posta bağlantı, seçenek **etiketi** (arşivli seçenek "(arşiv)"), hassas alanda değer görünür — D11); alanı olmayan kiracıda/şema yokken **render etmez**.
- **Mevcut ekranlara bağlama (tam liste):**
  - *Oluşturma/düzenleme diyalogları* (`FormDialog` gövdesinin sonuna `<CustomFieldsForm>`; `FormValues`'a `customFields`; gönderimde `toCustomFieldsPayload`; `FIELDS` listesine şemadan dinamik `customFields.<key>` eklenerek `applyValidationErrors`): `components/crm/{account,contact,lead,deal}-form-dialog.tsx` (`account|contact|lead|deal`), `components/service/case-form-dialog.tsx` (`case`), `components/marketing/campaign-form-dialog.tsx` (`campaign`).
  - *Editör sayfaları:* `components/commerce/document-editor.tsx` (teklif/sipariş ortak; başlık formunun altında "Özel alanlar" kartı; `kind` → `quote|order`).
  - *Detay bilgi panelleri* (`RecordDetailShell` `panel` alanında `InfoPanel` altına `<CustomFieldsPanel>`): `pages/crm/{account,contact,lead,deal,case,campaign}-detail.tsx`, `pages/commerce/{quote,order}-detail.tsx`.
  - *Listeler:* `pages/crm/{accounts,contacts,leads,deals,cases,campaigns}.tsx`, `pages/commerce/{quotes,orders}.tsx` — `useCustomColumns(entityType)` `showInList` alanları sabit sütunlardan sonra, "İşlemler"den önce ekler (`sortField: "cf.<key>"` yalnız `sortable` alanda; değer biçimleyicisi paneldekiyle ortak); `CustomFilters` (`ListPageFrame.filters` içinde "Özel alan filtreleri" `Popover`): `isFilterable` alanlardan seçim + tipe uyan girdi/işleç (metin `eq`/`contains`, sayı-tarih `gte/lte` aralığı, boolean üç durum, seçim `in`, çoklu seçim `in`/`contains`), **en çok 3** etkin süzgeç (dördüncüde ipucu), seçili süzgeçler kapatılabilir çip olarak görünür, "Temizle" `clearFilters`'a bağlı. **URL senkronu:** `cf.<key>[.<op>]` parametreleri URL'de; `use-list-params.ts`'e **ekleme**: `options.dynamicPrefixes?: string[]` (`["cf."]`) — önek taşıyan tüm parametreler `filters`'a ve `query`'ye geçer (şema yüklenmeden URL'den gelen süzgeç de sunucuya gider), `clearFilters`/`hasActiveFilters` bunları da kapsar; `sort=cf.<key>` mevcut `toggleSort` ile çalışır.
  - *Denetim:* `components/crm/record-audit-tab.tsx` + `lib/audit.ts`: `customFields.<key>` alan adı şemadaki etiketle gösterilir (şema yoksa/anahtar yetimse anahtar); `***` olduğu gibi.
  - *Plan ve kullanım* (`pages/settings/plan-usage.tsx`, `types/platform.ts`, `components/platform/subscription-editor-dialog.tsx`): "Özel alanlar" satırları (varlık başına kullanım çubuğu, `usage.customFields` / `limits.maxCustomFieldsPerEntity`) ve konsolda istisna alanı.
  - *Hata eşleme:* `lib/api-error.ts` arama listesine `customization:errors.<code>` eklenir (`plan.limit_exceeded` metni `entityType` argümanını da kullanır); `customization.stale_layout`, `customization.key_taken`, `customization.immutable_property` … çevirileri.
- **İzin/plan kapıları:** Ayarlar sayfası ve menü `org.customization.manage`; formda/panelde şema uçları çağıranın varlık okuma iznine bağlıdır (izin yoksa bölüm yok); salt okunur kiracıda yazma düğmeleri `usePermission` ile gizlenir ama `.manage` etkilenmez → ayar sayfasında yazma denenirse sunucu `tenant.suspended` döner (toast); modülü kapalı türün sekmesi/hiçbir isteği yok.
- **TR/EN:** tüm metinler `customization` ad alanında (tip adları, kısıt etiketleri, hata kodları, alan hata mesajı anahtarları — sunucu zaten çevrilmiş `errors` döndürür), `navigation.customFields`; **tr/en anahtar eşitliği testi**; `permission.org.customization.manage` sunucu resx'inde.

## Backend uygulama notları (dosya kapsamı)

- **Yeni:** `src/Modules/Customization/Sense.Crm.Modules.Customization.{Domain,Application,Contracts,Infrastructure,Api}`, `tests/Modules/Sense.Crm.Modules.Customization.Tests`, migration `InitialCustomization` (`dotnet dotnet-ef migrations add InitialCustomization --project src/Modules/Customization/Sense.Crm.Modules.Customization.Infrastructure --startup-project src/Sense.Crm.Migrator --context CustomizationDbContext -o Persistence/Migrations`).
  - **Domain:** `CustomFieldDefinition` (agregat; `Create/Update/Archive/Restore/AddOption/RenameOption/ArchiveOption/RestoreOption/ReorderOptions`, kural ihlalleri Result), `LayoutSection`, `EntitySchemaState`, **saf** `FieldValueEngine` (tip başına doğrulama/kanonikleştirme, `PatternGuard` NonBacktracking derleme + önbellek, birleştirme/varsayılan/zorunluluk algoritması) ve `FieldConstraintRules` — hepsi birim testli, altyapısız.
  - **Application:** komut/sorgular `CreateFieldDefinitionCommand`, `UpdateFieldDefinitionCommand`, `ArchiveFieldCommand`, `RestoreFieldCommand`, `AddOptionCommand`, `RenameOptionCommand`, `ArchiveOptionCommand`, `RestoreOptionCommand`, `ReorderOptionsCommand`, `SaveLayoutCommand` (hepsi `[RequiresPermission(org.customization.manage)]`), `GetSchemaQuery` (`[AnyAuthenticatedUser]` gerekçeli), `ListEntitiesQuery` (`manage`). Handler'lar `sealed`, `{Eylem}Handler`; FluentValidation doğrulaması yetkiden önce.
  - **Infrastructure:** `CustomFieldValidator` (`ICustomFieldValidator`), `CustomFieldSchemas` (`ICustomFieldSchemas`, HybridCache anahtarı `cf:schema:{tenantN}:{entityType}`, 60 sn, `tenant:{T}` etiketi), `CustomFieldQueryPlanner`, `CustomizationUsageReporter`, `CustomizationContractServices` (`AddCustomizationContractServices`; Worker da kullanır).
- **Değişen paylaşılan kod (uygulama sırasının 1. adımı):** `Shared.Kernel` (`CustomFieldValues`, `ICustomFieldsHolder`), `Shared.Contracts` (`CustomFields`, `Entitlements` ekleri), `Shared.Infrastructure` (dönüştürücü/karşılaştırıcı, `CustomFieldListQuery`, `CustomFieldDbFunctions`, `CustomFieldWriter`, `AuditLogInterceptor`, `AddCrmCore` varsayılanları), `Shared.Web` (`PagedQueryBinder`), Platform (plan/istisna/`LimitGuard`/DTO ekleri).
- **Varlık modülleri:** her modülde (Sales → Service → Commerce → Marketing): domain `ICustomFieldsHolder` + `Create(... custom)`, `*Configuration` `custom` eşlemesi + GIN indeksi, migration `AddCustomFields` (`--project src/Modules/<Modül>/Sense.Crm.Modules.<Modül>.Infrastructure --context <Modül>DbContext -o Persistence/Migrations`), komut/DTO alanları, `*Fields` doğrulayıcı arayüzlerine `CustomFields`, handler'da `CustomFieldWriter`, `*ReadStore`'da proje/planla/uygula, `Add<Modül>ContractServices`'te `CustomEntityRegistration`, `SalesAuditEntities` vb. değişmez.
- **Sıcak dosyalara yalnız ekleme:** `src/Sense.Crm.Api/ModuleCatalog.cs` (`new CustomizationModule()`), `src/Sense.Crm.Migrator/Program.cs`, `src/Sense.Crm.Worker/Program.cs`, `tests/Sense.Crm.Tests.Shared/Fixtures/TestFixture.cs` (Respawn `customization`), `SharedResource{,.en}.resx` (`customization.*`, `custom_field.*`, `cf.*`, `permission.org.customization.manage`, `field.customFields`), `docs/architecture/backend.md` (Customization bölümü + §5 hata tablosu + "M8D'de değişenler"), `docs/architecture/kararlar.md` (**K19**: özel alan depolama/kapsam/sınırlar), `docs/operations/runbook.md` (§9.1 şema önbelleği ≤ 60 sn bayatlık satırı; §8 yükseltmede `AddCustomFields` migration'ları + büyük tablolarda `CONCURRENTLY` notu), web sıcak dosyaları. `Identity.Contracts/Permissions.cs` ve `SystemRoleDefinitions.cs` **değişmez**. Yeni `packages.lock.json` (yeni 5 proje + test projesi; `dotnet restore Sense.Crm.slnx`, C-SEC L10).
- **Uygulama sırası (öneri):** (1) paylaşılan kod + Platform limit anahtarı + mimari testler; (2) Customization modülü (domain motoru → migration → handler'lar → denetleyiciler → şema önbelleği → `IUsageReporter`); (3) Dalga 1: Sales (4 tür) → Service; (4) Dalga 2: Commerce (teklif kopyası dahil) → Marketing; (5) test paketi; Web paralel, bu belgedeki JSON'a karşı sahte API ile (`CustomFieldsForm`/`Panel`/ayar sayfası → varlık bağlamaları). Dalga 2 kesilirse kayıtsız türler `404 customization.entity_type_unknown` döner, sözleşme değişmez.
- **Canlı duman testi (kart portu/DB):** API 5085, veritabanı `crm_m8d` (dev postgres `crm-postgres:15433`), Lead başka port atarsa o.

## Testler

**Backend — birim (`Sense.Crm.Modules.Customization.Tests`, altyapısız):**
- **Tip doğrulama matrisi (tablo testi, tip × {geçerli, sınır, geçersiz})**: her 11 tip için geçerli örnek, tam sınır (`maxLength`/`min`/`max`/`scale`/`maxSelections`/tarih yılı 1900 ve 2100), sınır ±1, yanlış JSON tipi (`"12"` sayı alanında, `1` boolean'da, `null` dizide, iç içe nesne), kesirli tam sayı, ondalık taşması (`scale`+1 basamak reddi, 15 anlamlı basamak sınırı), geçersiz takvim günü (`2026-02-30`), `javascript:` URL, e-posta/telefon biçimleri, kontrol karakteri, boş/kırpılmış metin = değer yok, CRLF→LF, çoklu seçimde tekilleştirme + `position` sırası + boş dizi = temizle.
- **Zorunlu/varsayılan/arşiv davranışı:** oluşturmada varsayılan uygulanır; açık `null` varsayılanı bastırır; güncellemede varsayılan **uygulanmaz**; zorunlu alan oluşturmada eksikse hata, güncellemede gönderilmemişse hata **yok**, gönderilip `null` ise hata; arşivli alan yazımı `custom_field.archived`, tanımsız `custom_field.unknown`; arşivli seçenek: mevcut değerle aynıysa kabul, farklıysa `option_archived`; çoklu seçimde karma; saklı ama istekte olmayan (yetim/arşivli) anahtarlar merge'de korunur; birleşik yük 16 KiB tavanı (tam sınır ve +1 bayt).
- **Tanım değişiklik kuralları:** `key` deseni ve benzersizlik (arşivliler dahil), `dataType`/`isSensitive`/`key` değişmezliği (`immutable_property`), kısıt beyaz listesi (bilinmeyen kısıt reddi), varsayılan değerin kısıtlara ve etkin seçeneğe uyması, seçenek anahtarı benzersizliği, son etkin seçenek/varsayılan seçenek arşiv kuralları, ≤ 100 seçenek, `showInList`/`isFilterable` tavanları ve `longText` reddi, `sortable` türetimi.
- **Regex güvenliği (D14):** `(a+)+$`, `(x+x+)+y`, `^(a|aa)+$` gibi klasik ReDoS kalıpları **kabul edilir** (NonBacktracking) ve 500 karakterlik saldırı girdisinde < 100 ms tamamlanır; geri başvuru/lookaround/atomik grup **tanım anında reddedilir**; desen > 200 karakter reddi; tam eşleşme (kısmi eşleşme geçmez); geçersiz desen `constraints.pattern` hatası.
- **`MapAsync` (teklif→sipariş):** aynı anahtar+tip kopyalanır; tip farklı/hedefte yok/arşivli/kısıta uymayan atlanır; zorunluluk denetlenmez.
- **Denetim maskeleme (birim):** `AuditLogInterceptor` düzleştirme: oluşturma/güncelleme/silme, yalnız `Custom` değişimi, aynı değer yazımı denetim üretmez, hassas anahtar `***` (eski ve yeni), yetim/bilinmeyen anahtar maskelenir, şema hatasında hepsi maskelenir.
- **Sorgu planlayıcı (`CustomFieldQueryPlanner`):** tip başına izinli/izinsiz işleç, `in`/değer sayısı > 20, süzgeç sayısı > 3, ikinci `cf.` sıralaması, sıralanamaz tip, `isFilterable`/`showInList` olmayan alan, arşivli alan, bozuk değer → hepsi `validation` hata anahtarlarıyla.

**Backend — Testcontainers HTTP:**
- **Tanım uçları:** oluştur/oku (şema ETag ve `If-None-Match` 304, sürüm her yazımda +1), güncelle, arşivle/geri yükle (idempotent), seçenek CRUD + sıra + arşiv, yerleşim (`expectedVersion` uyuşmazlığı `stale_layout`, her etkin alanın tam bir kez geçmesi, bölüm silme, arşivli alanın bölümünü koruması), şema `includeArchived` yalnız `manage`, izin matrisi (`manage` yokken tanım uçları 403; **doğrulama yetkiden önce**; şema ucu yalnız varlık okuma izniyle açılır: `crm.accounts.read` olan `account`'u görür, `case`'i görmez), `GET /customization/entities`, modülü kapalı tür `403 plan.module_disabled` (`starter` planı: `case`/`quote`/`order`/`campaign` kapalı), tanım denetimi (`GET /audit?entityType=CustomFieldDefinition`), `CustomFieldDefinitionChanged` outbox satırı (değişiklikte tam bir kayıt, no-op'ta yok, komut başarısızsa yok).
- **Plan limiti sınırları:** `maxCustomFieldsPerEntity = N`: N. alan geçer, N+1. `402 plan.limit_exceeded` (`args.entityType/max/used`); arşivleyince yer açılır; **geri yükleme** limitte 402; limit **tür başına** (account dolu, contact serbest); `0` = hiç alan açılamaz; `null`/yok = sınırsız ve **sıfır sayım sorgusu** (sorgu sayacı); kiracı istisnası (`overrides`) plandan önceliklidir, `null` istisna = sınırsız; plan düşürme mevcut alanları/değerleri silmez, yazım sürer, yeni alan bloklanır; **eşzamanlılık:** limitin 1 altındayken 20 paralel `POST fields` → tam **1** başarı, kalanı 402 (advisory kilit); teknik tavan 200 (`hard_cap_reached`); salt okunur kiracıda tanım yazımı `tenant.suspended`, şema ucu 200.
- **Her varlık için CRUD + özel alan (8 tür × şablon):** `customFields` ile oluştur → yanıt ve `GET` aynı; liste yalnız `showInList` anahtarlarını taşır; güncelle merge (bir anahtarı temizle, diğerine dokunma; `customFields` yok → hepsi korunur); hata yanıtında `errors["customFields.<key>"]` çoklu hata, çekirdek alan hatasıyla aynı yanıtta; arşivli/yetim anahtar yanıtta **yok** ama arşivden geri yükleyince **geri gelir**; oluşturmada varsayılan; zorunlu alan yokken eski istemci (alan bilmeyen `POST`) 400, güncellemede `PUT` (alan bilmeyen) **başarılı** ve değerleri korur (**geriye uyum**); mevcut satırlar (migration öncesi) `customFields: {}` döner ve güncellenebilir; başka kiracının alan anahtarı bu kiracıda `custom_field.unknown`.
- **Süzme/sıralama:** tip başına doğru sonuç (`eq`, `in`, `contains`, `gt/gte/lt/lte`, `isnull/notnull`, çoklu seçim `in`/`contains`, boolean üç durum, ondalık aralık, tarih aralığı), sayfalama + `totalCount` doğru, sıralama iki yönde **boşlar sonda** ve `Id` ile kararlı, sabit süzgeçlerle (`status`, `ownerUserId`, `q`) birleşim, `cf.` süzgeci `q`'yu bozmaz, **bozuk/karışık saklı değer sorguyu düşürmez** (SQL ile `custom` içine yanlış tipli değer yazılıp sıralanır/aralık süzülür → 200). **Enjeksiyon denemeleri:** `cf.x' OR '1'='1=…`, `cf.a;drop table…`, anahtarda `.`/`[`/`->>`/`'`/`"`/boşluk/çok uzun/`__proto__`/unicode, değerde `'`/`\`/`%`/`_`/`{"$ne":1}`/JSON parçası/` `, `sort=cf.x;select…` → hepsi `400 validation` **veya** düz metin olarak eşleşmeyen sonuç (500 asla, sorgu bütünlüğü bozulmaz); `ILIKE` joker kaçışı (`%` ve `_` düz metin); `in` 21 değer 400; 4 süzgeç 400; iki `cf.` sıralaması 400.
- **İndeks sağlığı:** 50.000 satırlı sentetik tabloda (`ANALYZE`, `SET LOCAL enable_seqscan = off`) `cf.risk_level=high` ve çoklu seçim `contains` için `EXPLAIN (FORMAT JSON)` planı `ix_<tablo>_custom` GIN üzerinde `Bitmap Index Scan` içerir; model indeksleri `(TenantId, …)` kuralını ve GIN tanımını (`jsonb_path_ops`) yansıtır (`Sense.Crm.Tests.TenantIsolation` + yeni model testi: her `ICustomFieldsHolder` agregatının tablosunda `custom jsonb` kolonu ve GIN indeksi var, her `CustomEntityRegistration` bir agregata bağlı).
- **Kiracı izolasyonu (zorunlu):** A'nın alan tanımları B'nin şemasında/listesinde/`entities` sayacında yok; B, A'nın alan `id`'siyle GET/PUT/archive/options/layout yaparsa 404; A ve B **aynı `key`**'i farklı tipte tanımlayabilir ve değerleri karışmaz; A'nın kaydı B'de 404 (özel alan yüklü olsa da); `cf.<A'nın anahtarı>` B'de `400 cf.unknown_field`; A'nın `customFields` değerleri B'nin liste/detay/denetiminde görünmez; şema önbelleği kiracı anahtarlı (A'nın değişikliği B'nin ETag'ını/sürümünü etkilemez, iki kiracı ayrı `version` sayar); `customization` tabloları `ITenantEntity` + `(tenant_id, …)` indeksli (mevcut model testleri otomatik).
- **Denetim (HTTP):** özel alan oluşturma/güncelleme/temizleme `GET /audit?entityType=Account&entityId=` içinde `customFields.<key>` farkıyla; yalnız özel alan değişince de `updated` kaydı; `isSensitive` alan `***` (hem `old` hem `new`), değeri **hiçbir yerde düz metin yazılmaz** (`audit_log_entries.changes` ham satır taraması); arşivlenmiş hassas alanın sonraki yazımı da maskeli; yetim anahtar maskeli.
- **KVKK imha (M7 testine ek):** iki kiracı (A silinecek, B kalacak); A'da 8 türün hepsinde özel alan değeri + tanım + bölüm + hassas değer; imha sonrası A için tüm `customization` tabloları ve tüm varlık tabloları 0 satır (yansımalı `ITenantEntity` sayımı zaten kapsar), B'nin satır sayıları ve `customFields` değerleri **birebir**; `audit_log_entries` A gitmiş; ikinci koşu no-op; şema önbelleği geçersiz.
- **Geriye uyum / regresyon:** mevcut tüm Sales/Service/Commerce/Marketing/Platform/Identity testleri yeni sütun/portlarla (Customization yüklü ve **yüklü değil** yapılandırma) değişmeden yeşil; DTO şekil testleri `customFields` eklenmiş halleriyle güncellenir; `GET /permissions` `org.customization.manage` içerir, Administrator alır, Standard almaz, eski kiracıya senkronlanır; `GET /subscription` `usage.customFields`/`limits.maxCustomFieldsPerEntity`; platform istisna doğrulaması (`overrides.maxCustomFieldsPerEntity` < 0 → 400).
- **Teklif→sipariş kopyası:** aynı anahtar+tip değerleri siparişe geçer, farklı tip atlanır, zorunlu sipariş alanı denetlenmez, dönüşüm atomikliği (kalem yazımı patlatılınca sipariş ve özel değerler kalıcı değil).
- **Lead dönüştürme:** özel alanlı lead dönüşür; firma/kişi/fırsat özel alansız açılır, zorunlu özel alanlar dönüşümü **engellemez**; dönüşmüş lead özel alanı güncellenemez (`lead.already_converted`).

**Mimari testler (`Sense.Crm.Tests.Architecture`, otomatik kapsar):** Customization yalnız `Shared.*`'a bağlı; hiçbir varlık modülü `Customization.*`'a bağlı değil; `Customization.*`'ı yalnız host'lar/testler referans eder; `RequestAuthorizationTests`: her Customization komut/sorgusu tam bir yetki özniteliği taşır, `GetSchemaQuery` `[AnyAuthenticatedUser]`, tanım komutları `org.customization.manage`, `Create*Command` limit kuralı (`[NoPlanLimit]` gerekçeli), `[TenantStatusExempt]` onaylı liste **değişmez**; `TenantFilterBypassInventoryTests` (IgnoreQueryFilters ve ham SQL envanteri) **değişmez**; mevcut her modülün `IUsageReporter`'ı testi `customization` için de geçer.

**Web (Vitest + Testing Library, kontrata karşı sahte API):**
- `lib/custom-fields`: `toCustomFieldsPayload` (her şema anahtarı; boşluk `null`; sayı/tarih/dizi tipleri; oluşturma-düzenleme farkı), `validateCustomFields` her tip için (zorunlu, uzunluk, aralık, ondalık, seçenek; **`pattern` çalıştırılmaz** — kasıtlı ReDoS'lu desenle test), varsayılan ön doldurma, `cf.` parametre adı üretimi, düzen indirgeyicisi (`moveField`, bölümler arası taşıma, bölüm silme kuralı), etiket çözümleme (dil değişimi, eksik dil geri dönüşü).
- **`CustomFieldsForm`:** her tip için doğru bileşen, bölüm başlıkları ve sıra, varsayılan ön doldurma yalnız oluşturmada, arşivli seçeneğin yalnız mevcut değerdeyse görünmesi, zorunlu/istemci hataları alan altında, sunucu `errors["customFields.<key>"]` alanlara eşlenir (`applyValidationErrors` dinamik alanlar), şema `403`/`404`/modül kapalı iken bölüm çizilmez ve çekirdek form kaydedilir, yük iskeleti; dil değişince etiketler değişir.
- **Varlık entegrasyonları (8 tür):** her oluşturma/düzenleme diyaloğu/editörü özel alan bölümünü gösterir ve isteğe `customFields` yükünü **kontrata uygun** ekler (boş alan `null`); detay panelinde `CustomFieldsPanel` doğru biçimlerde (boolean/çoklu/tarih/ondalık+birim/seçenek etiketi/`—`); alanı olmayan kiracıda panel ve bölüm **yok**; listede `showInList` sütunları, sıralanabilir sütun `cf.<key>` ile isteği değiştirir, süzgeç çipleri ve URL (`cf.<key>[.<op>]`) ↔ istek senkronu, 4. süzgeç engeli, "Temizle"; sayfa yenilemede URL'den gelen `cf.` süzgeci şema yüklenmeden isteğe girer (`dynamicPrefixes`).
- **Ayarlar sayfası:** sekmeler (kapalı modül sekmesi yok), alan listesi bölüm/sıra, yukarı/aşağı ile sıralama ve `PUT layout` yükü (`expectedVersion`, tam liste), taslak/Vazgeç/kaydetmeden çıkış onayı, `stale_layout` uyarısı, arşivle/geri yükle (onay), limit göstergesi ve dolu limitte `402` toast + bağlantı, önizleme diyaloğu, `NoAccess` (`manage` yokken) ve menü gizleme.
- **Alan diyaloğu:** tip seçimine göre kısıt paneli, oluştururken `key` önerisi ve desen doğrulaması, düzenlemede `key`/tip/hassaslık kilitli, seçenek editörü (ekle/arşivle/geri yükle/sıra/son etkin seçenek ipucu/varsayılan), tavan dolunca `showInList`/`isFilterable` pasif, sunucu hataları (`key_taken`, `constraints.*`, `options[i].key`, `immutable_property`) alanlara eşlenir, `plan.limit_exceeded` toast.
- **Denetim sekmesi:** `customFields.<key>` etiketle görünür, `***` korunur. **Plan ve kullanım:** özel alan çubukları/renk eşikleri. **TR/EN:** `customization.json` anahtar eşitliği. Kapı: `tsc`, `eslint`, `vitest`, `build` yeşil (yeni bağımlılık `@dnd-kit/sortable` kilit dosyasıyla).

## Merge kapısı (pano kuralı)
`dotnet build Sense.Crm.slnx --no-incremental` 0 uyarı, `dotnet format --verify-no-changes`, `dotnet test Sense.Crm.slnx` (yeni modül + mimari + kiracı izolasyon + mevcut modül regresyonları dahil), yeni `packages.lock.json`, web `tsc`/`eslint`/`vitest`/`build`, **canlı duman testi** (kart portu/DB): Migrator (`migrate` → `AddCustomFields` migration'ları, mevcut satırlar `custom = '{}'`) → yönetici Ayarlar → Özel alanlar'da firma için `credit_limit` (ondalık, liste sütunu + süzgeç), `risk_level` (tekli seçim, hassas olmayan), `tc_no` (metin, **hassas**) tanımlar → firma oluşturma diyaloğunda alanlar görünür, zorunlu/aralık hatası alan altında → firma detay panelinde değerler → listede sütun + `cf.` süzgeci + sıralama → denetim sekmesinde `tc_no` = `***`, diğerleri açık → alan arşivle/geri yükle (değerler geri gelir) → seçenek arşivle (eski değer görünür, yenisi seçilemez) → `starter` planında 6. alan `402` → teklif→sipariş dönüşümünde aynı anahtarlı alanın kopyalanması → Worker imhası sonrası tablolarda sıfır satır.

## Kapsam dışı
Özel modül/varlık tanımı (tanımlı türlerin dışında yeni "özel nesne"), formül/toplama (rollup)/otomatik numara alanları, lookup/ilişki alanı, çok para birimli `currency` tipi (ondalık + `unit` ile karşılanır), `datetime` tipi, dosya/görsel alanı, kişi/kullanıcı seçici alanı, alan başına ilişkili alan bağımlılığı (bağımlı seçim listesi), **koşullu görünürlük/zorunluluk kuralları**, sürükle-bırak **yerleşim oluşturucu** (kolon/satır/çok sayfalı düzen; yalnız bölüm + sıra), **alan düzeyi rol izni** (okuma/yazma başına; D11), sayfa düzeni profilleri (rol/pipeline başına farklı form), alan bazlı **değişiklik geçmişi arayüzü** (denetim sekmesi mevcut biçimde), **içe/dışa aktarma eşleme** ve CSV (M3 rapor CSV'leri özel alan içermez; D12), **rapor/dashboard boyutu** olarak özel alan, lead dönüştürmede alan eşleme (Zoho "field mapping"; lead→firma/kişi/fırsat), workflow kural koşullarında/görevlerinde özel alan, `q` serbest aramasında özel alan, `product`/`activity`/kalem/üye/yorum/pipeline üzerinde özel alan, alan başına dinamik veritabanı indeksi (operasyon işi), otomatik toplu geriye dönük varsayılan yazımı (backfill), alanın kalıcı silinmesi ve anahtar yeniden kullanımı, `isSensitive` sonradan değiştirme ve geçmiş denetim satırlarında geriye dönük redaksiyon, kiracılar arası alan şablonu/paketi paylaşımı, alan değişikliği bildirimi/e-posta, tr/en dışındaki dillerde etiket çevirisi (yalnız tr/en).
