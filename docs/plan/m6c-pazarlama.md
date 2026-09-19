# Milestone 6C — Pazarlama: Kampanyalar ve kampanya üyeleri (plan + HTTP kontratı)

Pano kartı: C-M6C (`m6/marketing`) · PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Kararlar K5/K7/K10/K14: [kararlar.md](../architecture/kararlar.md) · Önceki: [M2 kontratı](m2-api-kontrat.md), [M3](m3-aktivite-rapor.md), [M4](m4-workflow.md). Biçim kuralları aynıdır: taban yol `/api/v1`, JSON camelCase, enum'lar camelCase string, `null` alanlar yanıtta yazılmaz, hatalar ProblemDetails + `code` (+ `errors`), sayfalı liste `{ items, page, pageSize, totalCount }` (`page` 1'den, `pageSize` varsayılan 25, en çok 100), sıralama `sort=alan` veya `sort=-alan`, tarih-saatler ISO 8601 UTC, "yalnız tarih" alanlar `YYYY-MM-DD`. Tüm uçlar Bearer ister; kiracı token'dan gelir; başka kiracının kaydı her zaman `not_found` (404).

**Çıktı:** Pazarlamacı kampanya açar (tür, tarih, bütçe, beklenen gelir, gerçekleşen maliyet), potansiyel müşterileri ve kişileri toplu ya da tek tek kampanyaya ekler, üye durumunu (eklendi → gönderildi → yanıtladı …) günceller; bir potansiyel müşteri dönüştürüldüğünde üyelik durumu kendiliğinden `converted` olur. Kampanya başına üye sayısı, yanıt oranı, dönüşüm ve potansiyel başına maliyet ile tüm kampanyaların özeti (rapor + ana sayfa kartı) izlenir.

## Modül kararı
- Yeni modül `Crm.Modules.Marketing.{Domain,Application,Contracts,Infrastructure,Api}` (şema `marketing`, tek `MarketingDbContext`, migration `InitialMarketing`). `./build/new-module.ps1 -Name Marketing` ile açılır; `ModuleCatalog`, `Crm.Migrator`, `Crm.Worker` (outbox + `LeadConverted` tüketicisi) Sales/Activities ile aynı kalıpla bağlanır.
- Sales'e **yalnız `Sales.Contracts`** üzerinden bağlanır: `IRecordLookup` (varlık + ad, toplu ad çözümü; mevcut, değişmez) ve yeni `ILeadStatusLookup` (aşağıda). Identity'ye `Identity.Contracts` üzerinden: `IMemberLookup` (sahip üyeliği + ad), `TenantCalendarService` (rapor tarih aralığı). Marketing Sales'in tablolarına/varlıklarına dokunmaz; lead ve kişi kaydı Sales'te kalır, Marketing yalnız `(memberType, memberId)` başvurusu tutar (yumuşak bağ, FK yok).
- Ayrı Reporting modülü yok: kampanya özeti Marketing'in kendi verisinden hesaplanır (`GET /reports/marketing/summary`, `crm.reports.read`), Sales/Activities raporlarıyla aynı desen.
- **Lead kaynağı ilişkisi:** `Lead.Source = campaign` değeri Sales'te zaten var. Bu kartta lead'e kampanya kolonu **eklenmez** (Sales şeması/migration'ı ve M6A/B ile çakışma yok); ilişki `campaign_members` üyeliğidir. Lead ve kişi detayında "Kampanyalar" sekmesi bu üyelikleri gösterir. Üye eklemek lead'in `source` alanını değiştirmez.
- **Sales.Contracts eklemeleri (küçük, geriye uyumlu, yalnız ekleme):**
  - `ILeadStatusLookup` (Sales uygular; `AddSalesContractServices()` içinde kaydedilir; kiracı + yumuşak silme filtresi altında): `Task<IReadOnlySet<Guid>> GetConvertedLeadIdsAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default)` — verilen lead kimliklerinden durumu `converted` olanları döner. Marketing, dönüşmüş lead'in kampanyaya eklenmesini bununla reddeder.
  - `LeadConverted` (mevcut, değişmez): `Marketing.Application.Members.LeadConvertedMarketingHandler : IIntegrationEventHandler<LeadConverted>`; Worker `Program.cs`'e Workflows örneği gibi elle kaydedilir (sıcak dosya, yalnız ekleme). API'de `AddModuleHandlers` tarar.
  - `IRecordLookup` değişmez: kişi/lead tam adı ve varlık kontrolü mevcut `ExistsAsync` / `GetDisplayNamesAsync` ile yapılır.
- Marketing **integration event yayınlamaz** (YAGNI; tüketen yok). Yalnız `LeadConverted` tüketir.

## Varlıklar (hepsi `TenantAggregateRoot`, kimlikler `Guid.CreateVersion7`, EF'te `ValueGeneratedNever`)

### `Campaign` / `marketing.campaigns` (`IAuditLogged`, yumuşak silinir)
| Alan | Kural |
|---|---|
| `name` | zorunlu, ≤ 200 |
| `type` | `email\|event\|webinar\|advertising\|other`, zorunlu |
| `status` | `planned\|active\|completed\|cancelled`, oluştururken varsayılan `planned`; sonrası yalnız `POST /campaigns/{id}/status` ile (PUT durumu değiştirmez) |
| `startDate?`, `endDate?` | yalnız tarih (`YYYY-MM-DD`); ikisi de verilmişse `endDate >= startDate`, aksi `campaign.invalid_date_range` (400, `errors.endDate`) |
| `currency` | ISO-4217 3 harf büyük (`^[A-Z]{3}$`), varsayılan `TRY`; `budget`, `expectedRevenue`, `actualCost` için tek para birimi |
| `budget?`, `expectedRevenue?`, `actualCost?` | `decimal(18,4)`, ≥ 0 (negatif `validation`) |
| `description?` | ≤ 4000 |
| `ownerUserId` | verilmezse çağıran; verilen kiracının aktif üyesi olmalı (`owner.not_member` 400, `IMemberLookup`); sahibi değişmeyen kayıtta üyelik yeniden sorgulanmaz (pasifleşmiş sahibli kayıt düzenlenebilir) |

**Durum geçişleri (bağlayıcı tablo):**

| Şu andan | İzin verilen |
|---|---|
| `planned` | `active`, `cancelled` |
| `active` | `completed`, `cancelled` |
| `completed` | `active` (yeniden aç) |
| `cancelled` | `planned` (yeniden planla) |

Aynı duruma geçiş idempotent no-op'tur (204). Tabloda olmayan geçiş `campaign.invalid_status_transition` (409; `from`/`to` argümanları). Oluşturmada `status` verilirse yalnız `planned` veya `active` olabilir, aksi `validation` (`errors.status`). Kampanya `completed` veya `cancelled` iken **yeni üye eklenemez** (`campaign.closed` 409); üye durumu güncelleme ve üye çıkarma açık kalır (geç gelen yanıtlar işlenebilsin). `PUT` her durumda çalışır (tamamlanan kampanyanın `actualCost`'u sonradan girilir).

