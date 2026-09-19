# Milestone 6A — Ticaret: Ürünler, Teklifler, Satış Siparişleri (plan + HTTP kontratı)

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Kararlar: [kararlar.md](../architecture/kararlar.md) · Önceki: [M3](m3-aktivite-rapor.md), [M4](m4-workflow.md) · Pano kartı: `C-M6A` (dal `m6/commerce`). Biçim kuralları M2/M3/M4 ile aynı: taban yol `/api/v1`, JSON camelCase, enum'lar camelCase string, `null` alanlar yazılmaz, hatalar ProblemDetails + `code` (+ doğrulamada `errors`), sayfalı liste `{ items, page, pageSize, totalCount }` (`page` 1'den, `pageSize` varsayılan 25 / en çok 100), `sort` = `alan` veya `-alan` (bilinmeyen alan yok sayılır, her zaman `Id` ile kararlı), `q` = `ILIKE` + kaçışlı parametre. Tarih (gün) alanları `YYYY-MM-DD`, zaman damgaları ISO 8601 UTC. Tutarlar JSON sayı (decimal).

**Çıktı:** Kullanıcı ürün kataloğunu tutar; bir firmaya (isteğe bağlı kişi ve fırsata bağlı) kalemli, KDV/iskontolu teklif hazırlar, gönderir, kabul/ret sonucunu işler; kabul edilen teklifi tek tıkla, tek transaction'da satış siparişine dönüştürür (veya doğrudan sipariş açar); siparişi onaylar, teslim eder veya iptal eder. Teklif numarası `Q-2026-0001`, sipariş numarası `SO-2026-0001` (kiracı + yıl bazında, eşzamanlılığa dayanıklı). Raporlar sayfasında ticaret özeti vardır.

## Modül kararı
- Yeni modül `Crm.Modules.Commerce` (`./build/new-module.ps1 -Name Commerce`; şema `commerce`; tek `CommerceDbContext`; migration `InitialCommerce`). Ürün, teklif, sipariş ve numara sayacı aynı işlem sınırında (teklif→sipariş dönüşümü, numara + kayıt) yazıldığı için **tek modüldedir**.
- Diğer modüllere yalnız `*.Contracts` ile konuşur: `Sales.Contracts` (`IRecordLookup` — firma/kişi/fırsat varlığı + görünen ad; yeni `IRecordRelationLookup` — aşağıda), `Identity.Contracts` (`IMemberLookup`: aktif üye + ad; `ITenantDirectory`/`TenantCalendarService`: kiracı saat dilimi, "bugün", yıl, rapor aralığı). Mimari testler (NetArchTest) bu sınırı zorlar.
- Sales'in şemasına/tablolarına doğrudan erişim yok; fırsat aşaması **otomatik değişmez** (teklif kabulü fırsatı kazanılmış yapmaz — bilinçli, kapsam dışı).
- Raporlar (`GET /reports/commerce/summary`) Commerce modülündedir (kendi verisi; Ayrı Reporting modülü yok, M3 kararı geçerli).

### Contracts eklemeleri (tam liste)
1. **`Sales.Contracts`** (Sales uygular, `AddSalesContractServices()` içinde kaydolur; kiracı + yumuşak silme filtresi altında, başka kiracı/silinmiş → `null`):
   ```csharp
   public sealed record ContactLink(Guid Id, string FullName, Guid? AccountId);
   public sealed record DealLink(Guid Id, string Name, Guid AccountId, Guid? ContactId, string Currency);
   public interface IRecordRelationLookup
   {
       Task<ContactLink?> GetContactAsync(Guid contactId, CancellationToken ct = default);
       Task<DealLink?> GetDealAsync(Guid dealId, CancellationToken ct = default);
   }
   ```
   Amaç: teklif/sipariş tutarlılık kuralları (kişi firmaya, fırsat firmaya uymalı). Firma varlığı/adı mevcut `IRecordLookup` ile.
