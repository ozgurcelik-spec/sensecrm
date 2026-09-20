# Geliştirme ekibi panosu (agent Kanban)

Yöntem: ECC `team-agent-orchestration`. Her kartın tek sahibi, dosya kapsamı, durumu, kanıtı ve merge kapısı vardır. Paralel kartlar ayrı git worktree/branch'te çalışır; **tek entegratör** (Lead) sırayla merge eder. Ortak "sıcak dosyalar" yalnız ekleme (append-only) yapılır.

## Roller
| Rol | Sorumluluk |
|---|---|
| **Lead / Entegratör** (ana oturum) | Pano, kontratların onayı, merge sırası, çakışma çözümü, bağımsız doğrulama (build/format/test + tarayıcı E2E), commit |
| **Spec** (planlayıcı ajan) | Kartın plan + HTTP kontratı belgesi (`docs/plan/*.md`); kod yazmaz |
| **Backend** (kart başına) | `src/Modules/<Modül>/**`, ilgili testler, migration; kart worktree'sinde |
| **Web** (kart başına) | `web/**` içindeki kart dosyaları; kontrata karşı sahte API ile test |
| **DevOps** | `deploy/**`, `infra/**`, Dockerfile, CI, runbook |
| **Security** | İnceleme ve sertleştirme düzeltmeleri |

## Sıcak dosyalar (yalnız ekleme, çakışmayı entegratör çözer)
`src/Sense.Crm.Api/ModuleCatalog.cs`, `src/Sense.Crm.Migrator/Program.cs`, `src/Sense.Crm.Worker/Program.cs`, `tests/Sense.Crm.Tests.Shared/Fixtures/TestFixture.cs` (Respawn şemaları), `Identity.Contracts/Permissions.cs`, `SystemRoleDefinitions.cs`, `SharedResource{,.en}.resx`, `docs/architecture/backend.md`, `web/src/App.tsx`, `web/src/config/navigation.ts`, `web/src/i18n.ts`, `web/public/locales/*/{common,navigation}.json`, `web/README.md`, `web/package.json`, `pnpm-lock.yaml`.

## Kurallar
- Kart, kanıt (test çıktısı) olmadan Review'a geçmez; Review'dan Merged'a yalnız Lead'in bağımsız doğrulaması sonrası geçer.
- Canlı duman testleri kart başına ayrılmış port ve veritabanında yapılır (çakışma yok): API 5081/5082/5083, veritabanı `crm_<kart>` (dev postgres `crm-postgres:15433`).
- Merge kapısı (tüm kartlar): `dotnet build Sense.Crm.slnx --no-incremental` 0 uyarı, `dotnet format --verify-no-changes`, `dotnet test Sense.Crm.slnx` yeşil, web `tsc`/`eslint`/`vitest`/`build` yeşil, mimari + kiracı izolasyon testleri yeşil, yeni her kiracı varlığı için çapraz-kiracı testi.

