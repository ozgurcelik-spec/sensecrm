# Grup CRM Platformu (Zoho CRM muadili)

## Problem
Grup şirketi, satış ve operasyon süreçlerini dışarıdan kiralanan bir CRM yerine kendi sahip olduğu, kendi veri merkezinde çalışan bir yazılımda yönetmek istiyor. Hazır SaaS CRM'ler verinin yurt dışında tutulması, süreçlerin gruba göre özelleştirilememesi ve dışa bağımlılık nedeniyle bu ihtiyacı karşılamıyor. Aynı ürün daha sonra dışarıya SaaS olarak satılarak gelir kalemine dönüşecek.

## Evidence
- Grup yönetiminin in-house, kendi veri merkezinde çalışan CRM talebi (iç karar).
- Verilerin yurt içinde, grubun kendi sunucularında tutulması zorunluluğu.
- Dış pazarda SaaS olarak satılabilirlik — Assumption — needs validation via satış/ön görüşmeler ve pilot müşteri.
- Mevcut Zoho verisi yok; göç ihtiyacı bulunmuyor.

## Users
- **Primary**: Grup şirketlerinin satış ekipleri (lead, fırsat, müşteri takibi), satış yöneticileri (huni, rapor), operasyon ekipleri (görev ve süreç takibi).
- **Secondary**: Süreç tasarımcıları / BT (workflow ve yetki yönetimi), denetim (audit kaydı), grup yönetimi (şirketler arası konsolide görünüm).
- **Later**: Dış SaaS müşterileri (kendi kiracılarıyla).
- **Not for (MVP)**: Son müşteriler / iş ortakları (portal), mobil saha kullanıcıları.

## Hypothesis
We believe **grup şirketlerine sunulan, workflow ile özelleştirilebilen, çok kiracılı ve yurt içinde barındırılan bir CRM** will **satış süreçlerini tek, grubun sahip olduğu bir platformda toplayarak dış bağımlılığı ortadan kaldıracak** for **grup şirketlerinin satış ve operasyon ekipleri**.
We'll know we're right when **pilot grup şirketi satış sürecinin tamamını 3 ay içinde bu platforma taşır ve kullanıcıların haftalık aktif kullanım oranı %80'in üzerinde kalır**.

## Success Metrics
| Metric | Target | How measured |
|---|---|---|
| Pilot şirketin satış sürecini tamamen taşıması | 3 ay içinde | Lead/fırsat kayıtlarının platformda açılma oranı |
| Haftalık aktif kullanıcı oranı | ≥ %80 | Oturum/aktivite logları |
| Grup şirketlerine yaygınlaştırma | TBD — needs validation via grup yönetimi yol haritası | Aktif kiracı sayısı |
| Workflow ile otomatikleşen süreç sayısı | ≥ 1 (MVP), TBD sonrası | Conductor çalıştırma kayıtları |
| Kiracılar arası veri sızıntısı | 0 | Güvenlik testleri, audit |

## Scope
**MVP**
- Çok kiracılı yapı: tüm grup şirketleri tek veritabanında, satır bazında kiracı ayrımı.
- Kimlik, kullanıcı ve rol/yetki yönetimi (RBAC); şirket (kiracı) yönetimi.
- Çok dilli arayüz (en az Türkçe + İngilizce, yeni dil eklenebilir).
- Müşteri / Kişi, Potansiyel (Lead), Fırsat ve satış hunisi.
- Aktivite ve görevler.
- Temel dashboard ve raporlar.
- En az bir uçtan uca workflow (ör. lead atama + onay), workflow motoru üzerinden.
- Denetim kaydı (kim, neyi, ne zaman değiştirdi).
- Yurt içi, kendi veri merkezinde kurulum.