İndeksler: `(tenant_id, status)`, `(tenant_id, type)`, `(tenant_id, owner_user_id)`, `(tenant_id, start_date)`, `(tenant_id, created_at)`.

### `CampaignMember` / `marketing.campaign_members` (`IAuditLogged`, **fiziksel silinir**; silme `deleted` denetim kaydı bırakır)
| Alan | Kural |
|---|---|
| `campaignId` | FK (Marketing içi), kampanya yumuşak silinince üyeler kalır ama hiçbir uçtan görünmez (kampanya filtresinden geçer) |
| `memberType` | `lead\|contact` (tel: camelCase; iç eşleme `Sales.Contracts.RecordType`) |
| `memberId` | Sales lead/kişi kimliği; FK yok (yumuşak bağ) |
| `status` | `added\|sent\|responded\|converted\|unsubscribed`, ekleme anında `added` |
| `addedAt`, `statusChangedAt` | UTC; durum değişince `statusChangedAt` güncellenir |
| `addedByUserId?` | ekleyen kullanıcı |

**Benzersizlik:** `(tenant_id, campaign_id, member_type, member_id)` benzersiz indeks. Eşzamanlı eklemede benzersiz ihlali "zaten üye" sayılır (işleyici ön kontrol yapar, ihlalde çakışan satırları atlayıp bir kez yeniden dener). Ek indeksler: `(tenant_id, member_type, member_id)` (kayıt bazlı sorgu ve `LeadConverted`), `(tenant_id, campaign_id, status)` (metrikler).

**Üye durum kuralları:**
- Elle ayarlanabilir durumlar: `added`, `sent`, `responded`, `unsubscribed` (birbirine serbest geçiş). `converted` elle ayarlanamaz (`validation`, `errors.status`); yalnız `LeadConverted` ile olur ve yalnız `memberType = lead` içindir.
- `converted` üyenin durumu değişmez (kilitli): toplu durum güncellemesinde atlanır (`skippedCount`), tek üyeli çağrıda da 200 ve `skippedCount: 1` döner. Üyelik yine de çıkarılabilir.
- Dönüşmüş (`converted`) bir lead kampanyaya eklenemez: `skipped` içinde `lead_converted` nedeniyle raporlanır (dönüşüm o kampanyanın başarısı sayılmaz).
- Kişiler (`contact`) hiçbir zaman `converted` olmaz.

## Kampanyalar `/campaigns` (`crm.campaigns.read` / `crm.campaigns.write`)
**Kampanya yanıtı:** `{ id, name, type, status, startDate?, endDate?, currency, budget?, expectedRevenue?, actualCost?, description?, ownerUserId, ownerName, memberCount, createdAt, updatedAt? }` (`memberCount` liste ve detayda aynı; liste için sayfadaki kampanyalar tek toplama sorgusuyla sayılır).