## Kartlar
| ID | Başlık | Sahip | Durum | Dal / worktree | Merge kapısı |
|---|---|---|---|---|---|
| C-M1..M5 | Platform, Satış, Aktivite/Rapor, Workflow, Pilot paketleme | Backend+Web+DevOps | **Merged** | `main` | yeşil |
| C-SEC | Güvenlik sertleştirme (Conductor girdi güveni, hesap/davet/parola, giriş zamanlaması, refresh ömrü, yetki yükseltme, mimari test) | Security | **Merged** | `sec/hardening` | Güvenlik raporu H1–H4, M1–M9 kapanış tablosu + yeni testler |
| C-M6A | Ticaret: Ürünler, Teklifler, Satış Siparişleri (kalemli) | Spec→Backend+Web | **Merged** | `m6/commerce` | kart kapısı + teklif→sipariş dönüşümü tek transaction |
| C-M6B | Servis/Destek: Talepler (case), yorumlar, SLA süresi | Spec→Backend+Web | **Merged** | `m6/service` | kart kapısı |
| C-M6C | Pazarlama: Kampanyalar, kampanya üyeleri, lead kaynağı ilişkisi | Spec→Backend+Web | **Merged** | `m6/marketing` | kart kapısı |
| C-M7 | SaaS hazırlığı (planlar/limitler, kiracı yönetimi, faturalama altyapısı) | Spec→Backend+Web | **Merged** | `main` | M6 merge sonrası şekillenir |
| C-M8A | Bildirimler: uygulama içi + e-posta (SMTP) + SMS portu, şablonlar, tercihler, hatırlatma/SLA zamanlayıcıları (Notification Worker) | Spec→Backend+Web | **Ready** | `m8/notifications` | kart kapısı + egress/PII incelemesi |
| C-M8B | Webhooks ve Open API: giden webhook (imzalı, yeniden deneme, SSRF korumalı), API anahtarları, teslimat günlüğü | Spec→Backend+Web | **Ready** | `m8/integrations` | kart kapısı + güvenlik incelemesi |
| C-M8C | Dosya ekleri: nesne depolama (MinIO/S3), kayıtlara ek, plan depolama limiti, KVKK silme | Spec→Backend+Web | **Ready** | `m8/files` | kart kapısı + yetki/indirme incelemesi |
| C-M8D | Özel alanlar: kiracı bazlı alan tanımları, doğrulama, dinamik form/detay/liste | Spec→Backend+Web | **Ready** | `m8/custom-fields` | kart kapısı + kiracı izolasyonu |
| C-M9A | Kabuk ve iş kuyruğu: gruplu menü, genel arama, hızlı oluştur, İş Kuyruğu, Ana Sayfa widget'ları | Spec→Backend+Web | **Ready** | `m9/shell` | kart kapısı |
| C-M9B | Liste deneyimi: kayıtlı görünümler, gelişmiş filtre, toplu işlem, etiket, içe/dışa aktarma | Spec→Backend+Web | **Backlog** (M8D sonrası) | `m9/lists` | kart kapısı |
| C-M9C | Satış belgeleri ve envanter: fatura, fiyat listesi, tedarikçi, satın alma emri, belge alan paritesi | Spec→Backend+Web | **Ready** | `m9/inventory` | kart kapısı |
| C-M9D | Aktivite paritesi: görev tekrarı/anımsatıcı, toplantı, arama, takvim | Spec→Backend+Web | **Backlog** (M8A sonrası) | `m9/activities` | kart kapısı |
| C-M9E | Alan ve form paritesi: standart alanlar, Kaydet ve Yeni, dönüştürme eşlemesi | Spec→Backend+Web | **Backlog** (M8D sonrası) | `m9/fields` | kart kapısı |
| C-M9F | Rapor ve analitik: rapor oluşturucu, hazır raporlar, pano oluşturucu, hedefler, öngörü | Spec→Backend+Web | **Backlog** (M9B sonrası) | `m9/analytics` | kart kapısı |
| C-M9G | Otomasyon ve kanallar: webformları, atama kuralları, genel kural motoru, şema | Spec→Backend+Web | **Backlog** (M8A/B sonrası) | `m9/automation` | kart kapısı + güvenlik incelemesi |
| C-M9H | Erişim modeli: rol hiyerarşisi, kayıt görünürlüğü, alan izni, giriş geçmişi | Spec→Backend+Web | **Backlog** (M8D sonrası) | `m9/access` | kart kapısı + güvenlik incelemesi |
| C-M9I | Destek ve belgeler: çözümler (bilgi tabanı), belge klasörleri | Spec→Backend+Web | **Backlog** (M8C sonrası) | `m9/support` | kart kapısı |
| C-M9J | Şirket ayarları: çalışma saatleri/tatil, çoklu para birimi, şablonlar | Spec→Backend+Web | **Backlog** (M9C sonrası) | `m9/company` | kart kapısı |
| C-X1 | Analiz: BPMN süreç tasarım stüdyosu (Conductor üzerinde görsel tasarımcı, sürümleme, simülasyon) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x1-bpmn.md` | analiz belgesi + karar önerisi |
| C-X2 | Analiz: AI analiz worker'ı (yerinde model, KVKK, kullanım senaryoları, maliyet) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x2-ai.md` | analiz belgesi + karar önerisi |
| C-X3 | Analiz: ERP, dijital imza ve SFTP entegrasyonları (bağlayıcı çerçevesi, güvenlik, yerel sağlayıcılar) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x3-entegrasyon.md` | analiz belgesi + karar önerisi |
| C-X4 | Analiz: Müşteri/partner portalı (kimlik, kiracı ayrımı, kapsam, güvenlik) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x4-portal.md` | analiz belgesi + karar önerisi |
| C-X5 | Analiz: Kubernetes/Helm dağıtımı (Compose'dan geçiş, HA, gizli yönetimi, veri merkezi) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x5-k8s.md` | analiz belgesi + karar önerisi |
| C-X6 | Analiz: SSO (OIDC/SAML, AD/Entra, grup eşleme, JIT hesap, oturum) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x6-sso.md` | analiz belgesi + karar önerisi |
| C-X7 | Analiz: Sektör paketleri (paket modeli, şablonlar, alan/iş akışı setleri, dağıtım) | Spec (araştırma) | **Backlog** (boş yuvada sırayla) | `docs/analysis/x7-sektor.md` | analiz belgesi + karar önerisi |

## Yürütme sırası
1. C-M5 biter → doğrula, commit.
2. Paralel: C-M6A/B/C Spec ajanları (plan+kontrat) → onay → Backend+Web ajanları (worktree'lerde) → C-SEC (C-M5 sonrası, ayrı worktree).
3. Lead sırayla merge eder: C-SEC → C-M6A → C-M6B → C-M6C; her merge sonrası tüm kapılar + tarayıcı E2E.
4. C-M7 şekillenir.

## Güncelleme günlüğü
- 2026-09-19: Pano kuruldu. M1–M4 merged; C-M5 running; C-SEC güvenlik raporuna göre blocked.
- 2026-09-20: C-M5, C-SEC, C-M6A/B/C merged (sıra: M5 → M6C → M6A → M6B → C-SEC); merge sonrası tüm kapılar yeşil (backend + web) ve tarayıcıda uçtan uca doğrulandı. Kök ad alanı `Crm.*` → `Sense.Crm.*` olarak değiştirildi. Sıradaki: C-M7 (SaaS hazırlığı) şekillendirilecek.
- Çakışma dersleri: (1) `git checkout --merge` + JSON için yapısal birleştirme; (2) `.gitattributes` ile LF zorunlu; (3) izin sayısı testleri sayı sabitlemez; (4) aynı adlı tip/anahtar çakışmaları (MemberStatus, resx `field.*`) merge sonrası taranır.
- 2026-09-20: C-M7 merged (Platform modülü: planlar/limitler/askıya alma/ölçüm/KVKK silme; web: platform konsolu, Plan ve kullanım, bantlar, ilk kurulum kartı). Kapılar: backend 12 proje 1188 test, web 798 test; tarayıcıda uçtan uca doğrulandı (starter planında kapalı modüller menüden gizli, platform konsolu, askıya alma diyaloğu).
- Açık: yerel `main` henüz `origin`e push edilmedi (otomatik mod denetleyicisi push komutunu reddetti; kullanıcı elle çalıştırmalı).
- 2026-09-20: M8 başladı (A–D). Eşzamanlı en çok 5 ajan; Spec ajanları önce (4 paralel), sonra dalga dalga Backend+Web. Merge sırası: D → C → A → B (ortak varlık ve sıcak dosya çakışmalarını azaltmak için; gerekirse değişir).
- 2026-09-20: Zoho ekran analizi çıkarıldı (`docs/analysis/zoho-ekran-analizi.md`); M9A–J kartları panoya eklendi. Kural: toplam ajan sayısı hep 8; biten ajanın dalı `main`e merge edilir, worktree'si silinir, hemen yeni görev açılır (sıra: M9A spec, M9C spec, ardından bağımlılığı çözülenler).
- 2026-09-20: M9A ve M9C plan belgeleri merged (`docs/plan/m9a-kabuk.md`, `m9c-envanter.md`); M9A Backend ve M9C Backend ajanları başladı (Web ajanları backend sonrası). Not: M9A `AddSearchText` migration'ları M8D/M9C snapshot'larıyla çakışır — sonra merge edilen kart migration'ını birleşik dal üzerinde yeniden üretir.
- 2026-09-20: Kullanıcı isteğiyle yedi kapsam-dışı başlık için analiz kartları (C-X1..X7) açıldı; toplam ajan 8 kuralı gereği boşalan yuvalarda sırayla başlar. Çıktı: `docs/analysis/x*.md`.
