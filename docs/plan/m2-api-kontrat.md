# Milestone 2 — HTTP kontratı (backend ve web bu belgeye uyar)

Plan: [m2-satis-cekirdegi.md](m2-satis-cekirdegi.md). Taban yol `/api/v1`; JSON camelCase, enum'lar camelCase string, null alanlar yanıtta yok. Hatalar M1'deki gibi ProblemDetails + `code` (+ `errors`). Tarihler ISO 8601; `closingDate` yalnız tarih (`YYYY-MM-DD`). Tüm uçlar Bearer ister; kiracı token'dan gelir.

## Ortak
- Liste yanıtı: `{ items, page, pageSize, totalCount }`. Sorgu: `page` (1'den), `pageSize` (varsayılan 25, en çok 100), `q` (ad/e-posta/şirket içinde arama), `sort` (`alan` veya `-alan`), varlığa özel filtreler aşağıda.
- `ownerUserId` kiracının aktif üyesi olmalı, verilmezse çağıran kullanıcı. Yanıtlarda `ownerUserId` ve `ownerName` gelir.
- Ortak alanlar (her yanıtta): `id`, `createdAt`, `updatedAt?`.
- Hata kodları: `validation`, `forbidden`, `not_found`, `lead.already_converted`, `pipeline.stage_not_found`, `deal.lost_reason_required`, `account.has_dependents` (silme: bağlı kişi/fırsat var), `pipeline.default_required`, `pipeline.stage_in_use`, `owner.not_member`.
- Silme yumuşaktır (soft delete), 204 döner.
- Adres nesnesi: `{ street?, city?, state?, postalCode?, country? }`.

## İzinler
Okuma uçları `crm.<kaynak>.read`, yazma uçları `crm.<kaynak>.write` ister (kaynak: accounts, contacts, leads, deals). Pipeline okuma `crm.deals.read`, pipeline değiştirme `org.settings.manage`. Lead dönüştürme `crm.leads.write` + `crm.accounts.write` + `crm.contacts.write` (+ fırsat açılıyorsa `crm.deals.write`).

## Firmalar `/accounts`
Alanlar: `name*`, `industry?`, `website?`, `phone?`, `email?`, `billingAddress?`, `description?`, `ownerUserId`.
- `GET /accounts?q&ownerUserId&industry` → liste
- `GET /accounts/{id}` → detay + `contactCount`, `dealCount`
- `POST /accounts` → 201 detay; `PUT /accounts/{id}` → 204; `DELETE /accounts/{id}` → 204
- `GET /accounts/{id}/contacts`, `GET /accounts/{id}/deals` → dizi (sayfasız, en çok 200)

## Kişiler `/contacts`
Alanlar: `firstName?`, `lastName*`, `email?`, `phone?`, `mobile?`, `title?`, `accountId?` (yanıtta `accountName?`), `mailingAddress?`, `ownerUserId`. Yanıtta ayrıca `fullName`.
- `GET /contacts?q&accountId&ownerUserId`, `GET /contacts/{id}`, `POST`, `PUT /{id}` (204), `DELETE /{id}`

## Potansiyel müşteriler `/leads`
Alanlar: `firstName?`, `lastName*`, `company*`, `email?`, `phone?`, `source` (`web|referral|campaign|coldCall|other`, varsayılan `other`), `status` (`new|contacted|qualified|unqualified|converted`, oluştururken `new`), `rating?` (`hot|warm|cold`), `ownerUserId`. Yanıtta `fullName`, dönüşmüşse `convertedAccountId?`, `convertedContactId?`, `convertedDealId?`, `convertedAt?`.
- `GET /leads?q&status&source&ownerUserId`, `GET /leads/{id}`, `POST`, `PUT /{id}` (204; dönüşmüş lead için `lead.already_converted` 409), `DELETE /{id}`
- `POST /leads/{id}/convert` `{ accountId?, createDeal, dealName?, amount?, closingDate?, pipelineId? }` → 200 `{ accountId, contactId, dealId? }`. `accountId` verilirse mevcut firma kullanılır, yoksa lead'in `company` adıyla yeni firma açılır. Kişi lead adından oluşturulur. `createDeal` ise `dealName` zorunlu (yoksa `validation`), fırsat pipeline'ın (varsayılan: varsayılan pipeline) ilk açık aşamasında başlar. Hepsi tek transaction. Tekrar dönüştürme `lead.already_converted` 409.

## Pipeline `/pipelines`
Yanıt: `{ id, name, isDefault, stages: [{ id, name, order, probability, kind }] }`, `kind`: `open|won|lost`.
- `GET /pipelines`, `GET /pipelines/{id}` (`crm.deals.read`)
- `POST /pipelines` `{ name }` → 201 (varsayılan aşamalarla), `PUT /pipelines/{id}` `{ name, isDefault }` → 204
- `PUT /pipelines/{id}/stages` `{ stages: [{ id?, name, probability, kind }] }` → 204; dizi sırası `order` olur; tam bir `won` ve bir `lost` aşama şart (yoksa `validation`); kullanılan aşamayı silme `pipeline.stage_in_use`.
- Her yeni organizasyona (ve M1'de açılmış mevcutlara) varsayılan pipeline tohumlanır: Nitelendirme 10 (open), İhtiyaç Analizi 20 (open), Teklif 50 (open), Pazarlık 75 (open), Kazanıldı 100 (won), Kaybedildi 0 (lost). Adlar tohumlanırken organizasyon dilinde (`tr`: yukarıdaki, `en`: Qualification, Needs Analysis, Proposal, Negotiation, Closed Won, Closed Lost).

## Fırsatlar `/deals`
Alanlar: `name*`, `accountId*`, `contactId?`, `pipelineId?` (varsayılan pipeline), `stageId?` (pipeline'ın ilk aşaması), `amount?` (ondalık sayı), `currency` (varsayılan `TRY`), `closingDate?`, `ownerUserId`, `lostReason?`. Yanıtta `accountName`, `contactName?`, `stageName`, `stageKind`, `probability` (aşamadan), `pipelineName`.
- `GET /deals?q&pipelineId&stageId&stageKind&ownerUserId&accountId` → liste
- `GET /deals/board?pipelineId&ownerUserId` → `{ pipelineId, stages: [{ id, name, kind, probability, totalAmount, count, deals: [özet en çok 100] }] }` (kanban için). Özet: `id, name, accountName, amount, currency, closingDate?, ownerName`
- `GET /deals/{id}`, `POST`, `PUT /{id}` (204; aşama buradan değişmez), `DELETE /{id}`
- `POST /deals/{id}/stage` `{ stageId, lostReason? }` → 204. `lost` aşamada `lostReason` zorunlu (`deal.lost_reason_required`). Aşama değişince `DealStageChanged` domain event; kazanma/kaybetmede `closedAt` yazılır.

## Denetim
Firma, kişi, lead, fırsat, pipeline değişiklikleri `audit_log_entries`'e M1 biçiminde yazılır ve `GET /organization/audit` içinde görünür. Kişisel veri alanları (e-posta, telefon, cep) `SensitiveFields` ile maskelenir. Kayıt bazında denetim: `GET /audit?entityType=Account&entityId={id}` → `/organization/audit` ile aynı biçim (`org.audit.read` yerine ilgili `crm.<kaynak>.read` yeter).