2. **`Commerce.Contracts`** (yeni): `CommercePermissions` (`Module = "commerce"`, `Group = "crm"`, `All` altı anahtar), `CommerceAuditEntities` (`Product`, `Quote`, `SalesOrder` → okuma izinleri), integration event'ler `QuoteAccepted`, `SalesOrderCreated` (aşağıda).
3. **`Identity.Contracts`:** değişiklik yok (`IMemberLookup`, `ITenantDirectory`, `TenantCalendarService`, `IAuditEntityPermissions` mevcut).
4. **`Kernel`/`Shared.*`:** değişiklik yok (yuvarlama saf domain sınıfı `DocumentTotals` Commerce.Domain'dedir).

## İzinler
`CommercePermissions`: `crm.products.read`, `crm.products.write`, `crm.quotes.read`, `crm.quotes.write`, `crm.orders.read`, `crm.orders.write` (grup `crm`). `IModule.Permissions` ile kataloğa katılır → `GET /permissions` 18 → **24 anahtar** (mevcut testlerdeki "18" beklentileri güncellenir).
- **`SystemRoleDefinitions` değişmez:** Administrator = kataloğun tümü; Standard = tüm `crm.*` (yalnız `crm.approvals.decide` hariç) + `org.users.read` kuralı prefix tabanlıdır, altı yeni anahtarı otomatik verir. Mevcut organizasyonlara `SystemRolePermissionSynchronizer` API açılışında yayar (test: yeni anahtarlar eski kiracının Standard rolünde de görünür).
- Eşleme: okuma uçları `*.read`; oluştur/güncelle/sil/durum geçişi `*.write`; **teklif→sipariş dönüşümü** `crm.quotes.read` + `crm.orders.write`; kayıt bazlı denetim `GET /audit?entityType=Product|Quote|SalesOrder` ilgili `*.read` ile (`org.audit.read` de geçer); ticaret raporu `crm.reports.read`.
- Doğrulama yetkiden önce çalışır (M2 kuralı: geçersiz gövdeli yetkisiz istek 400 alır).

## Ortak kurallar (teklif + sipariş)

### Alanlar (belge başlığı)
`subject*` (≤200), `accountId*`, `contactId?`, `dealId?`, `ownerUserId` (verilmezse çağıran; kiracının aktif üyesi olmalı → `owner.not_member` 400; sahibi değişmeyen kayıtta yeniden sorgulanmaz), `currency` (ISO-4217, 3 harf, büyük harfe çevrilir, varsayılan `TRY`), `terms?` (≤4000), `notes?` (≤2000), `lines[]`.

**Bağlı kayıt doğrulaması:** `accountId` `IRecordLookup` ile; yok/silinmiş/başka kiracı → **404 `commerce.related_not_found`** (varlık sızdırılmaz; M3 ile aynı). `contactId`/`dealId` `IRecordRelationLookup` ile aynı kural; ek tutarlılık: kişinin firması varsa `contact.accountId == accountId` (`commerce.contact_account_mismatch` 400), `deal.accountId == accountId` (`commerce.deal_account_mismatch` 400). Yanıtta `accountName`, `contactName?`, `dealName?`, `ownerName` çözülür (silinmiş bağlı kayıtta ad boş döner; belge kalır).

### Kalem (`lines[i]`)
İstek: `{ productId?, description*, quantity, unitPrice, discountPercent, taxRate }`.
| Alan | Kural |
|---|---|
| `productId?` | Yumuşak bağ + **anlık görüntü**: kalem ürünün değerlerini kopyalar, sonradan ürün değişse/silinse belge değişmez. Ürün kiracıda yoksa `validation` (`errors["lines[i].productId"]`). Ürün pasifse **belgede daha önce olmayan** `productId` için `validation` (pasif ürün yeni kaleme eklenemez; mevcut kalem korunur). Ürün para birimi belge para birimiyle aynı olmalı, aksi `validation` (`lines[i].productId`) — kur çevrimi yok |
| `description*` | ≤500; arayüz ürün seçilince ürün adıyla doldurur |
| `quantity` | > 0, ≤ 1.000.000, en çok 4 ondalık |
| `unitPrice` | ≥ 0, ≤ 1.000.000.000, en çok 4 ondalık (KDV hariç birim fiyat) |
| `discountPercent` | 0–100, en çok 2 ondalık, varsayılan 0 (kalem bazlı iskonto; belge düzeyi iskonto yok) |
| `taxRate` | 0–100, en çok 2 ondalık, varsayılan 0 (KDV yüzdesi; fiyatlar KDV hariç) |
En çok 100 kalem. Sıra = dizi sırası (`position` 0'dan; yanıtta `id`, `position` ve hesaplanan alanlar döner). Kalem alanı hataları `errors` anahtarı `lines[3].quantity` biçimindedir (camelCase, indeksli). Gövdedeki hesaplanan alanlar (`lineTotal`, `grandTotal` vb.) **yok sayılır**.

### Toplam hesabı (sunucu belirleyici; `DocumentTotals`, saf domain, birim testli)
Her yazmada (oluştur/güncelle/dönüştür) baştan hesaplanır ve saklanır; `MidpointRounding.AwayFromZero`, **2 ondalık**, **kalem bazında yuvarlanır**, belge toplamları yuvarlanmış kalem değerlerinin toplamıdır (böylece ekranda satırlar toplama tam uyar):
```
lineSubtotal = Round(quantity × unitPrice, 2)
lineDiscount = Round(lineSubtotal × discountPercent / 100, 2)
net          = lineSubtotal − lineDiscount
lineTax      = Round(net × taxRate / 100, 2)
lineTotal    = net + lineTax

subtotal      = Σ lineSubtotal          (brüt, iskonto öncesi, KDV hariç)
discountTotal = Σ lineDiscount
taxTotal      = Σ lineTax
grandTotal    = subtotal − discountTotal + taxTotal   (= Σ lineTotal, değişmez kural)
```
Kalemsiz belge: hepsi 0. Değişmez: `grandTotal == Σ lineTotal`. **Referans vektörler** (backend birim testi ve web `computeTotals` testi aynı tabloyu kullanır):
| # | qty | unitPrice | disc % | tax % | lineSubtotal | lineDiscount | lineTax | lineTotal |
|---|---|---|---|---|---|---|---|---|
| 1 | 3 | 19.99 | 10 | 20 | 59.97 | 6.00 (5.997) | 10.79 (10.794) | 64.76 |
| 2 | 1 | 0.05 | 50 | 20 | 0.05 | 0.03 (0.025 → yarım yukarı) | 0.00 (0.004) | 0.02 |
| 3 | 2.5 | 10.10 | 0 | 18 | 25.25 | 0.00 | 4.55 (4.545 → yarım yukarı) | 29.80 |
| 4 | 1 | 100.00 | 100 | 20 | 100.00 | 100.00 | 0.00 | 0.00 |
Belge {1,3}: `subtotal 85.22`, `discountTotal 6.00`, `taxTotal 15.34`, `grandTotal 94.56` (= 64.76 + 29.80).
Saklama: birim fiyat `decimal(18,4)`, adet `decimal(18,4)`, yüzdeler `decimal(5,2)`, kalem ve belge tutarları `decimal(18,2)`.

**Web önizlemesi:** `web/src/lib/commerce-totals.ts` aynı algoritmayı **BigInt ölçekli tamsayıyla** uygular (adet ve fiyat 1e4 ölçekli, yüzdeler 1e2 ölçekli; yarım yukarı yuvarlama `(2n·a + d) / (2n·d)`), böylece kayan nokta sapması olmaz; yeni bağımlılık eklenmez. Önizleme yalnız gösterimdir; kayıttan dönen sunucu değerleri ekrana esastır.

### Numaralandırma (kiracı + tür + yıl, eşzamanlılığa dayanıklı)
- Numara: teklif `Q-{yıl}-{sıra:D4}`, sipariş `SO-{yıl}-{sıra:D4}` (sıra 9999'u aşarsa doğal uzar: `Q-2026-10000`). **Yıl, oluşturma anında kiracının saat diliminde** hesaplanır (`TenantCalendarService`; Europe/Istanbul'da 2026-12-31T21:30Z = 2027-01-01 → `…-2027-0001`). Yıl dönümünde sıra 0001'den başlar. Numara değişmez; silinmiş belgenin numarası yeniden kullanılmaz (boşluk yalnız silmede oluşur, iptal/ret'te yok).
- Tablo `commerce.document_counters (tenant_id, kind ('quote'|'order'), year, last_value)`, PK `(tenant_id, kind, year)` (`ITenantEntity`; kiracı filtresi + indeks kuralına uyar; yalnız ham SQL ile yazılır, `TenantId` her zaman `ITenantContext`ten açıkça parametre olarak verilir).
- Ayırma, belge INSERT'iyle **aynı veritabanı transaction'ında** tek atomik ifadeyle yapılır:
  ```sql
  INSERT INTO commerce.document_counters (tenant_id, kind, year, last_value)
  VALUES (@tenant, @kind, @year, 1)
  ON CONFLICT (tenant_id, kind, year)
  DO UPDATE SET last_value = document_counters.last_value + 1
  RETURNING last_value;
  ```
  Satır kilidi transaction bitene kadar tutulur → eşzamanlı oluşturmalar (aynı kiracı+tür+yıl) sıraya girer, aynı numara çıkmaz; transaction geri alınırsa sayaç da geri döner (**boşluksuz**). Farklı kiracı/tür/yıl birbirini kilitlemez. Yedek güvence: `(tenant_id, number)` **benzersiz indeks** (silinmişler dahil, filtre yok). Handler `BeginTransaction` ile ayırma + `SaveChanges` + outbox yazımını tek transaction'da yapar (UnitOfWork davranışı zaten açtıysa onu kullanır).
- Dönüşüm ve doğrudan sipariş oluşturma da aynı yolla `order` sayacını kullanır.

### Denetim, olaylar, hassas alan
- `Product`, `Quote`, `SalesOrder` `TenantAggregateRoot` + `IAuditLogged` + yumuşak silinen; kimlikler `Guid.CreateVersion7`, `ValueGeneratedNever`. Kalemler (`QuoteLine`, `SalesOrderLine`) başlığın çocuk tablolarıdır: `ITenantEntity` (TenantId ebeveynden atanır, filtre + `(tenant_id, …)` indeks), **`IAuditLogged` değil** (gürültü olmasın); kalem değişikliği denetimde başlığın `subtotal/discountTotal/taxTotal/grandTotal` farkıyla görünür. Enum alanları camelCase string yazılır. `EntityType` = `Product|Quote|SalesOrder`. **Hassas (maskelenen) alan yok** (kişisel veri tutulmaz).
- Integration event'ler `Commerce.Contracts`'ta, `IIntegrationEventOutbox.Enqueue` ile işlemle aynı `SaveChanges`'te Commerce outbox'ına yazılır (K10); Worker'a `OutboxPollingService<CommerceDbContext>` eklenir. M6A'da tüketici yoktur (M4 workflow/M7 sonraki kartlar dinler); testler outbox satırını doğrular.
  ```csharp
  public sealed record QuoteAccepted(Guid TenantId, Guid QuoteId, string Number, Guid AccountId, Guid? DealId,
      decimal GrandTotal, string Currency, Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);
  public sealed record SalesOrderCreated(Guid TenantId, Guid OrderId, string Number, Guid AccountId, Guid? DealId,
      Guid? QuoteId, decimal GrandTotal, string Currency, string Source /* "quote" | "direct" */, Guid? ActorUserId = null)
      : IntegrationEvent(TenantId, ActorUserId);
  ```
- **Eşzamanlılık:** `Quote` ve `SalesOrder` üzerinde Npgsql `xmin` eşzamanlılık belirteci; durum geçişlerinde/güncellemede çakışma → **409 `commerce.concurrent_update`** (aynı teklifi iki kez "kabul" edip `QuoteAccepted`'ı çiftlemek engellenir).

## Ürünler `/products`
Alanlar: `name*` (≤200), `code?` (SKU, ≤64, kırpılır), `description?` (≤2000), `unitPrice` (≥0, ≤1e9, ≤4 ondalık; varsayılan 0), `currency` (varsayılan `TRY`), `taxRate` (0–100, ≤2 ondalık; **API varsayılanı 0**, web formu 20 önerir), `unit?` (≤32, serbest metin; ör. adet, saat, lisans), `isActive` (varsayılan true).
- `code` kiracıda benzersiz, **büyük/küçük harf duyarsız**, silinmemişler arasında (`code_normalized` = `ToUpperInvariant` kolonu + `(tenant_id, code_normalized) WHERE is_deleted = false AND code_normalized IS NOT NULL` benzersiz indeks); ihlal → **409 `product.code_taken`**. `code` boşsa null saklanır (benzersizlik dışı).
- `GET /products?q&isActive&currency&sort&page&pageSize` → paged. `q` ad + kod + açıklama. `sort`: `name` (varsayılan artan), `code`, `unitPrice`, `createdAt`, `updatedAt`. Kalem seçici için aynı uç kullanılır (`isActive=true&q=…&pageSize=20`).
- `GET /products/{id}`, `POST /products` → 201 + `Location` + gövde, `PUT /products/{id}` → 204 (tam değiştirme; gönderilmeyen isteğe bağlı alan temizlenir; `isActive` verilmezse mevcut korunur), `DELETE /products/{id}` → 204 (yumuşak; belgelerdeki kalemler anlık görüntü olduğundan silme her zaman serbest, silinen ürünün kodu yeniden kullanılabilir).
- Yanıt: `{ id, name, code?, description?, unitPrice, currency, taxRate, unit?, isActive, createdAt, updatedAt? }`.

## Teklifler `/quotes`
Ek alanlar: `validUntil?` (tarih; boşsa süresiz), `status`, `sentAt?`, `acceptedAt?`, `rejectedAt?`, `rejectionReason?` (≤1000), `convertedOrderId?` + `convertedOrderNumber?` (türetilir: teklife bağlı silinmemiş sipariş), oluşturma/güncelleme zamanı. `status` **etkin durumdur** (aşağıda `expired` türetilir).

### Durum makinesi (saklanan durumlar `draft|sent|accepted|rejected`; `expired` yalnız türetilir)
| Eylem (uç) | Nereden | Nereye | Kurallar |
|---|---|---|---|
| `send` | `draft` | `sent` | ≥1 kalem (`quote.no_lines` 422); `validUntil` doluysa bugünden (kiracı saati) önce olamaz (`validation`, `errors.validUntil`); `sentAt` yazılır |
| `accept` | `sent` (süresi dolmamış) | `accepted` | Süresi dolmuşsa `quote.expired` 409; `acceptedAt`; `QuoteAccepted` event |
| `reject` `{ reason? }` | `sent` (süresi dolmuş olsa da) | `rejected` | `rejectedAt`, `rejectionReason` |
| `revert` | `sent`, `rejected` (etkin `expired` dahil) | `draft` | Yeniden düzenlemek için; `sentAt/rejectedAt/rejectionReason` temizlenir |
| `extend` `{ validUntil }` | `sent` (etkin `expired` dahil) | `sent` | Yeni tarih bugünden önce olamaz (`validation`); süresi dolmuş teklifi tekrar geçerli yapar |
| `PUT`, `DELETE` | yalnız `draft` | — | Diğer durumlarda `quote.not_editable` 409 |
Tanımsız her geçiş (ör. `draft`'a `accept`, `accepted`'a `revert`) → **`quote.invalid_transition` 409** (`from`, `to` argümanlı). `accepted` **uçtur** (kilitli; siparişe dönüşebilir). Tekrar aynı eylem (ör. ikinci `accept`) 409'dur (idempotent değil, olay çiftlenmesin).
- **Süre dolumu (expired):** zamanlanmış iş yok; **türetilir**: `status = sent` **ve** `validUntil` dolu **ve** `validUntil < bugün (kiracı saat dilimi)` ⇒ yanıtlarda, liste filtresinde ve raporlarda `status: "expired"`. Saklanan değer `sent` kalır (tek yerde tanımlı `QuoteStatusExpression`: liste filtresi, rapor ve DTO eşlemesi aynısını kullanır). `draft`, `accepted`, `rejected` asla süresi dolmuş sayılmaz. `expired` teklifte `accept` engellenir; `revert` veya `extend` ile kurtarılır.

### Uçlar
- `GET /quotes?q&status&accountId&contactId&dealId&ownerUserId&validFrom&validTo&converted&sort&page&pageSize` → paged özet. `q`: numara + konu. `status`: `draft|sent|accepted|rejected|expired` (etkin durum; `sent` süresi dolmamışları döner). `validFrom/validTo` tarih, uçlar dahil (`validUntil` boş olanlar dışarıda). `converted=true|false` (siparişe dönüşmüş/dönüşmemiş). `sort`: `number`, `subject`, `grandTotal`, `validUntil` (**boş `validUntil` her iki yönde de sonda**), `createdAt`; varsayılan `-createdAt`.
  Özet satır: `{ id, number, subject, status, accountId, accountName, contactId?, contactName?, dealId?, dealName?, ownerUserId, ownerName, currency, grandTotal, validUntil?, convertedOrderId?, createdAt }` (adlar `IRecordLookup.GetDisplayNamesAsync` toplu, tür başına tek sorgu).
- `GET /quotes/{id}` → özet alanları + `subtotal, discountTotal, taxTotal, terms?, notes?, sentAt?, acceptedAt?, rejectedAt?, rejectionReason?, convertedOrderNumber?, updatedAt?, lines: [{ id, position, productId?, description, quantity, unitPrice, discountPercent, taxRate, lineSubtotal, lineDiscount, lineTax, lineTotal }]`.
- `POST /quotes` → 201 + `Location` + detay gövdesi; her zaman `draft` başlar (numara burada atanır). Gövde:
  ```json
  { "subject": "Yıllık lisans teklifi", "accountId": "…", "contactId": null, "dealId": "…",
    "validUntil": "2026-10-19", "ownerUserId": null, "currency": "TRY", "terms": "…", "notes": "…",
    "lines": [ { "productId": "…", "description": "CRM Pro lisansı", "quantity": 2, "unitPrice": 100, "discountPercent": 0, "taxRate": 20 } ] }
  ```
- `PUT /quotes/{id}` → 204: tam değiştirme (yalnız `draft`; kalem kümesi tümden değişir, gönderilmeyen isteğe bağlı alan temizlenir, `ownerUserId` verilmezse mevcut korunur, numara/durum değişmez).
- `DELETE /quotes/{id}` → 204 (yalnız `draft`, yumuşak).
- `POST /quotes/{id}/send`, `/accept`, `/revert` → 204; `POST /quotes/{id}/reject` `{ reason? }` → 204; `POST /quotes/{id}/extend` `{ validUntil }` → 204.
- `POST /quotes/{id}/convert` → **201** + `Location: /api/v1/orders/{orderId}` + sipariş detay gövdesi (bkz. Siparişler). İzin `crm.quotes.read` + `crm.orders.write`.
- Denetim: `GET /audit?entityType=Quote&entityId=` (`crm.quotes.read`).

## Satış siparişleri `/orders`
Ek alanlar: `number` (`SO-…`), `status`, `orderDate` (tarih; verilmezse bugün — kiracı saati), `quoteId?` + `quoteNumber?`, `fulfilledAt?`, `cancelledAt?`, `cancelReason?` (≤1000). Başlık/kalem/toplam kuralları teklifle aynıdır (`validUntil` ve `sentAt` yok).

### Durum makinesi (`draft|confirmed|fulfilled|cancelled`)
| Eylem (uç) | Nereden | Nereye | Kurallar |
|---|---|---|---|
| `confirm` | `draft` | `confirmed` | ≥1 kalem (`order.no_lines` 422) |
| `fulfill` | `confirmed` | `fulfilled` | `fulfilledAt` yazılır (stok/sevkiyat yok) |
| `cancel` `{ reason? }` | `draft`, `confirmed` | `cancelled` | `cancelledAt`, `cancelReason` |
| `PUT`, `DELETE` | yalnız `draft` | — | Aksi `order.not_editable` 409 |
`fulfilled` ve `cancelled` **uçtur**. Tanımsız geçiş → **`order.invalid_transition` 409** (`from`, `to`). Siparişe dönüşmüş taslak sipariş (`quoteId` dolu) düzenlenebilir ve silinebilir; silinirse teklif yeniden dönüştürülebilir hâle gelir (aşağıya bakın).

### Teklif → sipariş dönüşümü (`POST /quotes/{id}/convert`, tek transaction)
1. Teklif yüklenir (kiracı filtresi; yok → 404). `accepted` değilse **`quote.not_accepted` 409**. Bağlı, silinmemiş sipariş varsa **`quote.already_converted` 409**. Firma hâlâ var mı yeniden doğrulanır (`commerce.related_not_found` 404; kişi/fırsat silinmişse ilgili alan boş bırakılmaz, aynı hata).
2. Tek `SaveChanges`/transaction içinde: `order` sayacından numara ayrılır, sipariş `draft` olarak yazılır (`subject`, `accountId`, `contactId`, `dealId`, `currency`, `ownerUserId`, `terms`, `notes` tekliften; `orderDate` = bugün; `quoteId` = teklif), **kalemler yeni kimliklerle kopyalanır ve `DocumentTotals` ile yeniden hesaplanır** (tutarlar tekliften birebir aynı çıkar), `SalesOrderCreated` (`source: "quote"`) outbox'a yazılır. Teklif durumu `accepted` kalır; dönüştürüldüğü `convertedOrderId` (siparişe göre türetilir) ile görünür.
3. **Çift dönüşüm engeli (kesin):** `sales_orders` üzerinde `(tenant_id, quote_id) WHERE quote_id IS NOT NULL AND is_deleted = false` **benzersiz indeks**. İki eşzamanlı dönüşümde ikincisi indeks ihlaliyle düşer (transaction geri alınır, sayaç da geri döner) ve handler ihlali yakalayıp **`quote.already_converted` 409** döner; asla iki sipariş, asla numara boşluğu oluşmaz.
4. Herhangi bir adımda hata → hiçbir şey kalıcı olmaz (sipariş, kalem, sayaç, outbox olayı birlikte geri alınır). İptal edilmiş sipariş silinemediği (yalnız `draft` silinir) için iptal edilmiş siparişi olan teklif yeniden dönüştürülemez (bilinçli, YAGNI; kullanıcı doğrudan sipariş açar). Yalnız `draft` siparişin silinmesi teklifi yeniden dönüştürülebilir yapar.

### Uçlar
- `GET /orders?q&status&accountId&contactId&dealId&quoteId&ownerUserId&orderFrom&orderTo&sort&page&pageSize` → paged özet. `q`: numara + konu. `orderFrom/orderTo` tarih (`orderDate`), uçlar dahil. `sort`: `number`, `subject`, `grandTotal`, `orderDate`, `createdAt`; varsayılan `-createdAt`. Özet: `{ id, number, subject, status, accountId, accountName, contactId?, contactName?, dealId?, dealName?, quoteId?, quoteNumber?, ownerUserId, ownerName, currency, grandTotal, orderDate, createdAt }`.
- `GET /orders/{id}` → özet + `subtotal, discountTotal, taxTotal, terms?, notes?, fulfilledAt?, cancelledAt?, cancelReason?, updatedAt?, lines[]` (teklifle aynı kalem biçimi).
- `POST /orders` → 201 + `Location` + detay (doğrudan sipariş): gövde tekliftekiyle aynıdır, `validUntil` yerine `orderDate?`; `draft` başlar; `SalesOrderCreated` (`source: "direct"`).
- `PUT /orders/{id}` → 204 (yalnız `draft`, tam değiştirme; `quoteId` değişmez), `DELETE /orders/{id}` → 204 (yalnız `draft`, yumuşak).
- `POST /orders/{id}/confirm`, `/fulfill` → 204; `POST /orders/{id}/cancel` `{ reason? }` → 204.
- Denetim: `GET /audit?entityType=SalesOrder&entityId=` (`crm.orders.read`).

## Rapor `GET /reports/commerce/summary?from&to` (`crm.reports.read`)
`from`/`to` M3 kurallarıyla aynı (`YYYY-MM-DD`, uçlar dahil, kiracı saatinde takvim günü, varsayılan son 12 ay, ters/10 yılı aşan aralık `validation`; `TenantCalendarService.ResolveReportRangeAsync` ile UTC yarı açık `[from, to+1 gün)`). Teklifler **`createdAt`** (yerel gün), siparişler **`orderDate`** aralığına göre; silinmişler yok.
```json
{
  "currencies": ["TRY"],
  "quotes": {
    "totalCount": 12, "totalAmount": 84500.50,
    "byStatus": [ { "status": "draft", "count": 3, "amount": 9000.00 }, { "status": "sent", "count": 4, "amount": 30000.00 },
                  { "status": "accepted", "count": 3, "amount": 35000.50 }, { "status": "rejected", "count": 1, "amount": 5500.00 },
                  { "status": "expired", "count": 1, "amount": 5000.00 } ]
  },
  "orders": {
    "totalCount": 4, "totalAmount": 41000.00,
    "byStatus": [ { "status": "draft", "count": 1, "amount": 6000.00 }, { "status": "confirmed", "count": 2, "amount": 20000.00 },
                  { "status": "fulfilled", "count": 1, "amount": 15000.00 }, { "status": "cancelled", "count": 0, "amount": 0 } ]
  },
  "conversionRate": 0.3333
}
```
- `byStatus` her zaman **tüm durumları** sabit sırayla döner (boş durum 0). Teklif durumları **etkin durumdur** (`expired` bugünün kiracı tarihine göre, aralık sonuna göre değil). `amount` = `grandTotal` toplamı.
- `orders.totalAmount` **iptal edilenler hariç** (`draft+confirmed+fulfilled`); `orders.totalCount` de öyle; iptaller yalnız `byStatus`'ta görünür.
- `conversionRate` = `accepted` teklif sayısı / **taslak olmayan** teklif sayısı (`sent+accepted+rejected+expired`), 0–1, 4 ondalığa yuvarlanır (yarım yukarı); payda 0 ise alan **yazılmaz** (`null`). Örnek: 3 / 9 = 0.3333.
- Tutarlar para birimlerine bakmadan toplanır (M2/M3 sınırlaması); aralıktaki farklı para birimleri `currencies`'te (sıralı, tekil) listelenir, arayüz birden çoksa "karışık para birimi" uyarısı gösterir.

## Web
Yeni dosyalar: `web/src/pages/commerce/*`, `web/src/components/commerce/*`, `web/src/services/{products,quotes,orders}.service.ts`, `web/src/hooks/use-{products,quotes,orders}.ts`, `web/src/lib/commerce-totals.ts`, yeni i18n ad alanı `commerce` (`web/public/locales/{tr,en}/commerce.json`; `i18n.ts`'e kayıt). Sıcak dosyalara **yalnız ekleme**: `App.tsx` (rotalar), `config/navigation.ts`, `i18n.ts`, `locales/*/navigation.json`, `types` (`PERMISSIONS` +6: `crmProductsRead/Write`, `crmQuotesRead/Write`, `crmOrdersRead/Write`), `hooks/use-crm-permissions.ts` (`canWriteProducts/Quotes/Orders`), `web/README.md`.
- **Menü:** Fırsatlar'dan sonra **Ürünler** (`/app/products`, `crm.products.read`), **Teklifler** (`/app/quotes`, `crm.quotes.read`), **Siparişler** (`/app/orders`, `crm.orders.read`); ilgili okuma izni yoksa menüde ve rotada yok (`NoAccess`). Yazma eylemleri (Yeni, Düzenle, Sil, durum düğmeleri) `*.write` ile gizlenir. Kalem seçici ürün araması `crm.products.read` ister; yoksa ürün seçimi kapalı, kalemler serbest metinle girilir. Firma seçici `crm.accounts.read` ister (yoksa yalnız önceden doldurulmuş firma ile oluşturulabilir).
- **Ürünler sayfası:** liste (ad, kod, birim fiyat + para birimi, KDV %, birim, durum) + arama + `isActive` filtresi + sıralama; oluştur/düzenle **diyaloğu** (form; sunucu alan hataları eşlenir; kod çakışması `product.code_taken` → `code` alanı hatası), aktif/pasif anahtarı, silme onayı. Detay sayfası yok (YAGNI).
- **Teklifler:** liste (numara, konu, firma, durum rozeti, tutar, geçerlilik, sahip; filtre çubuğu: durum, firma, sahip, "Dönüştürülmemiş kabul edilenler"; filtre-URL senkronu; `expired` rozeti ayrı renk) · **Detay** `/app/quotes/:id`: başlık (numara, konu, durum rozeti), duruma göre eylem düğmeleri (draft: Düzenle, Gönder, Sil · sent: Kabul, Reddet (neden diyaloğu), Süreyi uzat (tarih diyaloğu), Taslağa al · expired: Süreyi uzat, Taslağa al, Reddet · rejected: Taslağa al · accepted: **Siparişe dönüştür** (`crm.orders.write` gerekir; dönüştürülmüşse "Sipariş: SO-…" bağlantısı, düğme yok)), bilgi paneli (firma/kişi/fırsat bağlantıları), salt-okur kalem tablosu + toplam kartı, Şartlar/Notlar, **Denetim** sekmesi (`RecordAuditTab`, `entityType=Quote`) · **Editör** `/app/quotes/new` ve `/app/quotes/:id/edit` (yalnız draft; diğer durumda detaya yönlendirir): başlık formu (konu, firma seçici, kişi seçici (firmaya göre süzülür), fırsat seçici (firmaya göre), geçerlilik tarihi (varsayılan bugün+30), sahip, para birimi TRY/USD/EUR/GBP, şartlar, notlar) + **kalem ızgarası**.
- **Kalem ızgarası** (`LineItemsGrid`, teklif ve sipariş editörü ortak): sütunlar ürün (aranabilir seçici, isteğe bağlı), açıklama, adet, birim fiyat, iskonto %, KDV %, satır toplamı (salt-okur); satır ekle/sil/yukarı-aşağı taşı (klavye erişilebilir); ürün seçilince açıklama/birim fiyat/KDV ürün değerleriyle dolar (kullanıcı değiştirebilir); belge para birimi ile uyuşmayan ürün seçicide devre dışı. **Canlı toplam önizlemesi:** her değişimde `computeTotals` ile alt toplam / iskonto / KDV / genel toplam kartı anında güncellenir; kayıt sonrası sunucu değerleri gösterilir. Sunucu `lines[i].alan` hataları ilgili hücreye, diğer doğrulama hataları alanlara, `commerce.*`/`quote.*` kodları toast/uyarı olarak gösterilir. Kalemsiz taslak kaydedilebilir; "Gönder" kalem ister (`quote.no_lines` 422 mesajı).
- **Siparişler:** liste (numara, konu, firma, durum, tutar, sipariş tarihi, teklif bağlantısı), detay (eylemler: draft: Düzenle, Onayla, İptal, Sil · confirmed: Teslim edildi, İptal (neden diyaloğu)), kaynak teklife bağlantı, Denetim sekmesi (`entityType=SalesOrder`); **doğrudan sipariş** editörü `/app/orders/new` ve `/app/orders/:id/edit` (aynı `LineItemsGrid`).
- **"Teklif oluştur" eylemi:** fırsat detayı ve firma detayı sayfa eylemlerinde (`crm.quotes.write`). `/app/quotes/new?accountId=…&contactId=…&dealId=…` açar; fırsattan: firma, kişi, fırsat, para birimi ve konu (= fırsat adı) önceden dolar (yalnız kimlikler URL'de). Her ikisinde ayrıca **"Teklifler"** sekmesi (`crm.quotes.read`; `GET /quotes?accountId=` / `?dealId=`), firma detayında ek **"Siparişler"** sekmesi (`crm.orders.read`; `GET /orders?accountId=`).
- **Raporlar sayfası:** yeni **"Ticaret"** sekmesi (`crm.reports.read`; mevcut aralık seçicisi + CSV indirme): teklif durum tablosu (adet + tutar), sipariş durum tablosu, dönüşüm oranı kartı (payda 0 → "—"), çoklu para birimi uyarısı.
- **TR/EN:** tüm metinler, durum etiketleri, hata kodu eşlemeleri; izin adları `permission.*` desenine uygun (`permission.crm.products.read` … altı anahtar).

## Backend uygulama notları (dosya kapsamı)
- Yeni: `src/Modules/Commerce/Crm.Modules.Commerce.{Domain,Application,Contracts,Infrastructure,Api}`, `tests/Modules/Crm.Modules.Commerce.Tests`, migration `InitialCommerce` (`--project src/Modules/Commerce/Crm.Modules.Commerce.Infrastructure --context CommerceDbContext -o Persistence/Migrations`). Sales: `IRecordRelationLookup` uygulaması + kaydı (`SalesContractServices`).
- Sıcak dosyalara yalnız ekleme: `ModuleCatalog.cs` (`new CommerceModule()`), `Crm.Migrator/Program.cs`, `Crm.Worker/Program.cs` (`AddModuleDbContext` + `AddModuleHandlers` + `AddHostedService<OutboxPollingService<CommerceDbContext>>`), `TestFixture`/`CrmApiFactory` Respawn şemalarına `commerce`, `SharedResource{,.en}.resx` (aşağıdaki hata/alan anahtarları tr+en), `docs/architecture/backend.md` (Commerce bölümü + "M6A'da değişenler").
- Handler'lar `sealed`, `{Eylem}Handler`, her komut/sorgu `[RequiresPermission]`; FluentValidation `validation` + `errors` (kalem kuralları `RuleForEach` → `lines[i].alan`); liste sorguları `CommerceReadStore` (kiracı + silme filtresi altında; `q` kaçışlı `ILIKE`; sıralama beyaz liste); domain agregatları durum geçişlerini ve `DocumentTotals`'ı kendi içinde zorlar (Result deseni).
- Yeni hata kodları (metinler `SharedResource.resx` tr/en): `commerce.related_not_found` (404), `commerce.contact_account_mismatch` (400), `commerce.deal_account_mismatch` (400), `commerce.concurrent_update` (409), `product.code_taken` (409), `quote.not_editable` (409), `quote.invalid_transition` (409), `quote.expired` (409), `quote.no_lines` (422), `quote.not_accepted` (409), `quote.already_converted` (409), `order.not_editable` (409), `order.invalid_transition` (409), `order.no_lines` (422); mevcut: `validation`, `not_found`, `forbidden`, `owner.not_member`. Yeni `validation.*` mesajları (kalem adet/fiyat/yüzde/ondalık, pasif ürün, para birimi uyuşmazlığı, `validUntil` geçmiş, en çok 100 kalem) ve `field.*` adları.

## Testler
**Backend — birim (`Crm.Modules.Commerce.Tests`):**
- `DocumentTotals`: yukarıdaki dört referans vektör + belge {1,3} toplamı; yarım yukarı yuvarlama (0.025 → 0.03, 4.545 → 4.55), %100 iskonto, kalemsiz belge = 0, `grandTotal == Σ lineTotal` değişmezi (rastgele/özellik tabanlı örneklerle), çok kalemde kalem-bazlı yuvarlama ≠ toplu yuvarlama olan örnek, en büyük sınır değerler taşmaz.
- Teklif durum makinesi: tablodaki her geçerli/geçersiz geçiş (`draft→sent` kalemsiz 422, `accepted` uç, `revert`, `extend`, ikinci `accept`), düzenleme/silme yalnız draft; `expired` türetimi (sent + geçmiş tarih; draft/accepted/rejected asla; `validUntil` boş asla; kiracı saat dilimi sınırı: yerel gece yarısı öncesi/sonrası), süresi dolmuş `accept` → `quote.expired`.
- Sipariş durum makinesi: tüm geçişler, uç durumlar, kalemsiz `confirm` 422.
- Ürün: kod normalizasyonu, aralık kuralları.

**Backend — Testcontainers HTTP:**
- Ürün CRUD, filtre (`q`, `isActive`, `currency`), sıralama/sayfalama, arama joker kaçışı, `code_taken` (büyük/küçük harf duyarsız, silinmiş kodun yeniden kullanımı).
- Teklif CRUD, alan doğrulama (`lines[i].*` anahtarları), sahip kuralı, bağlı kayıt 404/`contact_account_mismatch`/`deal_account_mismatch`, pasif ürün/para birimi uyuşmazlığı, yanıttaki toplamların sunucuda hesaplandığı (gövdedeki sahte toplam yok sayılır), filtreler (`status=expired`, `converted`, `validFrom/To`), sıralama (boş `validUntil` her iki yönde sonda).
- **Numaralandırma eşzamanlılığı:** aynı kiracıda **20 paralel `POST /quotes`** → tümü 201, numaralar tekil ve 0001–0020 kesintisiz; sipariş sayacı bağımsız; A ve B kiracısı ikisi de `…-0001` alır; yıl dönümü (sahte saatle 2026-12-31T21:30Z Istanbul → `Q-2027-0001`); SaveChanges'i zorla başarısız kılınca sayaç geri döner (sonraki numara boşluksuz).
- **Dönüşüm atomikliği:** kabul → convert = 201, kalemler/toplamlar tekliften birebir, `quoteId` bağlı, `SalesOrderCreated` outbox satırı var, teklif `convertedOrderId` gösterir; `accepted` olmayan → `quote.not_accepted`; ikinci convert → `quote.already_converted`; **iki eşzamanlı convert → biri 201, biri 409, tek sipariş, sayaç tek adım**; kalem yazımını patlatan hata enjeksiyonu → sipariş/kalem/sayaç/outbox hiçbiri kalıcı değil; draft siparişi silince teklif yeniden dönüştürülebilir, cancelled sipariş varken 409; `QuoteAccepted` yalnız bir kez (çift `accept` yarışında).
- Sipariş: doğrudan oluşturma, CRUD, geçişler ve kilitler, filtreler.
- **Kiracı izolasyonu:** A'nın ürün/teklif/sipariş kimlikleri B'de GET/PUT/DELETE/geçiş/convert → 404; B'nin kaleminde A'nın `productId`'si → 400 `validation`; B'de A'nın firma/kişi/fırsatı → 404 `commerce.related_not_found`; listeler/rapor/denetim sızdırmaz; `document_counters` kiracı bazlı. `Crm.Tests.TenantIsolation` ve mimari testler yeni modülü otomatik kapsar (tüm yeni varlıklar `ITenantEntity`, `(tenant_id, …)` indeksli; Commerce yalnız `Sales.Contracts`/`Identity.Contracts`'a bağlı).
- **İzin 403:** her uç için ilgili `*.read`/`*.write` yokken 403 (yalnız okuma izni olan kullanıcı yazamaz; `orders.write` olmadan convert 403; `quotes.read` olmadan convert 403); Standard rol yeni altı izni taşır, eski kiracıda da senkronlanır; `GET /permissions` 24 anahtar; doğrulama yetkiden önce.
- **Denetim:** oluşturma/güncelleme/durum/silme kayıtları (camelCase enum), `GET /audit?entityType=Quote|SalesOrder|Product&entityId=` ilgili `*.read` ile.
- **Rapor doğruluğu:** bilinen veriyle (çeşitli durumlarda teklifler + `expired` + siparişler + iptal) `byStatus` sayı/tutarlar, `orders.totalAmount` iptalsiz, `conversionRate` (3/9 = 0.3333; payda 0 → alan yok), aralık uçları kiracı saat diliminde (31 Mart 21:30Z = 1 Nisan), varsayılan aralık, `currencies` listesi, `crm.reports.read` izni, doğrulama (ters aralık), kiracı izolasyonu.

**Web (Vitest + RTL, kontrata karşı sahte API):**
- `computeTotals`: backend ile **aynı dört referans vektör** ve belge toplamı; ondalık/yarım yukarı kenarları; BigInt sapmasızlığı.
- Kalem ızgarası: satır ekle/sil/taşı, ürün seçince alan doldurma, para birimi uyuşmayan ürünün devre dışı olması, canlı toplam kartının güncellenmesi, sunucu `lines[1].quantity` hatasının doğru hücreye eşlenmesi.
- Teklif editörü: kayıt gövdesinin kontrata uygunluğu (hesaplanan alan göndermez), sunucu hata eşlemesi, `?accountId&dealId` ile önceden doldurma ("Teklif oluştur" fırsat/firma), draft dışı teklifte düzenleme rotasının detaya yönlendirmesi.
- Detay eylemleri: durum × izin matrisine göre görünen düğmeler (draft/sent/expired/rejected/accepted; `crm.orders.write` yoksa "Siparişe dönüştür" yok), dönüştürünce sipariş sayfasına gidiş, dönüştürülmüşte bağlantı; reddet/uzat diyalogları.
- Listeler: filtre-URL senkronu, sıralama, boş/yükleniyor/hata durumları; ürün diyaloğu `product.code_taken` → `code` alanı hatası.
- Menü/rota izin gizleme (`crm.products.read` yokken Ürünler yok, `NoAccess`), yazma düğmelerinin `*.write` ile gizlenmesi; raporlar "Ticaret" sekmesi (aralık → istek parametreleri, payda 0 → "—", karışık para birimi uyarısı); TR/EN anahtar tamlığı.
- Kapı: `tsc`, `eslint`, `vitest`, `build` yeşil.

## Kapsam dışı
PDF/yazdırılabilir çıktı ve şablonlar, e-imza/müşteri onay bağlantısı, e-posta ile gönderme (**"Gönder" yalnızca durum değişikliğidir**), fiyat listeleri ve kademeli/kampanya fiyatı, çok para birimli kur çevrimi (tek belge tek para birimi; ürün para birimi belgeyle aynı olmalı), stok/envanter/sevkiyat/faturalama, belge düzeyi iskonto/kargo/ek masraf, KDV dahil fiyat, kalem birimi alanı, teklif sürümleri/revizyon geçmişi arayüzü, onay iş akışları (M4 onayı teklife bağlanmaz), teklif kabulünde fırsat aşamasının otomatik değişmesi, iptal edilmiş siparişin teklifi yeniden dönüştürme, kalem bazlı denetim kaydı, dashboard ticaret widget'ları, ürün kategorileri/görselleri/varyantları, toplu içe/dışa aktarma, ürün detay sayfası, teklif/sipariş kopyalama, iade/kredi notu.
