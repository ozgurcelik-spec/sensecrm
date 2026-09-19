# Milestone 2 — Satış çekirdeği (plan)

PRD: [zoho-crm-klonu.prd.md](../../.claude/prds/zoho-crm-klonu.prd.md) · Kararlar: [kararlar.md](../architecture/kararlar.md) · Ön koşul: Milestone 1 (Identity, kiracı, RBAC) yeşil.

**Çıktı:** Kullanıcı potansiyel müşteri (lead) girer, onu firma + kişi + fırsata dönüştürür, fırsatları kanban satış hunisinde aşamadan aşamaya taşır. Her şey kiracı bazlı, yetkiye bağlı ve denetim kaydında.

## Modül kararı
Tek modül: `Crm.Modules.Sales` (Domain/Application/Contracts/Infrastructure/Api, şema `sales`). Lead dönüştürme firma, kişi ve fırsatı tek transaction'da oluşturduğu için bunlar aynı modülde. Aktiviteler (M3) ayrı modül olacak ve kayıtlara `Contracts` üzerinden bağlanacak. İskelet `build/new-module.ps1` ile açılır.

## Varlıklar (Zoho karşılıkları)
| Varlık | Temel alanlar | Not |
|---|---|---|
| Account (Firma) | Name*, Industry, Website, Phone, Email, BillingAddress (value object), OwnerUserId*, Description | Ad kiracı içinde benzersiz değil (Zoho gibi), yinelenen uyarısı sonraki aşama |
| Contact (Kişi) | FirstName, LastName*, Email, Phone, Mobile, Title, AccountId?, OwnerUserId*, MailingAddress | KVKK: kişisel veri alanları `IAuditLogged.SensitiveFields` ile işaretli |
| Lead (Potansiyel) | FirstName, LastName*, Company*, Email, Phone, Source (enum: web, referral, campaign, coldCall, other), Status (new, contacted, qualified, unqualified, converted), Rating, OwnerUserId* | Dönüştürülünce salt-okur, `ConvertedAccountId/ContactId/DealId` tutulur |
| Pipeline | Name*, IsDefault, Stages (sıralı) | Her yeni organizasyona varsayılan huni tohumlanır: Nitelendirme 10%, İhtiyaç Analizi 20%, Teklif 50%, Pazarlık 75%, Kazanıldı 100% (won), Kaybedildi 0% (lost) |
| Deal (Fırsat) | Name*, AccountId*, ContactId?, PipelineId*, StageId*, Amount (Money, TRY varsayılan), ClosingDate, Probability (aşamadan), OwnerUserId*, LostReason | Aşama değişimi domain event (`DealStageChanged`) → M4 workflow tetikleyicisi |

`*` zorunlu. Tüm varlıklar `TenantAggregateRoot`, `IAuditLogged`, soft delete. Sahip (owner) kiracının aktif üyesi olmalı → Identity.Contracts üzerinden `IMemberLookup`.

## Komutlar / sorgular
- CRUD + sayfalı liste (arama `q`, filtre: owner, status/stage, tarih; sıralama) her varlık için.
- `ConvertLead(leadId, createDeal: bool, dealName?, amount?, closingDate?)` → mevcut firma eşleşmesi (aynı ada sahip firma varsa seçme imkânı), yoksa yeni; `LeadConverted` integration event.
- `MoveDealStage(dealId, stageId, lostReason?)` → kazanıldı/kaybedildi kapanışları.
- Pipeline yönetimi: aşama ekle/sırala/yeniden adlandır (ayar yetkisi `crm.deals.write` + `org.settings.manage`).
- Firma detayında ilişkili kişiler ve fırsatlar.

## İzinler
`crm.accounts.*`, `crm.contacts.*`, `crm.leads.*`, `crm.deals.*` (M1'de tanımlı). Kayıt sahipliği bazlı görünürlük (yalnızca kendi kayıtlarım) M3+'da Zoho "rol hiyerarşisi" ile gelecek.

## API (`/api/v1`)
`/accounts`, `/contacts`, `/leads`, `/leads/{id}/convert`, `/deals`, `/deals/{id}/stage`, `/pipelines`. Liste yanıtı senseik `PagedResult` biçiminde.

## Web
Liste sayfaları (Mantine tablo, arama, filtre, sütun sıralama), detay sayfası (Zoho tarzı sol bilgi paneli + sekmeler: Genel, İlişkili kayıtlar, Denetim), oluştur/düzenle formu, lead dönüştürme diyaloğu, fırsatlar için kanban huni görünümü (sürükle-bırak aşama değişimi) ve liste görünümü arasında geçiş. TR/EN metinler.

## Testler
- Domain: lead dönüştürme kuralları, aşama/olasılık, dönüştürülmüş lead'in değişmezliği.
- Entegrasyon (Testcontainers): CRUD, dönüştürme tek transaction, kiracılar arası izolasyon (A kiracısı B'nin firmasını göremez/değiştiremez), yetkisiz kullanıcıya 403.
- Web: liste + form + dönüştürme diyaloğu bileşen testleri.

## Kapsam dışı (M2)
Özel alanlar (Zoho "custom fields" / layout editörü) → ayrı milestone; içe aktarma (CSV) → sonra; yinelenen kayıt tespiti → sonra; e-posta entegrasyonu → sonra.
