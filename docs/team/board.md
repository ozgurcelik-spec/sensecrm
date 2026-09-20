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