- `GET /campaigns?q&type&status&ownerUserId&startFrom&startTo&sort&page&pageSize` → sayfalı liste. `q`: ad + açıklama, `ILIKE`, joker (`%`/`_`) kaçışlı parametre. `type` ve `status` virgülle ayrılmış çoklu değer alır (`status=planned,active`; bilinmeyen değer `validation`). `startFrom`/`startTo` (`YYYY-MM-DD`, uçlar dahil) `startDate` üzerindedir; biri verilince `startDate`'i olmayanlar dışarıda kalır. `sort` beyaz liste: `name, type, status, startDate, endDate, budget, actualCost, createdAt` (bilinmeyen alan yok sayılır); varsayılan `-createdAt`; boş değerler her iki yönde de sonda; her zaman `createdAt` + `Id` ile kararlı.
- `GET /campaigns/{id}` → kampanya yanıtı. Yok/silinmiş/başka kiracı → `not_found`.
- `POST /campaigns` `{ name*, type*, status?, startDate?, endDate?, currency?, budget?, expectedRevenue?, actualCost?, description?, ownerUserId? }` → 201 + `Location` + kampanya yanıtı.
- `PUT /campaigns/{id}` (aynı alanlar, `status` hariç; **tam değiştirme**: gönderilmeyen isteğe bağlı alan temizlenir, `ownerUserId` verilmezse mevcut sahip korunur, `currency` verilmezse `TRY`) → 204.
- `POST /campaigns/{id}/status` `{ status: "planned"|"active"|"completed"|"cancelled" }` → 204 (geçiş tablosu yukarıda).
- `DELETE /campaigns/{id}` → 204 (yumuşak silme; üyeler kalır, hiçbir uçtan görünmez; üyeliği olan kampanya silinebilir).
- Hata kodları: `validation`, `campaign.invalid_date_range` (400), `campaign.invalid_status_transition` (409), `owner.not_member` (400), `not_found`, `forbidden`.

