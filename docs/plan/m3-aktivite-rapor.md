# Milestone 3 — Aktiviteler, dashboard ve raporlar (plan + HTTP kontratı)

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Önceki: [M2 kontratı](m2-api-kontrat.md). Taban yol `/api/v1`, biçim kuralları M2 ile aynı (camelCase, ProblemDetails + `code`, `{ items, page, pageSize, totalCount }`).

**Çıktı:** Kullanıcı görev/arama/toplantı/not kaydeder, bunları firma-kişi-potansiyel-fırsata bağlar, gecikenleri görür; ana sayfada satış hunisi ve kişisel iş listesi, raporlar sayfasında satış raporları vardır.

## Modül kararı
- Yeni modül `Crm.Modules.Activities` (şema `activities`). İlişkili kaydın varlığı `Sales.Contracts` içindeki `IRecordLookup` ile doğrulanır (modüller yalnız Contracts ile konuşur).
- Satış raporları `Sales` modülünde (kendi verisi), aktivite raporu `Activities` modülünde. Ayrı Reporting modülü yok (YAGNI; çapraz JOIN gerektiren rapor çıkarsa açılır).
- İzinler `crm.activities.read/write` ve `crm.reports.read` (M1'de tanımlı) → `ActivitiesPermissions`, raporlar için `crm.reports.read` `Sales.Contracts` veya Identity.Contracts'ta kalabilir.

## Aktiviteler `/activities`
Alanlar: `type*` (`task|call|meeting|note`), `subject*`, `description?`, `status` (`open|completed|cancelled`; `note` her zaman `completed`), `priority` (`low|normal|high`, varsayılan `normal`), `dueAt?` (ISO tarih-saat, görev için), `startAt?`/`endAt?` (arama/toplantı; `endAt >= startAt`), `relatedType?` (`account|contact|lead|deal`) ve `relatedId?` (ikisi birlikte verilir), `assignedUserId` (varsayılan çağıran; kiracının aktif üyesi olmalı → `owner.not_member`). Yanıtta ayrıca `assignedUserName`, `relatedName?`, `completedAt?`, `isOverdue` (open ve `dueAt` geçmiş).
- `GET /activities?q&type&status&assignedUserId&relatedType&relatedId&dueFrom&dueTo&overdue&sort` → paged (`sort`: `dueAt`, `createdAt`, `subject`, `priority`; varsayılan `dueAt` artan, dueAt boş olanlar sonda)
- `GET /activities/{id}`, `POST` → 201, `PUT /{id}` → 204, `DELETE /{id}` → 204 (soft)
- `POST /activities/{id}/complete` → 204, `POST /activities/{id}/reopen` → 204 (note için `activity.note_status_fixed` 409)
- `GET /activities/summary?assignedUserId` (yoksa çağıran) → `{ openCount, overdueCount, dueTodayCount, completedThisWeek }` (kiracı saat dilimine göre "bugün" ve hafta başı pazartesi)
- Hata kodları: `activity.related_not_found` (400/404), `activity.invalid_range`, `activity.note_status_fixed`, `validation`.
- Bağlı kayıt silinirse aktivite kalır, `relatedName` boş döner (ilişki yumuşak; sonra temizlik işi).
- Denetim: aktiviteler `audit_log_entries`'e yazılır; `GET /audit?entityType=Activity&entityId=` çalışır (`crm.activities.read`).

## Satış raporları (`crm.reports.read`), Sales modülü
Tarih parametreleri `from`, `to` (`YYYY-MM-DD`, dahil), varsayılan: son 12 ay. Tutarlar kiracının fırsat para birimlerine bakmadan toplanır (M2 ile aynı sınırlama; yanıtta `currency` yok).
- `GET /reports/sales/funnel?pipelineId` → `{ pipelineId, stages: [{ id, name, kind, order, probability, count, totalAmount }] }` (yalnız açık aşamalar + won + lost, güncel durum)
- `GET /reports/sales/won-lost?from&to&groupBy=month|week` → `[{ period ("2026-09" veya ISO hafta "2026-W38"), wonCount, wonAmount, lostCount, lostAmount }]` (kapanış tarihi `closedAt`'a göre, boş dönemler 0 ile doldurulur)
- `GET /reports/sales/leads-by-source?from&to` → `[{ source, count, convertedCount }]` (`createdAt`'a göre)
- `GET /reports/sales/by-owner?from&to` → `[{ ownerUserId, ownerName, openDealCount, openDealAmount, wonCount, wonAmount, leadCount }]`

## Aktivite raporu (`crm.reports.read`), Activities modülü
- `GET /reports/activities/by-user?from&to` → `[{ userId, userName, completedCount, openCount, overdueCount }]` (`completedAt`/`dueAt` aralığına göre)

## Web
- **Ana sayfa (dashboard):** mevcut kartlara ek: "Benim işlerim" (summary + bugün/geciken ilk 5 görev, tamamla onay kutusu), satış hunisi grafiği (funnel), aylık kazanılan/kaybedilen fırsat çubuk grafiği (son 6 ay), potansiyel kaynak dağılımı. Her widget ilgili okuma iznine göre görünür/gizlenir; boş/yükleniyor/hata durumları.
- **Aktiviteler sayfası:** Görevler / Aramalar / Toplantılar / Notlar sekmeleri veya tür filtresi + hızlı filtreler (Bugün, Geciken, Bana atanan, Tümü), liste + oluştur/düzenle diyaloğu, satırda tamamla/yeniden aç.
- **Kayıt detaylarında "Aktiviteler" sekmesi** (firma, kişi, potansiyel, fırsat): o kayda bağlı aktiviteler + hızlı ekleme (bağlı kayıt önceden dolu).
- **Raporlar sayfası** (`crm.reports.read`): tarih aralığı seçici (bu ay, son 3 ay, son 12 ay, özel), sekmeler: Satış hunisi, Kazanılan/Kaybedilen, Potansiyel kaynakları, Satış temsilcisi, Aktivite. Tablo + grafik, CSV indirme (istemci tarafında).
- Grafik: `@mantine/charts` (recharts). TR/EN metinler.

## Testler
Backend: domain (durum geçişleri, aralık, not kuralı); Testcontainers HTTP: CRUD, filtreler/sıralama, complete/reopen, summary saat dilimi sınırları, bağlı kayıt doğrulama, **kiracı izolasyonu**, izin 403; rapor doğruluğu (bilinen veriyle funnel/won-lost/kaynak/temsilci/aktivite toplamları, boş dönem doldurma, aralık sınırları). Web: filtre-URL senkronu, form + sunucu hata eşlemesi, hızlı ekleme, dashboard widget izin gizleme, rapor aralığı → istek parametreleri.

## Kapsam dışı
Tekrarlayan görevler, hatırlatma bildirimleri/e-posta (bildirim altyapısı M4+), takvim görünümü, dosya eki, @bahsetme, çapraz JOIN'li karma raporlar, rapor kaydetme/zamanlama.
