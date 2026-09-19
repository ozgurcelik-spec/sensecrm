# Milestone 4 — İlk workflow'lar (Conductor) — plan + HTTP kontratı

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Kararlar K9/K10: [kararlar.md](../architecture/kararlar.md) · Önceki: [M3](m3-aktivite-rapor.md). Biçim kuralları M2/M3 ile aynı (`/api/v1`, camelCase, ProblemDetails + `code`, sayfalı liste `{ items, page, pageSize, totalCount }`).

**Çıktı:** Yönetici kural tanımlar; kural tetiklenince Conductor üzerinde bir workflow çalışır. İki hazır workflow: (1) yeni potansiyel müşteriyi kurala göre sırayla (round-robin) bir satış temsilcisine atayıp takip görevi açmak, (2) büyük tutarlı fırsat kazanıldığında onay talebi açmak ve karar sonucunu kayda işlemek. Yürütmeler ve onaylar arayüzde izlenir.

## Mimari kararlar
- Yeni modül `Sense.Crm.Modules.Workflows` (şema `workflows`; tablolar: `workflow_rules`, `workflow_executions`, `approvals`). Diğer modüllere yalnız `Contracts` üzerinden erişir: Sales (`IRecordLookup`, lead sahibi değiştirme için yeni bir `ILeadOwnerService` benzeri Contracts arayüzü), Activities (görev açma için yeni `IActivityCreator` Contracts arayüzü), Identity (`IMemberLookup`, rol üyeleri).
- **Motor arkasında port:** `IWorkflowEngine` (Start, Get, Terminate) + `ConductorWorkflowEngine` (Conductor OSS REST). Testlerde sahte motor; canlı doğrulamada gerçek Conductor.
- **Conductor OSS**, `infra/docker-compose.yml`'a servis olarak eklenir (host portu başka projelerle çakışmayacak biçimde; `18080`/`18081`/`15432`/`16379` başka projelerce kullanılıyor, `18090` civarı seçin). Kalıcılık için crm-postgres'e ayrı veritabanı/şema; **Redis/Elasticsearch gerektirmeyen** en yalın yapılandırma tercih edilir (araştırıp doğru imaj/sürüm/ortam değişkenlerini seçin, belgeleyin). Yapılandırma: `Conductor:BaseUrl`.
- Workflow tanımları kodda (JSON, sürümlü) tutulur, API başlangıcında Conductor'a idempotent kaydedilir. Görev işleyicileri (worker) `Sense.Crm.Worker` içinde Conductor'ı poll eder; her görev `TenantScope` içinde çalışır (kiracı `input.tenantId` ile gelir).
- Tetikleme: mevcut integration event'ler + outbox (`LeadCreated` yeni eklenir; `DealStageChanged` domain event'inden `DealStageChangedIntegration` yayınlanır). `Workflows` bunları dinler, kiracının **etkin kurallarına** bakar, koşul sağlanırsa yürütmeyi başlatır ve `workflow_executions` satırı yazar. Aynı olay için aynı kural iki kez başlamaz (idempotency anahtarı: `ruleId + eventId`).
- Yürütme durumu Conductor'dan poll/callback ile `workflow_executions`'a yansır (running/completed/failed/terminated); hata mesajı saklanır.
- Yeni izinler (Identity.Contracts veya Workflows.Contracts): `org.workflows.manage` (kural + yürütme yönetimi), `crm.approvals.decide` (onay verebilme). Administrator hepsini alır; Standard yeni izinlerden almaz. Mevcut kiracılar başlangıçta senkronize edilir.