## Kampanya üyeleri `/campaigns/{id}/members`
**Üye satırı:** `{ id, memberType, memberId, memberName?, memberMissing, status, addedAt, statusChangedAt, addedByUserId?, addedByName? }`. `id` üyelik satırının kimliğidir (kayıt kimliği `memberId`). `memberName`: lead/kişi tam adı, `IRecordLookup.GetDisplayNamesAsync` ile **toplu** çözülür (sayfa başına tür başına tek sorgu). Kayıt silinmişse/çözülemezse `memberName` yazılmaz ve `memberMissing: true` olur (Activities'teki yumuşak bağ davranışı); üyelik yine listelenir ve çıkarılabilir. `addedByName` `IMemberLookup` ile.

- `GET /campaigns/{id}/members?memberType&status&sort&page&pageSize` → sayfalı. `status` çoklu (virgülle). `sort`: `addedAt`, `statusChangedAt`, `status`, `memberType` (varsayılan `-addedAt`, ardından `Id`). **Ada göre arama/sıralama yoktur** (ad Sales'te; modüller arası JOIN yok).
- `POST /campaigns/{id}/members` (toplu ekleme, tek üye = tek elemanlı dizi) `{ memberType: "lead"|"contact", memberIds: [uuid, …] }` → **200**
  ```json
  { "addedCount": 3, "alreadyMemberCount": 1, "skipped": [ { "memberId": "…", "reason": "not_found" }, { "memberId": "…", "reason": "lead_converted" } ] }
  ```
  - `memberIds`: 1–**500** eleman (aksi `validation`, `errors.memberIds`); tekrarlar sessizce tekilleştirilir. **İdempotent:** zaten üye olanlar `alreadyMemberCount`'a girer, hata değildir; aynı çağrı tekrarlanırsa `addedCount: 0`.
  - Kiracıda olmayan (yok/silinmiş/başka kiracı) kimlikler 404 vermez; `skipped` içinde `not_found` olur, kalanlar eklenir. Dönüşmüş lead'ler `lead_converted` ile atlanır (`ILeadStatusLookup`).
  - Kampanya `completed`/`cancelled` ise → 409 `campaign.closed` (hiçbir şey eklenmez). Kampanya yok → 404.
  - Tek transaction; ekleme `crm.campaigns.write` ister (lead/kişi için ayrıca yazma izni gerekmez).
- `POST /campaigns/{id}/members/status` `{ memberIds: [uuid, …], status }` → 200 `{ updatedCount, skippedCount }`. `memberIds` üyelik **satır** kimlikleridir (1–500). `status` ∈ `added|sent|responded|unsubscribed` (`converted` → `validation`). `updatedCount`: durumu gerçekten değişen satırlar; `skippedCount`: `converted` kilitli satırlar; bu kampanyada olmayan kimlikler sessizce yok sayılır. Kampanya kapalı olsa da çalışır.
- `POST /campaigns/{id}/members/remove` `{ memberIds: [uuid, …] }` (satır kimlikleri, 1–500) → 200 `{ removedCount }`; olmayan kimlikler yok sayılır (idempotent). Kapalı kampanyada da çalışır.

## Kayıt bazlı üyelikler
`GET /campaigns/by-member?memberType=lead|contact&memberId=<uuid>` (`crm.campaigns.read`; yol `{id:guid}` kısıtı sayesinde `/campaigns/{id}` ile çakışmaz) → düz dizi, en çok 200, `addedAt` azalan:
```json
[ { "campaignId": "…", "campaignName": "Sonbahar E-posta", "campaignType": "email", "campaignStatus": "active", "membershipId": "…", "memberStatus": "sent", "addedAt": "2026-09-19T10:00:00Z" } ]
```
Yalnız silinmemiş kampanyalar. Lead/kişi `IRecordLookup.ExistsAsync` ile doğrulanır; yoksa `not_found`. Lead/kişi detayındaki "Kampanyalar" sekmesi bunu kullanır (üyelik satırındaki `campaignId` + `membershipId` ile çıkarma `members/remove` çağrısıdır).

## Metrikler
`GET /campaigns/{id}/metrics` (`crm.campaigns.read`) → hesaplanır, saklanmaz; tek gruplama sorgusu (`campaign_id` üzerinde `member_type` + `status` sayımları):
```json
{
  "campaignId": "…",
  "memberCount": 40, "leadCount": 30, "contactCount": 10,
  "statusCounts": { "added": 10, "sent": 12, "responded": 8, "converted": 6, "unsubscribed": 4 },
  "contactedCount": 30, "responseCount": 14, "responseRate": 46.67,
  "convertedCount": 6, "conversionRate": 20,
  "currency": "TRY", "costPerLead": 400
}
```
Tanımlar (bağlayıcı):
- `memberCount` = tüm üyeler; `leadCount`/`contactCount` = `memberType` kırılımı; `statusCounts` beş anahtarın hepsi her zaman vardır (0 dahil).
- `contactedCount` = `status ≠ added` olan üyeler (ulaşılan). `responseCount` = `responded + converted`. **`responseRate` = `responseCount / contactedCount × 100`**, 2 ondalığa yuvarlanır (yarıdan yukarı); `contactedCount = 0` ise `0`. (Pay her zaman paydanın alt kümesi olduğundan 0–100.)
- `convertedCount` = `statusCounts.converted`. **`conversionRate` = `convertedCount / leadCount × 100`** (yalnız lead'ler dönüşebilir), 2 ondalık; `leadCount = 0` ise `0`.
- **`costPerLead` = `actualCost / leadCount`**, 2 ondalık; `actualCost` boşsa veya `leadCount = 0` ise alan **yazılmaz**. `currency` kampanyanın para birimidir.
- Silinmiş lead/kişiye ait üyelikler de sayılır (sayım Marketing'in kendi verisindendir; bilinen sınırlama, açık işler).

## Pazarlama raporu `GET /reports/marketing/summary?from&to` (`crm.reports.read`, Marketing modülü)
Tarih parametreleri M3 raporlarıyla aynıdır: `from`/`to` `YYYY-MM-DD`, uçlar dahil, **organizasyon saat diliminde** takvim günü (`TenantCalendarService.ResolveReportRangeAsync`), ikisi de isteğe bağlı, varsayılan son 12 ay; ters/10 yılı aşan aralık veya bugünden sonraki `from` → `validation`. **Kampanya aralığa şöyle girer:** `startDate` varsa `startDate ∈ [from, to]`; `startDate` yoksa `createdAt` (kiracı yerel günü, UTC yarı açık `[from, to+1 gün)` sınırıyla) aralıkta. Yumuşak silinenler yok; tüm durumlar (iptal dahil) sayılır. Tutarlar para birimine bakmadan toplanır (M3 ile aynı bilinen sınırlama; yanıtta `currency` yok).
```json
{
  "from": "2025-10-01", "to": "2026-09-19",
  "campaignCount": 12,
  "byStatus": [ { "status": "planned", "count": 2 }, { "status": "active", "count": 5 }, { "status": "completed", "count": 4 }, { "status": "cancelled", "count": 1 } ],
  "byType": [ { "type": "email", "count": 4, "budget": 80000, "actualCost": 61000, "memberCount": 900, "convertedCount": 35 } ],
  "totals": {
    "budget": 250000, "expectedRevenue": 900000, "actualCost": 175000,
    "memberCount": 2400, "leadCount": 1800, "contactedCount": 1500, "responseCount": 420, "responseRate": 28,
    "convertedCount": 96, "conversionRate": 5.33, "costPerLead": 97.22
  },
  "topCampaigns": [ { "id": "…", "name": "…", "type": "email", "status": "active", "budget": 50000, "actualCost": 12000, "memberCount": 300, "responseRate": 41.5, "convertedCount": 22 } ]
}
```
- `byStatus`: dört durumun hepsi sabit sırada (0 dahil). `byType`: beş türün hepsi sabit sırada (`email, event, webinar, advertising, other`, 0 dahil).
- `totals`: `budget`, `expectedRevenue`, `actualCost` toplamı (boşlar 0); `responseRate`, `conversionRate`, `costPerLead` yukarıdaki metrik formülleriyle **toplam** sayılar üzerinden hesaplanır (kampanya oranlarının ortalaması değil); `costPerLead` = `totals.actualCost / totals.leadCount`, `leadCount = 0` ise yazılmaz. "Bütçe – maliyet" farkı istemcide hesaplanır.
- `topCampaigns`: en çok 10; `convertedCount` azalan, sonra `memberCount` azalan, sonra ad artan. Kampanya başına `responseRate` metrik tanımıyla aynıdır.
- Uygulama: `IMarketingReportStore` (veritabanında toplama; kampanya başına üye sayımı `campaign_members` gruplaması), rapor handler'ı `[RequiresPermission("crm.reports.read")]`. Sayısal doğruluk bilinen veriyle testlidir.

## İzinler
- Yeni `Crm.Modules.Marketing.Contracts.MarketingPermissions` (`Module = "marketing"`, `Group = "crm"`): `crm.campaigns.read`, `crm.campaigns.write`; modül `IModule.Permissions` ile kataloğa bildirir → `GET /permissions` 18 → **20** anahtar (mevcut anahtar sayısını sabitleyen testler güncellenir). `crm.reports.read` (Identity.Contracts, ortak) rapor ucu için kullanılır, yeni rapor izni yok.
- **Uç–izin eşlemesi:** okuma uçları (liste, detay, üye listesi, metrikler, `by-member`) `crm.campaigns.read`; yazma uçları (POST/PUT/DELETE kampanya, `status`, üye ekle/durum/çıkar) `crm.campaigns.write`; rapor `crm.reports.read`. Doğrulama yetkiden önce çalışır (M2 kuralı).
- **SystemRoleDefinitions:** **Administrator** kataloğun tamamını alır (yeni iki anahtar dahil, kod değişmez). **Standard** kuralı "tüm `crm.*` (yalnız `crm.approvals.decide` hariç) + `org.users.read`" olduğundan iki yeni anahtarı **otomatik alır; `SystemRoleDefinitions.cs` koda dokunulmaz**, `StandardExcluded`'a ekleme yapılmaz. Mevcut organizasyonlar API açılışında `SystemRolePermissionSynchronizer` ile senkronlanır. Testler bunu doğrular (yeni organizasyon + eski organizasyon).
- Lead/kişi adları `IRecordLookup` ile gösterilir; `crm.campaigns.read` taşıyan ama `crm.leads.read`/`crm.contacts.read` taşımayan özel rol üye adlarını görür (Activities'teki `relatedName` ile aynı, kabul edilmiş sınırlama; açık işlere yazılır).

## Denetim ve olaylar
- `Campaign` ve `CampaignMember` değişiklikleri (oluşturma/güncelleme/silme) `audit.audit_log_entries`'e iş verisiyle aynı transaction'da yazılır (`EntityType` = `Campaign` | `CampaignMember`); enum alanları camelCase string, kişisel veri alanı yok (maskelenen alan yok). `MarketingAuditEntities` + `MarketingAuditEntityPermissions : IAuditEntityPermissions`: her iki tür `crm.campaigns.read` ile `GET /audit?entityType=Campaign&entityId={id}` (ve `CampaignMember`, satır kimliğiyle) okunur. Kampanya "Denetim" sekmesi yalnız `Campaign` kayıtlarını gösterir; üyelik kayıtları API'den okunabilir (arayüzde üye satırı bazlı denetim yok).
- Toplu ekleme/çıkarmada satır başına denetim kaydı doğar; üst sınır 500 olduğundan yazma hacmi sınırlıdır.
- Tüketilen olay: `LeadConverted` → `LeadConvertedMarketingHandler`: olayın `TenantId`'si kapsamında `(memberType = lead, memberId = LeadId)` olan **tüm** üyelik satırlarını (her kampanya, kampanya durumu ne olursa olsun, silinmiş kampanya hariç) `status = converted`, `statusChangedAt = şimdi` yapar; zaten `converted` olan satırlara dokunmaz → **idempotent** (olay tekrar teslim edilse de aynı sonuç). Üyeliği olmayan lead için no-op. Eşleşen kişi (`ContactId`) otomatik olarak kampanyaya eklenmez (kapsam dışı). Değişiklikler `CampaignMember` denetim kaydı üretir (kullanıcı boş, sistem bağlamı).

## Hata kodları (metinler `SharedResource.resx` tr/en'e eklenir)
| code | HTTP | Ne zaman |
|---|---|---|
| `validation` | 400 | Girdi geçersiz (`errors` ile; ör. `errors.memberIds`, `errors.status`, `errors.name`) |
| `campaign.invalid_date_range` | 400 | `endDate < startDate` (`errors.endDate`) |
| `owner.not_member` | 400 | Sahip aktif üye değil (mevcut kod) |
| `campaign.invalid_status_transition` | 409 | Geçiş tablosunda olmayan durum değişikliği |
| `campaign.closed` | 409 | `completed`/`cancelled` kampanyaya üye ekleme |
| `forbidden` / `not_found` | 403 / 404 | İzin yok / kampanya yok, silinmiş veya başka kiracıda; `by-member` için lead/kişi yok |

Alan adları (`field.campaign.*`, `validation.campaign_*`) ve durum/tür etiketleri için resx anahtarları eklenir; üye durumu ve tür/durum adları istemcide çevrilir (K8).

## Web (React, Mantine 9; `web/**`)
Yeni dosyalar (öneri): `web/src/services/campaigns.service.ts`, `web/src/hooks/use-campaigns.ts`, `web/src/types/campaigns.ts` (+ `types/index.ts` yeniden dışa aktarımı, `PERMISSIONS.crmCampaignsRead/Write`), `web/src/pages/crm/campaigns.tsx`, `campaign-detail.tsx`, `web/src/components/marketing/{campaign-form-dialog, campaign-members-tab, add-to-campaign-dialog, campaign-metrics-cards, campaign-status-menu, record-campaigns-tab}.tsx`, `web/src/components/reports/marketing-report-tab.tsx`, i18n `web/public/locales/{tr,en}/campaigns.json`. Sıcak dosyalara yalnız ekleme: `App.tsx` (rotalar `campaigns`, `campaigns/:id`), `config/navigation.ts`, `i18n.ts`, `navigation.json`, `common.json`, `README.md`.

- **Menü:** "Kampanyalar" (`Megaphone` ikonu), Fırsatlar'dan sonra, Aktiviteler'den önce; `crm.campaigns.read` yoksa görünmez; rota `PermissionGuard`'lı (yetkisiz → `NoAccess`).
- **Kampanyalar listesi** (`/app/campaigns`, `list-page-frame` + `data-table`): sütunlar ad (detaya bağlantı), tür rozeti, durum rozeti, başlangıç–bitiş, bütçe, üye sayısı, sahip; arama kutusu (`q`), tür ve durum çoklu filtreleri, sahip filtresi, sıralanabilir başlıklar (`sort` beyaz listesi); durum/filtre/sıralama/sayfa **URL ile senkron** (`use-list-params`); "Yeni kampanya" düğmesi ve satır menüsü (düzenle/sil, onay diyaloğu) yalnız `crm.campaigns.write` ile. Boş/yükleniyor/hata durumları.
- **Oluştur/düzenle diyaloğu** (`campaign-form-dialog`): ad, tür, başlangıç/bitiş tarihi (tarih seçici; bitiş < başlangıç istemcide de engellenir), para birimi, bütçe, beklenen gelir, gerçekleşen maliyet (≥ 0), açıklama, sahip seçici (`owner-select`); oluşturmada durum `planned`/`active` seçilebilir, düzenlemede durum alanı yoktur. Sunucu hataları `applyValidationErrors` ile alanlara eşlenir (`campaign.invalid_date_range` → bitiş tarihi alanı, `owner.not_member` → sahip alanı).
- **Kampanya detayı** (`/app/campaigns/:id`, `record-detail-shell`): başlık + durum rozeti ve **durum menüsü** (yalnız geçiş tablosundaki geçerli hedefleri gösterir: planlandı → "Başlat"/"İptal et"; aktif → "Tamamla"/"İptal et"; tamamlandı → "Yeniden aç"; iptal → "Yeniden planla"; `campaign.invalid_status_transition` 409'da hata toast'ı), düzenle/sil düğmeleri (`write`). Sekmeler:
  - **Genel:** bilgi paneli (tür, tarihler, bütçe / beklenen gelir / gerçekleşen maliyet, sahip, açıklama) + **metrik kartları** (`GET /campaigns/{id}/metrics`): Üye sayısı, Yanıt oranı (%), Dönüşen (adet + `conversionRate`), Potansiyel başına maliyet (yoksa "—"), durum dağılımı (beş durumun küçük çubuğu/rozet sayıları). Üye durumu/ekleme/çıkarma sonrası metrikler yeniden yüklenir (`invalidateQueries`).
  - **Üyeler:** sayfalı tablo (tür rozeti, ad — kişi/lead detayına bağlantı, `memberMissing` ise soluk "Kayıt silinmiş", durum seçici/rozet, eklenme tarihi), tür ve durum filtreleri, satır seçimi; `write` ile: "Üye ekle" diyaloğu (tür seç lead/kişi → o türün mevcut liste API'siyle aranan çoklu seçim, seçilenler `POST members`; sonuç özeti toast'ı: "3 eklendi, 1 zaten üyeydi, 2 atlandı" ve `skipped` nedenleri), seçili satırlar için toplu "Durumu değiştir" menüsü (`converted` seçeneği yok) ve "Kampanyadan çıkar" (onay diyaloğu), satır içinde durum değiştirme; `converted` satırların durum seçicisi kilitli. Kampanya `completed`/`cancelled` iken "Üye ekle" devre dışı + açıklayıcı ipucu (`campaign.closed`).
  - **Denetim:** mevcut `RecordAuditTab` (`entityType="Campaign"`), `org.audit.read` veya `crm.campaigns.read`.
- **"Kampanyaya ekle" toplu eylemi:** `data-table`'a geriye uyumlu, isteğe bağlı satır seçimi (`selection` prop'u; verilmezse mevcut davranış) eklenir; **Potansiyeller** ve **Kişiler** listelerinde seçim etkin, seçili satır varken eylem çubuğunda "Kampanyaya ekle" görünür (yalnız `crm.campaigns.write`). Seçim geçerli sayfayla sınırlıdır (sayfa ≤ 100 < 500 sınırı); sayfa/filtre değişince seçim temizlenir. Diyalog (`add-to-campaign-dialog`): yalnız `planned`/`active` kampanyaları listeler (`status=planned,active`, aranabilir seçici) → `POST /campaigns/{id}/members` (`memberType` liste türüne göre); sonuç özeti toast'ı, `campaign.closed` yarışında hata toast'ı; dönüşmüş lead'ler için "atlandı: dönüşmüş" bilgisi.
- **Lead ve Kişi detayında "Kampanyalar" sekmesi** (`record-campaigns-tab`, `crm.campaigns.read` ile görünür; `lead-detail.tsx`/`contact-detail.tsx` sekme listesine ekleme): `GET /campaigns/by-member` tablosu (kampanya adı → detay, tür, kampanya durumu, üyelik durumu, eklenme tarihi); `write` ile "Kampanyaya ekle" (tek kampanya seçici → tek elemanlı `POST members`) ve satırda "Çıkar". Lead dönüşmüşse ekleme düğmesi gizli, listedeki üyelik `converted` görünür.
- **Raporlar sayfasına "Pazarlama" sekmesi** (`crm.reports.read`; mevcut tarih aralığı seçici ve `report-tabs`/`report-panel` deseni): durum ve tür dağılımı (`@mantine/charts`), "Bütçe / gerçekleşen maliyet" çubuğu (tür başına), toplam kartları (yanıt oranı, dönüşüm, potansiyel başına maliyet), `topCampaigns` tablosu, istemci tarafı CSV indirme. Aralık → `from`/`to` parametreleri.
- **Ana sayfa (dashboard) kartı** "Kampanya özeti" (`crm.reports.read` **ve** `crm.campaigns.read` ile görünür, aksi gizli): `summary` varsayılan aralığıyla `campaignCount`, aktif kampanya sayısı (`byStatus`), `responseRate`, `convertedCount`; "Kampanyalar"a bağlantı; boş/yükleniyor/hata durumları.
- TR/EN: tüm metinler `campaigns` (ve `navigation`, `reports`, `home`) ad alanlarında; tür/durum/üye durumu etiketleri, hata kodu → metin eşlemesi.

## Testler
**Backend** (`Crm.Modules.Marketing.Tests`; birim + Testcontainers HTTP, Respawn şemalarına `marketing` eklenir):
- *Domain:* durum geçiş tablosunun her hücresi (izinli/izinsiz), aynı duruma no-op; `endDate >= startDate` (eşit geçerli, önce geçersiz), yalnız biri verilince geçerli; tutar ≥ 0; oluşturmada `status` yalnız planned/active; üye durum kuralları (`converted` elle atanamaz, `converted` kilitli); metrik formülleri (payda 0, yuvarlama, `costPerLead` yokluğu, oran üst sınırı 100).
- *HTTP — kampanya:* CRUD + tam değiştirme, filtre/çoklu değer/sıralama/sayfalama/`q` joker kaçışı, `startFrom/startTo`, boşların sonda olması, `status` uçları (geçerli + 409), silme sonrası 404 ve üyelerin görünmemesi, `owner.not_member`.
- *HTTP — üyeler:* toplu ekleme (1, 500 ve 501 eleman → 400), **idempotency** (aynı çağrı iki kez → ikincide `addedCount: 0`, `alreadyMemberCount` doğru; girdi içi tekrarlar), yok/başka kiracı kimliği → `skipped: not_found` (404 değil), dönüşmüş lead → `skipped: lead_converted`, kapalı kampanyaya ekleme `campaign.closed` (durum güncelleme/çıkarma yine çalışır), eşzamanlı aynı ekleme (benzersiz ihlali → hata değil, çift satır yok), üye listesi filtre/sıralama/sayfalama + toplu ad çözümü (silinmiş kayıt → `memberMissing`), toplu durum (`converted` → 400, kilitli satır `skippedCount`), toplu çıkarma (olmayan kimlik yok sayılır), `by-member` (yok kayıt → 404, silinmiş kampanya dışarıda).
- ***`LeadConverted` otomatik dönüşüm:*** lead iki kampanyada üye → `POST /leads/{id}/convert` sonrası (olay Worker yerine testte `IEventBus` ile yayınlanır) iki üyelik `converted`; kilitli durumlar; olay iki kez teslim → aynı sonuç (idempotent); üyeliği olmayan lead → no-op; başka kiracının olayı bu kiracının üyeliklerini etkilemez; kişi otomatik eklenmez.
- ***Metrik doğruluğu:*** bilinen veri kümesiyle (ör. 10 lead + 4 kişi; 3 `added`, 5 `sent`, 4 `responded`, 2 `converted`, 0 `unsubscribed` …) `metrics` her alanı elle hesaplanmış değerle eşleşir; üye durumu değişince yeniden hesaplanır; `costPerLead` `actualCost` yokken/lead yokken yazılmaz. Rapor: bilinen çok kampanyalı veriyle `byStatus`/`byType` (sıfır doldurma, sabit sıra), `totals` (toplam sayılar üzerinden oranlar), `topCampaigns` sıralaması + 10 sınırı, `startDate`/`createdAt` yedeği ile aralık sınırları (kiracı saat dilimi, uçlar dahil), varsayılan aralık, ters aralık → 400, silinmiş kampanya hariç.
- *Kiracı izolasyonu (zorunlu):* A'nın kampanyası/üyesi/metriği/`by-member`/denetimi B'ye görünmez (404 / boş liste); B, A'nın kampanyasına üye ekleyemez, durum değiştiremez, silemez; B'nin rapor özeti A'yı saymaz; A'nın lead kimliği B'nin kampanyasına eklenmeye çalışılırsa `skipped: not_found`. `Crm.Tests.TenantIsolation` ve mimari testler yeni modülü otomatik kapsar (yeni tablolar `TenantAggregateRoot`).
- *İzinler (403):* yazma izni olmayan kullanıcı (yalnız `crm.campaigns.read`) tüm yazma uçlarında 403, okuma uçlarında 200; izinsiz kullanıcı okumada 403; rapor `crm.reports.read` olmadan 403; Standard rolü (yeni + senkronlanan eski organizasyon) iki yeni izni taşır, `GET /permissions` 20 anahtar; doğrulama yetkiden önce (geçersiz gövdeli yetkisiz istek 400).
- *Denetim:* kampanya oluştur/güncelle/durum/sil ve üye ekle/çıkar/durum kayıtları, `GET /audit?entityType=Campaign&entityId=` `crm.campaigns.read` ile çalışır, `LeadConverted` kaynaklı üye değişikliği denetlenir.

**Web** (Vitest + Testing Library, sahte API): liste filtre/sıralama/sayfa ↔ URL senkronu; oluştur/düzenle formu (bitiş < başlangıç istemci hatası, sunucu `campaign.invalid_date_range` ve `owner.not_member` alan eşlemesi, negatif tutar); durum menüsü her durumda yalnız geçerli hedefleri gösterir ve 409'u toast'a çevirir; üyeler sekmesi (ekleme diyaloğu ve sonuç özeti, toplu durum + çıkarma onayı, `converted` satırının kilitli seçicisi, `memberMissing` gösterimi, kapalı kampanyada ekleme devre dışı); metrik kartları (değerler, `costPerLead` yokken "—"); Potansiyeller/Kişiler listesinde seçim → "Kampanyaya ekle" (seçim sayfa değişince temizlenir, doğru `memberType` + `memberIds`, özet toast'ı); lead/kişi "Kampanyalar" sekmesi (liste, ekleme, çıkarma, dönüşmüş lead'de ekleme gizli); **izin gizleme** (`write` yoksa tüm yazma düğmeleri/toplu eylem gizli, `read` yoksa menü + rota `NoAccess` + sekme gizli, `crm.reports.read` yoksa Pazarlama sekmesi ve dashboard kartı gizli); rapor aralığı → istek parametreleri; TR/EN anahtar eşitliği. Kapı: `tsc`, `eslint`, `vitest`, `build`.

**Merge kapısı (pano kuralı):** `dotnet build Crm.slnx --no-incremental` 0 uyarı, `dotnet format --verify-no-changes`, `dotnet test Crm.slnx`, mimari + kiracı izolasyon testleri, yeni her kiracı varlığı için çapraz-kiracı testi, canlı duman testi (kart portu/veritabanı `crm_marketing`, örn. API 5083): kampanya aç → lead'leri toplu ekle → lead'i dönüştür → üyelik `converted` + metrikler/rapor doğrulanır.

## Uygulama notları
- **Sıcak dosyalar (yalnız ekleme):** `ModuleCatalog.cs` (`new MarketingModule()`), `Crm.Migrator/Program.cs`, `Crm.Worker/Program.cs` (Marketing outbox + `LeadConverted` işleyici kaydı + `AddMarketingContractServices`), `TestFixture.cs` (Respawn `marketing`), `SharedResource{,.en}.resx`, `docs/architecture/backend.md` (Marketing bölümü), web sıcak dosyaları (yukarıda), `web/README.md`. `Identity.Contracts/Permissions.cs` ve `SystemRoleDefinitions.cs` **değişmez**.
- Migration: `dotnet dotnet-ef migrations add InitialMarketing --project src/Modules/Marketing/Crm.Modules.Marketing.Infrastructure --startup-project src/Crm.Migrator --context MarketingDbContext -o Persistence/Migrations`. Tüm indeksler `tenant_id` ile başlar (K3).
- Sales tarafı değişikliği yalnız `ILeadStatusLookup` (+ uygulaması `LeadStatusLookup`, kayıt) ve buna ait bir birim/HTTP testidir; Sales'te başka davranış değişmez. C-M6A/B ile çakışmayı azaltmak için ekleme ayrı bir dosyada yapılır (`Sales.Contracts/LeadStatusLookup.cs`).
- Denetimde fiziksel silme (`CampaignMember` `deleted`) `AuditLogInterceptor` tarafından yakalanmalıdır; yakalanmıyorsa arayüzü değiştirmeden interceptor doğrulanır ve test eklenir.

## Kapsam dışı
E-posta gönderimi/şablonları/zamanlama (bildirim ve e-posta altyapısı yok), açılış sayfaları/form oluşturucu, sosyal medya paylaşımı/entegrasyonları, segmentasyon ve dinamik listeler (kuralla otomatik üye kümesi), abonelikten çıkma sayfaları/toplu e-posta tercihleri (`unsubscribed` yalnız elle işaretlenen bir durumdur), CSV ile üye içe aktarma, kampanya bazlı ROI ve kazanılan fırsat atfı (fırsat → kampanya bağı, gelir gerçekleşimi; `expectedRevenue` yalnız girilen bir alandır), lead'e kampanya kolonu/otomatik `source = campaign`, dönüşen lead'in kişisinin otomatik üyeliği, üye listesinde ada göre arama/sıralama ve e-posta/şirket sütunları (Sales verisi; ileride `IRecordLookup` arama uzantısıyla), kampanya kopyalama/şablon, hiyerarşik (alt) kampanyalar, kampanya bütçesi onayı, çok para birimli birleştirme (M3 sınırlaması), silinmiş lead/kişi üyeliklerinin otomatik temizliği (yumuşak bağ; sonra temizlik işi), üye bazlı denetim arayüzü, Marketing integration event yayını.