**Out of scope (MVP)**
- Teklif/Sözleşme, Sipariş/Satış, Servis/Destek — 2. aşama.
- Pazarlama modülü — 2. aşama.
- Mobil uygulama — yapılmayacak (ürün yalnızca web; mobil tarayıcıda çalışır).
- Müşteri/partner portalı — dış kullanıcı ihtiyacı pilot sonrası netleşecek.
- BPMN süreç tasarım stüdyosu, süreç kataloğu, simülasyon — önce sabit workflow'larla değer kanıtlanacak.
- Sektör paketleri (Finans, Sigorta, Trading, Kamu, Sağlık) — dış SaaS satışına bağlı.
- AI destekli analiz — veri birikmeden anlamlı değil.
- Dış sistem entegrasyonları (ERP, e-imza, RPA, SFTP) — 2. aşama; e-posta bildirimi hariç.
- Zoho'dan veri göçü — mevcut veri yok.

## Delivery Milestones
<!-- Status: pending | in-progress | complete -->

| # | Milestone | Outcome | Status | Plan |
|---|---|---|---|---|
| 1 | Platform temeli | Kiracı oluşturulabilir, kullanıcılar giriş yapar, rol/yetki çalışır, arayüz TR/EN | pending | — |
| 2 | Satış çekirdeği | Müşteri, kişi, lead ve fırsat yönetilir; satış hunisi görünür | pending | — |
| 3 | Aktivite & görünürlük | Görevler/aktiviteler izlenir; dashboard ve temel raporlar | pending | — |
| 4 | İlk workflow | Lead atama + onay süreci otomatik yürür, izlenebilir | pending | — |
| 5 | Pilot yayın | Pilot grup şirketi kendi veri merkezinde canlı kullanır | pending | — |
| 6 | 2. aşama modülleri | Teklif, Sipariş, Servis, Pazarlama | pending | — |
| 7 | SaaS hazırlığı | Dış müşteriye kiracı açma, faturalama, süreç stüdyosu | pending | — |

## Open Questions
Kullanıcı kararları Claude'a bıraktı (19.09.2026); kararlar [kararlar.md](../../docs/architecture/kararlar.md) içinde.
- [x] "250" → kapasite hedefi 1.000 kiracı / 10.000 kullanıcı ile her iki yorum karşılanıyor (K3).
- [ ] Pilot olacak grup şirketi hangisi, kaç kullanıcısı var? — milestone 5 öncesi netleşmeli.
- [x] Grup konsolide raporu → MVP dışı, sonra "organizasyon grubu" (K4).
- [x] Kimlik doğrulama → kendi hesap sistemi; AD/LDAP/OIDC SSO sonraki aşama (K6).
- [x] Diller → TR + EN, yenisi eklenebilir yapı (K8).
- [x] Altyapı → geliştirmede Docker Compose, üretimde Kubernetes hedefi (K13). RPO/RTO pilot öncesi belirlenecek.
- [ ] KVKK dışı regülasyon (ör. finans şirketleri için BDDK) — sektör paketleri öncesi doğrulanmalı.
- [x] Dış SaaS → yurt içi veri merkezinde barındırılan çok kiracılı hizmet (Zoho modeli); on-prem kurulum şimdilik yok.
- [x] Mobil uygulama → yok, yalnızca web.

## Risks
| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Kapsamın Zoho'nun tamamına kayması | High | High | MVP sınırına sadık kalmak, her aşamayı pilot geri bildirimiyle açmak |
| Satır bazlı kiracı ayrımında veri sızıntısı | Medium | High | Kiracı filtresinin merkezi ve zorunlu uygulanması, otomatik izolasyon testleri |
| Workflow motorunun ekibe öğrenme ve işletme yükü | Medium | Medium | MVP'de tek workflow ile başlamak, operasyon runbook'u |
| Kendi veri merkezinde işletme (izleme, yedek, DR) yükü | Medium | High | Altyapı gereksinimlerini 1. milestone'da netleştirmek |
| SaaS pazar talebinin doğrulanmaması | Medium | Medium | Önce grup içi değeri kanıtlamak; SaaS'ı milestone 7'ye bırakmak |

---
*Status: DRAFT — requirements only. Implementation planning pending via /plan.*