## Kurallar `/workflows/rules` (`org.workflows.manage`)
Kural: `{ id, name, kind, isEnabled, params, createdAt, updatedAt? }`. `kind` ve `params`:
- `leadAssignment` — tetik: lead oluşturuldu. `params`: `{ sources?: string[] (boşsa hepsi), assigneeRoleId: uuid, followUpHours: int (1–720, varsayılan 24) }`. Akış: rol üyeleri arasından round-robin (en az açık lead'e sahip aktif üye; eşitlikte en eski atanan) → lead `ownerUserId` güncellenir → atanan kişiye "Yeni potansiyel: <ad>" görevi (`dueAt = şimdi + followUpHours`, ilişkili kayıt lead) açılır. Rolde aktif üye yoksa yürütme `failed` (`error: "no_assignee"`), lead sahibi değişmez.
- `dealApproval` — tetik: fırsat, aşama türü `won` olan aşamaya taşındı. `params`: `{ minAmount: decimal (>0), approverRoleId: uuid }`. `amount >= minAmount` ise: onaylayıcı roldeki her aktif üyeye bağlı bir `approvals` kaydı ve "Fırsat onayı: <ad>" görevi açılır; workflow `approvals` kararını bekler (Conductor HUMAN/WAIT görevi). İlk **reddetme** veya rolün **ilk onayı** sonucu belirler (tek onay yeter — sabit, MVP); sonuç fırsata "Onay: onaylandı/reddedildi (yorum)" notu (Activities `note`) olarak eklenir; diğer bekleyen onaylar `cancelled` olur. Fırsatın aşaması değiştirilmez (bilgilendirici onay).
- `GET /workflows/rules`, `GET /workflows/rules/{id}`, `POST /workflows/rules` → 201, `PUT /workflows/rules/{id}` → 204, `DELETE /workflows/rules/{id}` → 204, `POST /workflows/rules/{id}/enable`, `/disable` → 204.
- Doğrulama: `kind` bilinen olmalı; `assigneeRoleId`/`approverRoleId` kiracıda var olmalı (`workflow.role_not_found`); `params` şemaya uymalı (`validation`, alan hataları `params.<alan>`). Aynı `kind` için birden fazla etkin kural olabilir (hepsi çalışır).
- Kural değişiklikleri denetim kaydına yazılır.

## Yürütmeler `/workflows/executions` (`org.workflows.manage`)
- `GET /workflows/executions?status&ruleId&from&to&page&pageSize` → `{ id, ruleId, ruleName, kind, status (running|completed|failed|terminated), startedAt, endedAt?, subjectType (lead|deal), subjectId, subjectName?, error? }`, en yeni önce.
- `GET /workflows/executions/{id}` → yukarıdakiler + `steps: [{ name, status, startedAt?, endedAt?, output? }]` (Conductor'dan) + `approvals?: [{ id, approverName, status, decidedAt? }]`.
- `POST /workflows/executions/{id}/terminate` → 204 (yalnız running; aksi `workflow.not_running` 409).
- `POST /workflows/executions/{id}/retry` → 204 (yalnız failed; yeni yürütme açar).

## Onaylar `/approvals`
Onay: `{ id, executionId, title, subjectType, subjectId, subjectName?, amount?, currency?, requestedAt, status (pending|approved|rejected|cancelled), approverUserId, approverName, decidedAt?, comment? }`.
- `GET /approvals?status&mine=true|false&page&pageSize` — `mine=true` çağıranın onayları (ek izin gerekmez); `mine=false` yalnız `org.workflows.manage`.
- `GET /approvals/{id}` — kendi onayı veya `org.workflows.manage`.
- `POST /approvals/{id}/decision` `{ decision: "approve"|"reject", comment? }` → 204. `crm.approvals.decide` + onay çağırana ait olmalı (`forbidden`); zaten karar verilmiş/iptal ise `approval.already_decided` 409; reddetmede `comment` zorunlu (`validation`). Kararla workflow ilerler.
- `GET /approvals/summary` → `{ pendingCount }` (çağıranın bekleyenleri; üst çubuk rozeti için).

## Web
- **Ayarlar → İş akışları** (`org.workflows.manage`): kural listesi (ad, tür, etkin anahtarı), oluştur/düzenle diyaloğu (tür seçilince parametre alanları: kaynak çoklu seçim, rol seçici, saat/tutar), silme; sekme **Yürütmeler**: filtreli liste, satırdan detay (adım zaman çizelgesi, onaylar, hata), sonlandır/yeniden dene.
- **Onaylarım** sayfası + üst çubukta bekleyen sayısı rozeti: bekleyenler ve geçmiş, karar diyaloğu (onayla/reddet + yorum), ilgili kayda bağlantı. Menüde `crm.approvals.decide` veya bekleyen onayı olan kullanıcıya görünür.
- Fırsat ve potansiyel detayında "Genel" sekmesinde ilgili yürütme durumu şeridi (varsa): "İş akışı: <kural> — çalışıyor/tamamlandı/hata".
- TR/EN metinler.

## Testler
Backend: kural doğrulama, round-robin seçimi (birim), idempotent tetikleme, sahte motorla uçtan uca (lead oluştur → atama + görev; büyük fırsat kazan → onay → karar → not), kiracı izolasyonu (A'nın kuralı/yürütmesi/onayı B'ye görünmez, B'nin olayı A'nın kuralını tetiklemez), izin 403, hata yolu (`no_assignee`), retry/terminate. Canlı: gerçek Conductor konteynerine karşı iki workflow'un uçtan uca duman testi (sahte olmayan). Web: kural formu tür-uyarlamalı alanlar + sunucu hata eşlemesi, onay kararı (reddette yorum zorunlu), rozet, yürütme detayı.

## Kapsam dışı
Görsel süreç tasarımcısı (BPMN stüdyosu), kullanıcı tanımlı workflow adımları, çoklu aşamalı/sıralı onay zincirleri, onayın aşama geçişini engellemesi, zamanlanmış tetikleyiciler, e-posta/SMS bildirimleri, workflow sürüm geçmişi arayüzü, Conductor kimlik doğrulaması/çok kiracılı Conductor (tek Conductor, kiracı `input.tenantId` ile ayrılır).
