# Milestone 6B — Servis/Destek: Talepler, yorumlar, SLA (plan + HTTP kontratı)

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Kararlar: [kararlar.md](../architecture/kararlar.md) (K5, K7, K10, K14) · Önceki: [M2](m2-api-kontrat.md), [M3](m3-aktivite-rapor.md), [M4](m4-workflow.md). Kart: C-M6B, dal `m6/service`. Biçim kuralları M2/M3/M4 ile aynıdır: taban yol `/api/v1`, JSON camelCase, enum'lar camelCase string, `null` alanlar yazılmaz, hatalar ProblemDetails + `code`, sayfalı liste `{ items, page, pageSize, totalCount }` (varsayılan 25, üst sınır 100), `sort` = `alan` veya `-alan`, oluşturma 201 + `Location` + gövde, güncelleme/eylem/silme 204.

**Çıktı:** Müşteri sorununu bir **talep** (case) olarak kaydedilir (firma/kişiye bağlı), bir temsilciye atanır, yanıtlanır (herkese açık yanıt / dahili not), çözülür ve kapatılır. Her talebin önceliğine göre **SLA süreleri** (ilk yanıt + çözüm) vardır; gecikme/risk listede ve detayda görünür. Raporlarda servis özeti, ana sayfada talep sayaçları bulunur.

## 1. Modül kararı
- Yeni modül `Sense.Crm.Modules.Service.{Domain,Application,Contracts,Infrastructure,Api}`, şema `service`, tek `ServiceDbContext`, migration `InitialService`. Neden ayrı modül: kendi yaşam döngüsü (durum makinesi), kendi sayaç ve SLA politikası tabloları var; Sales'e/Activities'e eklenirse o modüllerin sınırı şişer (K5). Rapor uçları M3'teki gibi **aynı modülde** (ayrı Reporting modülü yok, YAGNI).
- Bağımlılıklar yalnız `*.Contracts` üzerinden: `Sales.Contracts` (`IRecordLookup`: firma/kişi varlığı + adları; yeni `IContactAccountLookup`, bkz. §7), `Identity.Contracts` (`IMemberLookup`, `ITenantDirectory`/`TenantCalendarService`, `IAuditEntityPermissions`, `OrganizationCreated`, `CrmPermissions.ReportsRead`). Sales/Activities/Workflows modüllerine ve tersine başka bağ yok (mimari testler yeşil kalır).
- Yeni modülün `ModuleCatalog`, `Sense.Crm.Migrator`, `Sense.Crm.Worker` bağlantısı Sales/Activities ile aynı kalıptır (§8'deki sıcak dosyalar, yalnız ekleme).
- Migration komutu: `dotnet dotnet-ef migrations add InitialService --project src/Modules/Service/Sense.Crm.Modules.Service.Infrastructure --startup-project src/Sense.Crm.Migrator --context ServiceDbContext -o Persistence/Migrations`. İskelet: `./build/new-module.ps1 -Name Service`.

## 2. Varlıklar (hepsi kiracıya ait; kimlikler `Guid.CreateVersion7`, EF'te `ValueGeneratedNever`)

### 2.1 `Case` — tablo `service.cases` (`TenantAggregateRoot`, `IAuditLogged`, `ISoftDelete`)
| Alan | Kural |
|---|---|
| `number` | `C-2026-0001` (§3.1); kiracıda benzersiz, değişmez, silinmiş talebin numarası yeniden kullanılmaz |
| `subject` | zorunlu, ≤ 200 |
| `description` | ≤ 8000 |
| `accountId?`, `contactId?` | yumuşak bağ (M3 kuralı): bağlı kayıt sonradan silinse talep kalır, `accountName`/`contactName` boş döner. Bağ yalnız değiştiğinde yeniden doğrulanır |
| `status` | `new\|open\|pending\|resolved\|closed`; oluşturmada her zaman `new` (§3.2) |
| `priority` | `low\|normal\|high\|urgent`, varsayılan `normal` |
| `channel` | `email\|phone\|web\|other`, varsayılan `other` (yalnız kayıt bilgisidir; e-posta ile talep açma kapsam dışı) |
| `assignedUserId?` | aktif üye olmalı (`owner.not_member`, 400 — `IMemberLookup.IsActiveMemberAsync`); **verilmezse atanmamış** (temsilci kuyruğu); mevcut atanan korunurken üyelik yeniden sorgulanmaz |
| `resolutionNote?` | ≤ 4000; çözüm/kapatma notu (§3.2) |
| `firstResponseAt?`, `resolvedAt?`, `closedAt?` | UTC; kurallar §3.2–3.3 |
| `reopenCount` | yeniden açma sayısı, başlangıç 0 |
| `firstResponseDueAt`, `dueAt` | SLA hedef anları (UTC), oluşturmada hesaplanıp saklanır (§3.4). `dueAt` = çözüm hedefi |
| `slaAnchorAt` | çözüm SLA'sının başlangıç anı: oluşturma anı, yeniden açmada açılış anı |
| `firstResponseWarnAt`, `resolutionWarnAt` | "risk altında" eşik anları (§3.4); SQL'de `slaState` süzmek için saklanır, yanıtta yer almaz |
| `createdAt`, `updatedAt?`, `createdByUserId` | denetim alanları (mevcut Sales/Activities kalıbı) |
| eşzamanlılık | PostgreSQL `xmin` eşzamanlılık belirteci; çakışma → `general.concurrency_conflict` (409, mevcut `GlobalExceptionHandler`) |

Domain davranışları (`Case` üzerinde, birim testli): `Create`, `Update`, `ChangeStatus`, `ChangePriority`, `Assign`, `AddComment` (ilk yanıt kuralı). Durum makinesi ve SLA hesabı **domain'de saf** (`CaseSlaCalculator`, `TimeProvider`/`DateTime` parametreli) — HTTP/DB'siz test edilir.

### 2.2 `CaseComment` — tablo `service.case_comments` (`TenantAggregateRoot`, `IAuditLogged`; değişmez)
`caseId`, `visibility` (`public|internal`), `body` (1–8000), `authorUserId`, `createdAt`. Düzenleme/silme yoktur (kapsam dışı; denetim kaydı bütünlüğü). `SensitiveFields = { body }`: denetim kaydında `***` maskelenir (yorumlar kişisel veri taşıyabilir, K13 KVKK). Kayıt yalnız talep uçlarıyla oluşur (ayrı `/comments` kaynağı yok). Tüm yazarlar organizasyon üyeleridir ("temsilci"); müşteri kimliği/portalı yoktur.

### 2.3 `CaseEvent` — tablo `service.case_events` (`TenantEntity`, yalnız ekleme, değişmez)
Zaman çizelgesi için: `caseId`, `type` (`created|statusChanged|priorityChanged|assigned`), `actorUserId`, `fromValue?`, `toValue?` (durum/öncelik camelCase metin, atamada kullanıcı kimliği metni; atanmamış = boş), `note?` (çözüm/kapatma notu), `createdAt`. **Denetim dışıdır** (kendisi zaten değişmez bir olay günlüğü; çift yazımı önlemek için); `Case` ve `CaseComment` `audit.audit_log_entries`'e yazılır. Bu bilinçli sapma Lead onayına sunulur (kural "tüm varlıklar denetimli" idi; `CaseEvent` ve `CaseCounter` istisnadır).

### 2.4 `CaseCounter` — tablo `service.case_counters` (`TenantEntity`, denetimsiz teknik tablo)
`(tenantId, year)` bileşik anahtar, `lastValue int`. Yalnız §3.1'deki SQL ile yazılır.

### 2.5 `SlaPolicy` — tablo `service.sla_policies` (`TenantAggregateRoot`, `IAuditLogged`)
Öncelik başına bir satır (kiracıda tam 4): `priority`, `firstResponseMinutes`, `resolutionMinutes`. Benzersiz `(tenantId, priority)`.

### 2.6 İndeksler (hepsi `TenantId` ile başlar)
`cases`: `(tenant, number)` benzersiz; `(tenant, status, created_at desc)`; `(tenant, assigned_user_id, status)`; `(tenant, account_id)`; `(tenant, contact_id)`; `(tenant, due_at)`. `case_comments`/`case_events`: `(tenant, case_id, created_at)`. `sla_policies`: `(tenant, priority)` benzersiz. Enum'ların sıralaması (`status`, `priority`) metin değil **enum sırasıdır** (Activities `priority` sıralamasındaki yöntemle).

## 3. İş kuralları

### 3.1 Numaralama `C-{yıl}-{sıra:D4}`
- Yıl, oluşturma anının **kiracı saat dilimindeki** takvim yılıdır (`TenantCalendarService`); sıra her `(kiracı, yıl)` için 1'den başlar, 9999'u aşarsa basamak artar (`C-2026-10000`).
- Eşzamanlılık güvenli sayaç: talep INSERT'iyle **aynı transaction'da**, doğrulamalardan sonra en son adım olarak
  `INSERT INTO service.case_counters (tenant_id, year, last_value) VALUES (@t, @y, 1) ON CONFLICT (tenant_id, year) DO UPDATE SET last_value = case_counters.last_value + 1 RETURNING last_value`.
  Satır kilidi eşzamanlı oluşturmaları commit'e kadar sıraya dizer → numara benzersiz ve **boşluksuzdur**; transaction geri alınırsa sayaç da geri alınır. `(tenant, number)` benzersiz indeksi ikinci emniyettir. Ham SQL `TenantId`'yi `ITenantContext`'ten kendisi yazar.

### 3.2 Durum makinesi
`POST /cases/{id}/status` `{ status, resolutionNote? }`. İzinli geçişler:

| Mevcut → | new | open | pending | resolved | closed |
|---|---|---|---|---|---|
| **new** | — | ✔ | ✔ | ✔ | ✔ |
| **open** | ✘ | — | ✔ | ✔ | ✔ |
| **pending** | ✘ | ✔ | — | ✔ | ✔ |
| **resolved** | ✘ | ✔ (yeniden açma) | ✘ | — | ✔ |
| **closed** | ✘ | ✔ (yeniden açma, §3.3) | ✘ | ✘ | — |

- Tablo dışı geçiş (özellikle `new`'e dönüş): `case.invalid_transition` (409). **Aynı duruma geçiş idempotent 204** (olay/yan etki yok).
- **Çözüm notu zorunlu:** hedef `resolved` ise; ya da hedef `closed` ve mevcut durum `resolved` değilse (çözülmeden kapatma). Boş/boşluk → `case.resolution_required` (400). Diğer geçişlerde `resolutionNote` yok sayılır.
- `resolved` (veya çözülmeden `closed`): `resolvedAt = şimdi`, `resolutionNote` yazılır; **`firstResponseAt` hâlâ boşsa çözüm anı ilk yanıt sayılır** (`firstResponseAt = resolvedAt`) — böylece hiç yorumsuz çözülen talep ilk yanıt SLA'sında sonsuza dek "bekliyor" kalmaz. `closed`: `closedAt = şimdi`. `resolved → closed`: `resolvedAt` ve not korunur.
- Her geçiş `CaseEvent(statusChanged, from, to, note)` yazar; çözülme anı `CaseResolved` olayını doğurur (§5.3).
- Otomatik geçiş: `new` durumundaki talebe **ilk herkese açık yorum** eklenince durum `open` olur (`statusChanged` olayı, aktör = yorumu yazan).
- Eylem izinleri: `PUT`, `priority`, `assign` yalnız **aktif** talepte (`new|open|pending`), aksi `case.not_active` (409). Yorum `closed` talepte `case.closed` (409), `resolved` talepte serbesttir. `DELETE` her durumda (yumuşak).

### 3.3 Yeniden açma
`resolved → open`: süre sınırı yok. `closed → open`: `closedAt`'tan itibaren **14 gün** içinde (`CaseRules.ReopenWindowDays = 14`; sabit, `Service:ReopenWindowDays` ile ayarlanabilir), aksi `case.reopen_window_expired` (409). Yeniden açma: `reopenCount++`, `resolvedAt = closedAt = null`, `resolutionNote = null` (eski not zaman çizelgesindeki olayda kalır), `slaAnchorAt = şimdi`, `dueAt` mevcut önceliğin çözüm süresiyle **yeniden hesaplanır** (§3.4); `firstResponseAt/DueAt` değişmez.

### 3.4 SLA (duvar saati dakikası; iş saatleri/tatil takvimi kapsam dışı)
- **Hesap anı:** oluşturmada, öncelik değişiminde ve yeniden açmada. Politika satırı o anki değerlerle okunur; **politika sonradan değişirse mevcut taleplerin süreleri değişmez** (anlık görüntü). Ayar ekranı bunu metinle söyler.
- `firstResponseDueAt = createdAt + firstResponseMinutes(priority)`; `dueAt = slaAnchorAt + resolutionMinutes(priority)`.
- Öncelik değişimi: `firstResponseDueAt` yalnız `firstResponseAt` boşsa `createdAt + yeni süre` ile, `dueAt` `slaAnchorAt + yeni süre` ile yeniden hesaplanır (şimdiden değil, **özgün başlangıçtan**: önceliği yükseltmek hedefi geçmişe düşürüp talebi ihlal edebilir, düşürmek uzatır — kasıtlı; kötüye kullanım yok).
- **Uyarı eşiği:** toplam sürenin %80'i. `firstResponseWarnAt = createdAt + firstResponseMinutes × 48 sn`, `resolutionWarnAt = slaAnchorAt + resolutionMinutes × 48 sn` (tam tamsayı saniye; kayan nokta yok). Sabit, ayarlanamaz.
- **Okuma anında hesap** (`now` = `TimeProvider`; yanıt ve süzgeçte aynı fonksiyon):
  - `firstResponseBreached = (firstResponseAt ?? now) > firstResponseDueAt`
  - `resolutionBreached = (resolvedAt ?? now) > dueAt` (kapalı talepte `resolvedAt` her zaman dolu)
  - `isSlaBreached = firstResponseBreached || resolutionBreached`
  - `slaState`: `breached` eğer `isSlaBreached`; değilse talep **aktif** (`new|open|pending`) ve (`firstResponseAt` boş **ve** `now >= firstResponseWarnAt`) **veya** `now >= resolutionWarnAt` ise `atRisk`; aksi `ok`. Çözülmüş/kapalı ve ihlalsiz talep hep `ok`.
  - **Sınırlar:** hedef anın kendisi (`now == dueAt`) ihlal **değil**, `atRisk`'tir; bir tik sonrası ihlaldir. `now == warnAt` `atRisk`'tir.
  - Not: `pending` durumunda SLA duraklamaz (kapsam dışı).
- Varsayılan politikalar (dakika; ilk yanıt / çözüm): `urgent` 60 / 240 · `high` 240 / 1440 · `normal` 480 / 4320 · `low` 1440 / 10080. Doğrulama: `1 ≤ firstResponseMinutes ≤ resolutionMinutes ≤ 525600` (1 yıl).

### 3.5 Yorumlar
- `POST /cases/{id}/comments` `{ visibility*, body* }` (görünürlük **açıkça verilir**, varsayılan yok: yanlışlıkla herkese açık yorum riski istemcide varsayılan `internal` değil kullanıcı seçimidir; sunucu eksikse `validation`).
- Talep `firstResponseAt` boşken **herkese açık** yorum → `firstResponseAt = şimdi` (aynı transaction); dahili yorum ilk yanıt **sayılmaz**; sonraki yorumlar `firstResponseAt`'i değiştirmez. Yorum ile talep güncellemesi eşzamanlı yarışırsa (xmin) handler tek sefer yeniden yükleyip dener; yine çakışırsa 409.

## 4. HTTP kontratı

İzinler: okuma `crm.cases.read`, yazma `crm.cases.write` (bir kaynağın yazma izni okumayı **içermez**; Administrator/Standard ikisini de taşır). Doğrulama (FluentValidation, `validation` + `errors`) yetkiden önce çalışır (M2 kuralı). Başka organizasyonun kaydı her zaman `not_found` (404). `GET /cases/summary` ile `/{id}` çakışmasın diye rota `{id:guid}` kısıtlıdır.

### 4.1 Talepler `/cases`
Liste öğesi `CaseListItem` (açıklama ve çözüm notu **yok**), detay `CaseDetail` (hepsi). Ortak alanlar:

```json
{
  "id": "…", "number": "C-2026-0001", "subject": "Fatura hatalı",
  "status": "open", "priority": "high", "channel": "email",
  "accountId": "…", "accountName": "Acme A.Ş.", "contactId": "…", "contactName": "Ayşe Yılmaz",
  "assignedUserId": "…", "assignedUserName": "Mert Kaya",
  "reopenCount": 0,
  "firstResponseAt": "2026-09-19T08:30:00Z", "resolvedAt": null, "closedAt": null,
  "firstResponseDueAt": "2026-09-19T12:00:00Z", "dueAt": "2026-09-20T08:00:00Z",
  "isSlaBreached": false, "slaState": "atRisk",
  "firstResponseBreached": false, "resolutionBreached": false,
  "createdAt": "2026-09-19T08:00:00Z", "updatedAt": "…", "createdByUserId": "…", "createdByName": "…"
}
```
`CaseDetail` ek olarak `description?`, `resolutionNote?`. `slaState`: `ok|atRisk|breached`. Adlar `IRecordLookup.GetDisplayNamesAsync` (tür başına tek sorgu) ve `IMemberLookup.GetDisplayNamesAsync` (pasif üyeler dahil) ile toplu çözülür; ad çözümü bağlı kaydın okuma iznine bakmaz (M3 ile aynı bilinen sınırlama).

| Yol | Notlar |
|---|---|
| `GET /cases` | Filtreler: `q` (`number` + `subject`, `ILIKE`, joker kaçışlı), `status` ve `priority` (**virgülle çoklu**, örn. `status=new,open,pending`), `channel`, `assignedUserId`, `unassigned=true` (atanmamışlar; `assignedUserId` ile birlikte verilirse `validation`), `accountId`, `contactId`, `slaState` (`ok\|atRisk\|breached`, tek değer). `sort`: `number`, `subject`, `status`, `priority` (low<normal<high<urgent), `dueAt`, `createdAt`, `updatedAt`; **varsayılan `-createdAt`**; bilinmeyen alan yok sayılır; her zaman `Id` ile kararlı. Silinmişler yok. Bilinmeyen enum değeri → `validation` |
| `GET /cases/summary` | `{ openCount, overdueCount, mineCount, unassignedCount }` — `open` = aktif (`new\|open\|pending`); `overdue` = aktif **ve** `isSlaBreached` (liste süzgeci karşılığı: `status=new,open,pending&slaState=breached`); `mine` = aktif ve `assignedUserId = çağıran`; `unassigned` = aktif ve atanmamış |
| `GET /cases/{id}` | `CaseDetail` |
| `POST /cases` | Gövde `{ subject*, description?, accountId?, contactId?, priority?, channel?, assignedUserId? }` → 201 `CaseDetail`. Sıra: doğrula → firma/kişi/üye kontrolleri → SLA politikası (yoksa tembel tohumla, §6) → sayaç → INSERT + `created` olayı. `contactId` verilip `accountId` verilmezse ve kişinin firması varsa `accountId` **kişinin firmasından türetilir**; ikisi de verilir ve kişinin firması farklıysa `case.contact_account_mismatch` (400); firması olmayan kişi herhangi bir firmayla verilebilir. Firma/kişi yok/silinmiş/başka organizasyon → `case.account_not_found` / `case.contact_not_found` (404, M3 kalıbı) |
| `PUT /cases/{id}` | `{ subject*, description?, accountId?, contactId?, channel? }` → 204, **tam değiştirme** (gönderilmeyen isteğe bağlı alan temizlenir; `channel` verilmezse korunur); durum/öncelik/atanan/SLA'yı **değiştirmez**. Firma/kişi kuralları POST ile aynı (yalnız değiştiyse yeniden doğrulanır; türetme yalnız `accountId` yoksa ve `contactId` değiştiyse). Aktif değilse `case.not_active` |
| `DELETE /cases/{id}` | 204, yumuşak silme; yorumlar/olaylar kalır ama okunamaz |
| `POST /cases/{id}/status` | `{ status*, resolutionNote? }` → 204 (§3.2, §3.3) |
| `POST /cases/{id}/priority` | `{ priority* }` → 204; SLA hedefleri yeniden hesaplanır (§3.4); aynı öncelik idempotent 204; aktif değilse `case.not_active` |
| `POST /cases/{id}/assign` | `{ assignedUserId }` (`null` = atamayı kaldır) → 204; aktif üye değilse `owner.not_member`; aynı atanan idempotent 204; aktif değilse `case.not_active` |
| `POST /cases/{id}/comments` | `{ visibility*, body* }` → 201 `{ id, caseId, visibility, body, authorUserId, authorName, createdAt }` (§3.5); `crm.cases.write` |
| `GET /cases/{id}/timeline?page&pageSize` | Yorumlar + olaylar birleşik, **en yeni önce** (`createdAt desc, id desc`; iki tablonun `UNION ALL` projeksiyonu), sayfalı `{ items, page, pageSize, totalCount }`. Öğe: `{ id, type, occurredAt, actorUserId?, actorName?, visibility?, body?, from?, to?, fromName?, toName?, note? }`; `type` = `comment` (`visibility`, `body` dolu), `created`, `statusChanged` (`from`,`to` durum, çözüm/kapatmada `note`), `priorityChanged` (`from`,`to` öncelik), `assigned` (`fromName`/`toName` kullanıcı adları, boş = atanmamış) |

Örnek zaman çizelgesi öğeleri: `{ "id":"…","type":"comment","occurredAt":"…","actorUserId":"…","actorName":"Mert Kaya","visibility":"internal","body":"Faturayı muhasebeye ilettim." }` · `{ "type":"statusChanged","from":"open","to":"resolved","note":"Fatura düzeltildi." }` · `{ "type":"assigned","fromName":null,"toName":"Mert Kaya" }`.

### 4.2 SLA politikaları `/service/sla-policies` (`org.settings.manage`)
- `GET /service/sla-policies` → `[{ priority, firstResponseMinutes, resolutionMinutes }]` **her zaman 4 satır**, `low, normal, high, urgent` sırasıyla (eksikse tembel tohumlanır).
- `PUT /service/sla-policies` `{ policies: [{ priority, firstResponseMinutes, resolutionMinutes }] }` → 204. Dizi tam 4 öncelik içermeli (her biri bir kez), aksi `validation` (`errors["policies"]`); sayı kuralları §3.4 (`errors["policies[2].firstResponseMinutes"]`). Değişiklik **yalnız yeni hesaplamaları** etkiler (§3.4). Audit: `SlaPolicy` değişiklikleri (`org.audit.read` ile).

### 4.3 Raporlar (`crm.reports.read`, Service modülü)
`from`/`to` (`YYYY-MM-DD`, uçlar dahil, kiracı saat diliminde takvim günü, varsayılan son 12 ay, ters/10 yıldan uzun aralık `validation`) — M3 §11 ile aynı (`TenantCalendarService.ResolveReportRangeAsync`). **Kohort: `createdAt` aralıkta olan, silinmemiş talepler.** "İhlal" `now` ile §3.4'teki `isSlaBreached` tanımıdır (rapor anındaki durum).

- `GET /reports/service/summary?from&to` →
```json
{ "from": "2025-10-01", "to": "2026-09-19", "totalCount": 120, "resolvedCount": 90,
  "byStatus":   [ { "status": "new", "count": 4 }, { "status": "open", "count": 20 }, { "status": "pending", "count": 6 }, { "status": "resolved", "count": 40 }, { "status": "closed", "count": 50 } ],
  "byPriority": [ { "priority": "low", "count": 30 }, { "priority": "normal", "count": 60 }, { "priority": "high", "count": 22 }, { "priority": "urgent", "count": 8 } ],
  "avgFirstResponseMinutes": 143.5, "avgResolutionMinutes": 1210.0,
  "slaBreachedCount": 18, "slaBreachRate": 0.15 }
```
  `byStatus`/`byPriority` **her zaman tüm değerleri** sırasıyla döner (boş = 0; sıra enum sırası). `resolvedCount` = kohortta `resolvedAt` dolu olanlar. `avgFirstResponseMinutes` = `firstResponseAt` dolu olanların `firstResponseAt − createdAt` ortalaması (dakika, 1 ondalığa yuvarlanmış); `avgResolutionMinutes` = `resolvedAt` dolu olanların `resolvedAt − createdAt` ortalaması (yeniden açılmışta `resolvedAt` boş olduğundan dışarıda); örneklem yoksa alan **yazılmaz** (`null`). `slaBreachRate = slaBreachedCount / totalCount` (0..1, 4 ondalık; `totalCount = 0` → 0).
- `GET /reports/service/by-assignee?from&to` → `[{ assignedUserId?, assignedUserName?, totalCount, openCount, resolvedCount, avgFirstResponseMinutes?, avgResolutionMinutes?, slaBreachedCount }]` — aynı kohort ve tanımlar; `openCount` aktif (güncel), atanmamış talepler tek satır olarak `assignedUserId`/`assignedUserName` **olmadan** gelir; sıra `totalCount` azalan, sonra ad; adlar `IMemberLookup` (pasif üyeler dahil).

### 4.4 Denetim
`GET /audit?entityType=Case&entityId={id}` (Identity'deki mevcut uç, değişmez): `ServiceAuditEntities` → `Case` ve `CaseComment` için `crm.cases.read` (kayıt bazlı okuma), `SlaPolicy` yalnız `org.audit.read`. `changes` alanları camelCase; enum'lar camelCase string; `CaseComment.body` `***`.

### 4.5 Yeni hata kodları (metinler `SharedResource.resx` tr/en, alanlar `field.*`)
| code | HTTP | Ne zaman |
|---|---|---|
| `case.account_not_found` | 404 | Firma aktif organizasyonda yok |
| `case.contact_not_found` | 404 | Kişi aktif organizasyonda yok |
| `case.contact_account_mismatch` | 400 | Kişinin firması, verilen firmadan farklı |
| `case.invalid_transition` | 409 | Durum makinesinde izinsiz geçiş (`from`, `to` argümanları) |
| `case.resolution_required` | 400 | Çözme / çözülmeden kapatmada not boş |
| `case.reopen_window_expired` | 409 | Kapalı talep 14 günden sonra açılmak istendi |
| `case.not_active` | 409 | Aktif olmayan talepte PUT/öncelik/atama |
| `case.closed` | 409 | Kapalı talepte yorum |
| `owner.not_member` | 400 | Atanan aktif üye değil (mevcut kod) |
| `general.concurrency_conflict` | 409 | xmin çakışması (mevcut kod) |

Ayrıca mevcut `validation`, `forbidden`, `not_found`, `auth.unauthenticated`.

## 5. İzinler, denetim, olaylar

### 5.1 İzinler
`Service.Contracts.ServicePermissions` (`Module = "cases"`, `Group = "crm"`): `crm.cases.read`, `crm.cases.write`; `ServiceModule.Permissions` ile kataloğa katılır (`GET /permissions` **20** anahtar; M6A/M6C de ekleyeceğinden testler sayı yerine `Contain` doğrular). `SystemRoleDefinitions` **değişmez**: Administrator = tüm katalog; Standard = `crm.*` (onay hariç) → iki yeni anahtarı otomatik alır. Mevcut organizasyonlar API açılışında `SystemRolePermissionSynchronizer` ile senkronlanır. SLA ayarı `org.settings.manage`, raporlar `crm.reports.read` (Identity.Contracts'ta kalır). Web `PERMISSIONS` sabitine `crmCasesRead/Write` eklenir, `permission.*` çeviri anahtarları M4 kalıbıyla tr/en eklenir.

### 5.2 Denetim
`Case`, `CaseComment`, `SlaPolicy` `IAuditLogged` (aynı transaction, kiracı bazlı). `Case` türetilmiş alan değişiklikleri (`firstResponseDueAt`, `dueAt`, `*WarnAt`, `slaAnchorAt`) denetim farkında görünür (gürültü kabul; ayrı dışlama mekanizması eklenmez). `ServiceAuditEntityPermissions : IAuditEntityPermissions` (Activities kalıbı), `GetEntityAuditHandler` değişmez.

### 5.3 Integration event `CaseResolved` (`Service.Contracts`, K10)
```csharp
public sealed record CaseResolved(Guid TenantId, Guid CaseId, string CaseNumber, Guid? AccountId, Guid? ContactId,
    string Priority, Guid? AssignedUserId, DateTime ResolvedAt, int ResolutionMinutes, bool SlaBreached,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);
```
`ChangeStatus` handler'ı `resolvedAt`'i yazan her geçişte (`→ resolved` ve çözülmeden `→ closed`) `IIntegrationEventOutbox.Enqueue` ile **aynı `SaveChanges`/transaction**'da yayınlar; yeniden açılıp tekrar çözülen talep yeni bir olay üretir; idempotent aynı-durum geçişi ve hatalı geçiş olay üretmez. `ResolutionMinutes` = `createdAt`'tan `resolvedAt`'e tam dakika (aşağı yuvarlanır); `SlaBreached` = `resolvedAt` anında §3.4 değerlendirmesi. Bugün tüketicisi yoktur (bildirim/workflow için hazır); Worker `OutboxPollingService<ServiceDbContext>` çalıştırır.

## 6. SLA politikası tohumlama (Sales `DefaultPipelineSeeder` kalıbı)
- `IDefaultSlaPolicySeeder.EnsureAsync(tenantId)` (Application portu) + `DefaultSlaPolicySeeder` (Infrastructure): kiracı kapsamında, organizasyon başına PostgreSQL advisory lock (`service.sla_policies:{tenantId:N}`, transaction ömürlü) + kilit sonrası yeniden kontrol; **eksik öncelikler** varsayılan değerlerle eklenir (4 satır tamam ise hiçbir şey yapmaz → idempotent ve eşzamanlı çağrıya dayanıklı; `(tenant, priority)` benzersiz indeksi son emniyet). Dile bağlı metin olmadığından locale kullanılmaz.
- Üç yol: (1) **Worker**: Identity'nin `OrganizationCreated` olayını tüketen `OrganizationCreatedSlaHandler : IIntegrationEventHandler<OrganizationCreated>` (Sales'in aynı olay için mevcut handler'ıyla birlikte kayıtlı); (2) **API açılışı**: `DefaultSlaPolicySyncHostedService` — `ITenantDirectory.ListAllAsync` ile M1–M6A'da açılmış tüm organizasyonlar (hata başlatmayı durdurmaz, sonraki açılışta tekrarlanır); (3) **tembel güvence**: `SlaPolicyProvider` talep oluşturma/öncelik değiştirme/yeniden açma ve `GET /service/sla-policies` başında politika eksikse `EnsureAsync` çağırır (Worker olayı henüz işlemediyse kayıttan hemen sonra talep açmak çalışır).

## 7. Contracts eklemeleri (küçük)
- `Sales.Contracts`: `record ContactAccountLink(Guid ContactId, Guid? AccountId)` ve `interface IContactAccountLookup { Task<ContactAccountLink?> FindAsync(Guid contactId, CancellationToken ct = default); }` (kişi yok/silinmiş/başka kiracı → `null`; kişinin firması yoksa `AccountId = null`). Uygulama `Sales.Infrastructure` (kiracı + yumuşak silme filtresi altında), `AddSalesContractServices()` içinde kayıtlı (Worker'da da). Var olan `IRecordLookup` **değişmez** (arayüze üye eklemek sahte uygulamaları bozar).
- `Service.Contracts` (yeni): `ServicePermissions`, `ServiceAuditEntities`, `CaseResolved`.
- Identity/Shared/Kernel: **ekleme yok** (`TenantCalendarService`, `IMemberLookup`, `IAuditEntityPermissions`, `OrganizationCreated`, `ErrorCodes.ConcurrencyConflict` mevcut).

## 8. Sıcak dosyalar (yalnız ekleme; çakışmayı entegratör çözer)
`src/Sense.Crm.Api/ModuleCatalog.cs` (`new ServiceModule()`), `src/Sense.Crm.Migrator/Program.cs` (`ServiceDbContext`), `src/Sense.Crm.Worker/Program.cs` (`AddModuleDbContext<ServiceDbContext>`, `AddModuleHandlers`, `OutboxPollingService<ServiceDbContext>`, seeder + `OrganizationCreatedSlaHandler` kaydı), `tests/Sense.Crm.Tests.Shared/Fixtures/TestFixture.cs` (Respawn şeması `service`), `SharedResource{,.en}.resx` (`case.*` hata + `field.*` + `permission.*` anahtarları), `docs/architecture/backend.md` (yeni bölüm; merge sırasında), `Sense.Crm.slnx` (iskelet betiği), `web/src/App.tsx`, `web/src/config/navigation.ts`, `web/src/i18n.ts` (yeni ad alanı `service`), `web/public/locales/{tr,en}/{common,navigation}.json`, `web/README.md`. `Identity.Contracts/Permissions.cs` ve `SystemRoleDefinitions.cs` **dokunulmaz**.

## 9. Web
Ad alanı `service` (`web/public/locales/{tr,en}/service.json`, tr+en tam eşleşme). Yeni dosyalar: `types/index.ts` (ek: `CASE_STATUSES/PRIORITIES/CHANNELS`, `CaseListItem`, `CaseDetail`, `TimelineItem`, `SlaPolicy`, `ServiceSummaryReport`…, `PERMISSIONS.crmCasesRead/Write`), `services/cases.service.ts`, `hooks/use-cases.ts`, `hooks/use-sla-policies.ts` (+ `use-reports.ts` eki), `pages/crm/cases.tsx`, `pages/crm/case-detail.tsx`, `pages/settings/sla.tsx`, `components/service/*` (`case-badges`, `case-form-dialog`, `case-timeline`, `case-reply-box`, `case-actions`, `record-cases-tab`, `home-cases-widget`, `service-report`). Mevcut kalıplar yeniden kullanılır: `useListParams`, `DataTable`/`ListPageFrame`, `RecordDetailShell`, `OwnerSelect`, `AccountPicker`, `RecordAuditTab`, `toastApiError`/`applyValidationErrors`, `PermissionGuard`.

- **Menü ve rotalar:** sol menüde `Talepler` (`/app/cases`, `LifeBuoy` simgesi, `crm.cases.read`; Aktiviteler'in altında); `/app/cases/:id` (`crm.cases.read`); Ayarlar'da `SLA politikaları` (`/app/settings/sla`, `Timer` simgesi, `org.settings.manage`). Yetkisiz kullanıcı menüyü görmez, doğrudan URL'de `NoAccess`.
- **Talepler listesi:** URL ile eşzamanlı (`?page&pageSize&q&sort&status&priority&channel&assignedUserId&unassigned&slaState&accountId&contactId`; varsayılanlar URL'de yok; varsayılan sıra `-createdAt`). Hızlı filtre çipleri: `Açık` (`status=new,open,pending`), `Bana atanan` (`assignedUserId=<ben>` + Açık), `Atanmamış` (`unassigned=true` + Açık), `SLA aşıldı` (`slaState=breached` + Açık), `Tümü`; ayrıca durum/öncelik/kanal/atanan/SLA seçicileri ve arama. Sütunlar: numara (detay bağlantısı), konu, firma, durum rozeti, öncelik rozeti, atanan, **SLA rozeti**, oluşturma, hedef (`dueAt`). SLA rozeti: `ok` yeşil, `atRisk` sarı, `breached` kırmızı; ipucu: hedef anlar + "2 sa 15 dk kaldı / 40 dk geçti" (kiracı saat dilimi; kapalı taleplerde rozet yalnız ihlal varsa gösterilir). Başlıkta `Yeni talep` (`crm.cases.write`). Boş/yükleniyor/hata durumları.
- **Talep oluşturma diyaloğu:** konu*, açıklama, firma seçici, kişi seçici (firma seçiliyse kişiler o firmaya süzülür; kişi seçilince firma boşsa otomatik dolar), öncelik, kanal, atanan (isteğe bağlı, `Atanmamış` seçeneği). Sunucu hata eşlemesi: `validation` → alan hataları, `case.contact_account_mismatch`/`*_not_found` → ilgili alan; başarıda detay sayfasına gider. Önceden doldurma (`fixedAccount`/`fixedContact`) ile "Talep aç" için yeniden kullanılır.
- **Talep detayı:** başlıkta numara + konu + durum/öncelik/SLA rozetleri; sekmeler `Genel` ve `Denetim` (`RecordAuditTab entityType="Case"`). `Genel`: solda **zaman çizelgesi** (en yeni üstte; yorum kartları, `dahili` yorumlar sarı zemin + "Dahili" etiketi; durum/öncelik/atama olayları tek satır olarak; "Daha fazla göster" sayfalama, `pageSize=50`) ve üstünde **yanıt kutusu** (`Herkese açık yanıt` / `Dahili not` ayrımı `SegmentedControl` — **varsayılan seçili yok**, gönder düğmesi seçim yapılana dek pasif; metin alanı; Ctrl+Enter ile gönder; kapalı talepte gizli ve "Talep kapalı" bandı; `case.closed` hatası eşlenir). Sağda **bilgi paneli**: firma ve kişi (bağlantılı), kanal, atanan, oluşturan, oluşturma anı, ilk yanıt (gerçekleşme anı veya "bekleniyor — hedef …"), çözüm hedefi, çözülme/kapanış anı, çözüm notu, yeniden açma sayısı, iki SLA satırı (hedef, kalan/geçen süre, renk). **Eylemler** (`crm.cases.write`; yetkisizde gizli/pasif): `Durum` menüsü yalnız §3.2 tablosundaki geçerli hedefleri listeler (Çöz/Çözülmeden kapat → not diyaloğu, boşsa gönderilmez; Yeniden aç; kapalı ve 14 gün geçmişse `Yeniden aç` gizlenir ya da sunucu `case.reopen_window_expired` ile reddedince toast), `Öncelik` seçici (değişince SLA yeniden yüklenir), `Atanan` seçici (`Atanmamış` dahil), `Düzenle`, `Sil` (onay). Aktif olmayan taleplerde düzenle/öncelik/atama pasif. `general.concurrency_conflict` → toast + yeniden yükle.
- **Firma/Kişi detayı:** başlıkta `Talep aç` (`crm.cases.write`; firma/kişi sabit, diyalog önceden dolu), yeni sekme `Talepler` (`crm.cases.read`; ilgili `accountId`/`contactId` ile `GET /cases`, 50 satır, numara/konu/durum/öncelik/SLA/tarih, "Tümünü gör" → `/app/cases?accountId=…`), sekme etiketinde toplam sayı. Dokunulan dosyalar: `account-detail.tsx`, `contact-detail.tsx` (yalnız ekleme).
- **Ayarlar → SLA politikaları** (`org.settings.manage`): 4 satırlık tablo (öncelik, ilk yanıt dk, çözüm dk; yanında "= 8 sa" insan okur karşılık), `Kaydet` (değişiklik yoksa pasif), istemci doğrulaması §3.4 ile aynı, sunucu `validation` alan hataları satırlara eşlenir; açıklama metni: "Süreler duvar saati dakikasıdır (iş saatleri yok); değişiklik yalnız yeni talepleri, öncelik değişimlerini ve yeniden açmaları etkiler."
- **Raporlar:** `Raporlar` sayfasına `Servis` sekmesi (`tab=service`; sayfa zaten `crm.reports.read`): KPI kartları (toplam, çözülen, ort. ilk yanıt, ort. çözüm, SLA ihlali sayı + %), duruma göre halka, önceliğe göre çubuk grafik, "Temsilci bazlı" tablo, CSV indirme (istemci tarafı, mevcut `report-panel`). Süreler `2 sa 15 dk` biçiminde (`formatDuration(minutes)`); tarih aralığı seçici mevcut `from/to` parametrelerini gönderir.
- **Ana sayfa:** `crm.cases.read` olan kullanıcıya "Servis" kartı (`GET /cases/summary`: Açık, Geciken, Bana atanan, Atanmamış; her biri süzgeçli listeye bağlantı: Açık → `?status=new,open,pending`, Geciken → `?status=new,open,pending&slaState=breached`, …); izin yoksa kart yok.
- TR/EN: tüm metinler, durum/öncelik/kanal/SLA etiketleri, hata kodu çevirileri (`case.*`), `permission.crm.cases.*`.

## 10. Testler
**Backend** (`Sense.Crm.Modules.Service.Tests`; Testcontainers HTTP + `FakeTimeProvider`, M3/M4 fixture kalıbı; birim testler domain'de):
- **Numaralama:** ardışık `C-2026-0001…`; aynı kiracıda 20 eşzamanlı POST → 20 benzersiz, boşluksuz numara; iki kiracının sayaçları bağımsız; yıl sınırı kiracı saat diliminde (Europe/Istanbul'da 31 Ara 23:30 UTC = 1 Oca yerel → `C-2027-0001`, UTC hâlâ 2026); sayaç oluşturma başarısız olup transaction geri alınınca **numara yanmaz** (`SaveChanges` patlatılarak); silinen talebin numarası yeniden kullanılmaz; 10000. talepte biçim.
- **Durum makinesi** (tablo testi, 5×5 hücre): izinli geçişler, izinsiz → `case.invalid_transition` 409, `new`'e dönüş yok, aynı-durum idempotent 204 (olay/`CaseResolved` yok); çözüm notu zorunluluğu (`resolved`, çözülmeden `closed`; `resolved → closed`'da gerekmez; boşluk → `case.resolution_required`); `resolvedAt/closedAt/resolutionNote` alanları; hiç yorumsuz çözmede `firstResponseAt = resolvedAt`; yeniden açma: `reopenCount`, alanların temizlenmesi, `slaAnchorAt`/`dueAt` yeniden hesabı, `resolved → open` sınırsız, `closed → open` 14. gün dahil (gün 14 geçer, 14 gün + 1 sn `case.reopen_window_expired`); aktif olmayan talepte PUT/öncelik/atama `case.not_active`; kapalıda yorum `case.closed`, çözülmüşte yorum serbest.
- **SLA hesabı ve sınırları** (birim + HTTP): dört öncelik için varsayılanlarla `firstResponseDueAt/dueAt`; `now == dueAt` → ihlal değil (`atRisk`), `dueAt + 1 ms` → `breached`; `warnAt` tam eşiğinde (`normal`: +384 dk) `atRisk`, bir tik öncesi `ok`; ilk yanıt gelince ilk yanıt ihlali/riski durur ama çözüm riski sürer; ilk yanıt hedefinden **sonra** verilen yanıt ihlal olarak kalır; çözülmüş ihlalsiz talep `ok`, geç çözülmüş talep `breached`; öncelik yükseltme hedefi özgün başlangıçtan yeniden hesaplar (geçmişe düşebilir), düşürme uzatır; politika değişimi mevcut taleplerin hedeflerini **değiştirmez**, yeni talebe uygulanır; `slaState` süzgeci ve `summary.overdueCount` yanıttaki `slaState` ile aynı kümeyi verir; PUT politika doğrulaması (eksik/yinelenen öncelik, `first > resolution`, 0, sınır üstü → `validation` + alan yolları).
- **İlk yanıt kuralı:** herkese açık ilk yorum `firstResponseAt` yazar ve `new → open` yapar (olay aktörü yorum yazan); dahili yorum yazmaz; ikinci herkese açık yorum değiştirmez; çözülmüş talepte yorum `firstResponseAt`'i bozmaz; iki eşzamanlı ilk yorum → biri 409 değil, handler yeniden denemesiyle ikisi de kaydolur ve `firstResponseAt` tek kez yazılır.
- **CRUD/filtre/sıralama:** oluşturma (varsayılanlar, atanmamış, `contactId`'den `accountId` türetme, `case.contact_account_mismatch`, `owner.not_member`, firma/kişi 404), PUT tam değiştirme + değişmeyenleri korur, DELETE yumuşak, zaman çizelgesi sırası + sayfalama + olay türleri + ad çözümü; liste filtreleri (`q` joker kaçışı, `status`/`priority` çoklu değer, `unassigned`, `accountId`, `contactId`, `slaState`), sıralama anahtarları (durum/öncelik enum sırası), sayfalama üst sınırı, bilinmeyen enum → `validation`; `summary` sayaçları bilinen veriyle.
- **İzinler:** yazma izni olmayan kullanıcı her yazma ucunda 403 (geçersiz gövde yine 400: doğrulama yetkiden önce), okuma izni olmayan okuma uçlarında 403; `POST comments` yazma ister; SLA uçları `org.settings.manage` (yalnız `crm.cases.write` olan 403); raporlar `crm.reports.read`; `GET /permissions` `crm.cases.read/write`'ı içerir; Administrator ikisini, Standard ikisini de alır; mevcut organizasyon senkronu.
- **Kiracı izolasyonu:** A'nın talebi B'de liste/detay/zaman çizelgesi/durum/atama/yorum/silme uçlarında 404; B'nin firma/kişi/üyesini A'nın talebine bağlamak `case.*_not_found`/`owner.not_member`; SLA politikaları izole (B'nin PUT'u A'yı etkilemez, iki kiracı ayrı 4 satır); sayaçlar ayrı; rapor/özet yalnız kendi verisini sayar; `GET /audit?entityType=Case&entityId=<A'nın>` B'den boş; `Sense.Crm.Tests.TenantIsolation` yeni varlıkları (`Case`, `CaseComment`, `CaseEvent`, `CaseCounter`, `SlaPolicy`) filtre + `(TenantId, …)` indeksi için otomatik kapsar.
- **Rapor doğruluğu:** bilinen veri kümesiyle (farklı öncelik/durum/atanan, atanmamış, silinmiş talep hariç, aralık dışı talep hariç) `summary` (byStatus/byPriority sıfır dolgulu ve sıralı, ortalamalar elle hesaplanmış, örneklem yokken `null`, ihlal sayısı/oranı) ve `by-assignee` (atanmamış satırı, sıralama, pasif üye adı); aralık uçları kiracı saat diliminde (UTC+14 kiracı), varsayılan 12 ay, ters/10 yıldan uzun aralık `validation`; boş aralıkta sıfırlar.
- **Denetim ve olaylar:** oluşturma/durum/öncelik/atama `Case` denetim kayıtları, `CaseComment.body` `***`, `GET /audit?entityType=Case` `crm.cases.read` ile (org.audit.read olmadan), `SlaPolicy` yalnız `org.audit.read`; `CaseResolved` outbox: çözmede tam bir kayıt + doğru yük (`ResolutionMinutes`, `SlaBreached`), çözülmeden kapatmada bir kayıt, yeniden aç + çöz ikinci kayıt, hatalı/aynı-durum geçişte kayıt yok, komut başarısızsa kayıt yok.
- **Tohumlama:** kayıt sonrası Worker olayı 4 politika yazar; API açılışı M1–M6A organizasyonlarını tohumlar; art arda/eşzamanlı çağrı 4 satırda kalır (idempotent); Worker olayı işlemeden ilk talep açma tembel yolla çalışır; eksik tek satır tamamlanır.
- **Mimari:** `Sense.Crm.Tests.Architecture` yeni modülü otomatik kapsar (yalnız `Sales.Contracts`/`Identity.Contracts` bağı, handler'lar `sealed` + `{Eylem}Handler`); `Contracts` dışında başka modüle referans yok.

**Web** (Vitest + Testing Library, kontrata karşı sahte API, M3/M4 kalıbı): liste filtre/sıralama/sayfa ↔ URL senkronu ve hızlı filtre çipleri → doğru sorgu parametreleri; SLA rozeti üç durum + kapalı talep; oluşturma diyaloğu (zorunlu alan, kişiden firma türetme, sunucu `validation`/`case.contact_account_mismatch` alan eşlemesi, önceden doldurulmuş firma/kişi); detay: her durumda `Durum` menüsü yalnız §3.2 geçişlerini listeler, çöz/çözülmeden kapat not diyaloğu boşken gönderilmez ve `status` yükü doğru, yeniden aç; yanıt kutusu seçim yapılmadan pasif, herkese açık/dahili yükü (`visibility`), kapalı talepte gizli, dahili yorumun görsel ayrımı, zaman çizelgesi öğe türleri + sayfalama; `case.reopen_window_expired`/`case.not_active`/concurrency toast'ları; yetki kapıları (`crm.cases.write` yoksa eylemler/`Yeni talep`/`Talep aç` yok, `crm.cases.read` yoksa menü ve `Talepler` sekmesi yok, ana sayfa kartı gizli); `Talepler` sekmesi doğru `accountId`/`contactId` sorgusu; SLA ayar formu (dört satır, doğrulama, `PUT` yükü, sunucu hata eşlemesi, değişiklik yokken `Kaydet` pasif); rapor `Servis` sekmesi aralık → istek parametreleri ve CSV; tr/en `service.json` anahtar eşitliği.

**Merge kapısı (board.md):** `dotnet build Sense.Crm.slnx --no-incremental` 0 uyarı, `dotnet format --verify-no-changes`, `dotnet test Sense.Crm.slnx` yeşil, web `tsc`/`eslint`/`vitest`/`build` yeşil, mimari + kiracı izolasyon testleri yeşil, yeni her kiracı varlığı için çapraz-kiracı testi. Canlı duman testi ayrı port/veritabanında (`crm_service`, API 5082): kayıt → talep aç → yanıtla → çöz → kapat → yeniden aç, SLA rozeti ve raporlar tarayıcıda.

## 11. Kapsam dışı
E-postadan talep açma / e-posta yanıtı (email-to-case), iş saatleri ve tatil takvimleri (SLA duvar saatidir), `pending` durumunda SLA duraklatma, müşteri portalı ve müşteri yorumları, yükseltme (escalation) kuralları ve SLA bildirimleri/e-posta, bilgi bankası, dosya ekleri, yorum düzenleme/silme, otomatik atama (round-robin/kuyruk) ve talep-workflow'ları (Conductor kuralı olarak sonra eklenebilir; `CaseResolved` hazır), çözülmüş talebin otomatik kapanması (zamanlanmış iş yok), SLA'ya göre tekrarlı politika (firma/müşteri bazlı SLA, birden çok politika), talep birleştirme/bölme, ilgili fırsat/ürün bağı, özel alanlar/etiketler, toplu işlemler, CSV içe/dışa aktarma, talep silmede yumuşak bağ temizliği (M3 ile aynı sınırlama: bağlı firma/kişi silinirse ad boş döner, firma silme talep yüzünden engellenmez), rol hiyerarşisi/kayıt sahipliği görünürlüğü (K7 sonraki aşama).
