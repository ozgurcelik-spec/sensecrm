# C-X7 — Sektör paketleri: analiz ve karar önerisi

Tarih: 2026-09-20 · Yazar: Spec (araştırma) · Kart: `C-X7` (`docs/team/board.md`) · Kod yazılmadı; yalnız bu belge.
Girdiler: `docs/architecture/mimari.webp` (sol sütun "Sektör Paketleri: Finans, Sigorta, Trading, Kamu, Sağlık, Diğer"; blok 6 "Sektör Şablonları"), `kararlar.md` (K2, K4, K5, K13, K17, K18), `hardening-report.md` (M6, M7), `docs/plan/m2, m4, m6a, m6b, m7, m8d, m9b, m9c, m9e, m9f, m9g, m9h, m9i, m9j`, `docs/analysis/x1-bpmn.md` (Faz 4 "sektör şablonları"), `x2-ai.md`, `x3-entegrasyon.md`, gerçek kod (`src/Modules/*`; **Customization modülü henüz yok**, M8D yalnız plan), kardeş repo `senseik` (salt okunur; sektör/dikey yapılandırma deseni **yok**, yalnız İK'ya özgü `Union` gibi alan nesneleri var).

**Doğrulama işaretleri:** `[D]` bu depodaki belge/koddan doğrulandı, `[K]` dış kaynak sayfası bu oturumda okundu, `[Ö]` ikincil kaynak/arama özeti, `[?]` doğrulanmadı (bilgi/çıkarım). Efor: 1 ew = 1 mühendis-haftası (test + inceleme dahil, dış bekleme hariç), ±%40. Puanlar yargıdır, ölçüm değildir. **Hukuki içerik hukuk görüşü değildir**; her sektör için hukuk onayı §4/§7'de ön koşuldur.

## 0. Özet ve öneri

1. **Öneri: "A hemen, B kademeli" (seçenek E).** Şimdi her sektör için kısa, iyi belgelenmiş yapılandırma rehberi (A) ve **paralel** olarak kapalı katalogda çalışan, imzalı, sürümlü, bildirimsel **çözüm paketleri** (B: manifest → mevcut modül portları). Sektöre özgü kodlu modül (C) ve pazar yeri (D) şimdi yapılmaz.
2. **Neden B?** Grup 250 şirket: aynı yapılandırmayı 250 kez elle kurmak ve sonra sapmayı izlemek asıl maliyet. Paket = "dağıtılabilir, farkı görülebilir, geri alınabilir yapılandırma". SaaS'ta aynı mekanizma satılabilir ürün olur. Puan farkı küçüktür (A 74 · B 76 · E 79, §1.2); belirleyici argüman 250× çarpan ve B'nin A'nın üst kümesi olmasıdır.
3. **Sert sınır = yeni varlık türü.** Mevcut yapı taşları (M8D özel alan: 8+3 sabit tür, arama/süzme sınırlı; lookup tipi yok; satır düzeyi özel alan yok) poliçe, finansal hesap, teminat mektubu, sözleşme gibi birinci sınıf nesneleri **taşımaz**. Paket v1 bunları "mevcut varlık + özel alan" olarak **taklit eder** (poliçe = kazanılmış fırsat, hasar = talep, ihale = fırsat). Gerçek çözüm "Özel Nesneler" (diyagramdaki "Özel Modüller") çekirdek yeteneğidir; **paketten bağımsız, ayrı analiz kartı** (öneri: C-X8) ve workshop kanıtına bağlı karar.
4. **İlk paketler:** (0) **Grup Temeli** (sektörden bağımsız, motoru kanıtlar, 250 kiracıda tutarlılık) → (1) **Ticaret (Trading = mal ticareti)**: mevcut modüllere en iyi oturan, en az duyarlı veri → (2) **Sigorta (acente/aracı)**: değeri yüksek (yenileme), poliçe=fırsat ile başlanabilir; sağlık/hayat branşı ve tıbbi alan **dışarıda**. Kamu (ihale takibi) ucuz üçüncü; **Finans ve Sağlık ertelenir** (müşteri sırrı / özel nitelikli veri ön koşulları, §4). Sıra, keşif çalıştayında (§6) grubun gerçek karışımıyla doğrulanır.
5. **Ön koşul (paketten önce):** duyarlı veri sınıfı (`dataClass`), M9H alan izni, arama/dışa aktarma/AI'dan dışlama, özel nitelikli alanlar için okuma denetimi (§4). M8D henüz merge olmadığı için `isSensitive` bool yerine `dataClass` enum'ı **şimdi** eklemek ucuzdur.
6. **Maliyet:** spike + çalıştay ~2 ew; MVP dilimi (motor: alan/yerleşim/huni/SLA/etiket/rol + Grup Temeli + Ticaret içeriği, otomasyon/rapor olmadan) ~24 ew; tam ilk yayın (otomasyon/rapor/şablon/örnek veri + Sigorta) ~35 ew; filo dağıtımı/yükseltme +5 ew (±%40). Özel Nesneler ayrıca ~20–30 ew (kaba; C-X8 analizi kesinleştirir).

## 1. Paket ne demek? Seçenekler

**Tanım.** Sektör paketi = bir sektörün yaygın veri alanlarını, seçim listelerini, satış hunisini, SLA politikasını, kayıtlı görünümlerini, rapor/pano tanımlarını, belge/e-posta şablonlarını, otomasyon/atama kurallarını, rol önerilerini ve (isteğe bağlı) sentetik örnek veriyi **veri olarak** tarif eden sürümlü paket. **Değildir:** yeni kod, yeni tablo/varlık türü, veri dönüşümü, entegrasyon/webhook/anahtar kurulumu, kullanıcı/üyelik/plan değişikliği.

Mimari uyum `[D]`: paket `kararlar.md` K5'e uyar (modül sınırı; paket motoru diğer modüllere yalnız `*.Contracts` portlarıyla), M8D'nin "her modül kendi türünü kaydeder" deseninin (`CustomEntityRegistration`) genişlemesidir; "Sektör Şablonları" (blok 6) X1 Faz 4'te süreç şablonu (TPD) olarak aynı katalogla dağıtılır.

### 1.1 Seçenekler

- **A — Paket yok:** birkaç iyi belgelenmiş yapılandırma rehberi (sayfa + ekran görüntüsü + CSV alan listesi); uygulama elle.
- **B — Bildirimsel, sürümlü, içe aktarılabilir çözüm paketleri:** JSON manifest; alan, seçenek listesi, huni, kayıtlı görünüm, rapor/pano, şablon, otomasyon/atama kuralı, rol, örnek veri; **mevcut modül portlarıyla**, idempotent uygulama, dry-run, fark, kaldırma/yükseltme.
- **C — Sektöre özgü kodlu modüller:** `Insurance`, `Tenders`, `Portfolio` vb. (yeni tablo, yeni ekran).
- **D — Pazar yeri/iş ortağı modeli:** üçüncü taraf paket yayını, dağıtım, gelir paylaşımı.
- **E — A hemen + B kademeli (öneri).**

### 1.2 Puanlama (1 = kötü, 5 = iyi; ağırlıklar toplam 100; toplam = Σ ağırlık×puan/5)

| Ölçüt (ağırlık) | A | B | C | D | E |
|---|---|---|---|---|---|
| Değer (250 kiracıda tutarlılık, sektör uyumu) (25) | 2 | 4 | 5 | 3 | 4 |
| Efor / ilk değere süre (15) | 5 | 3 | 1 | 1 | 4 |
| Güvenlik / saldırı yüzeyi (20) | 5 | 4 | 4 | 2 | 4 |
| Bakım ve sürüm yükü (15) | 4 | 3 | 1 | 2 | 3 |
| Yasal/KVKK riski (10) | 5 | 4 | 3 | 2 | 4 |
| SaaS'a ölçeklenme (10) | 1 | 5 | 2 | 4 | 5 |
| Mevcut mimariyle uyum (5) | 5 | 4 | 2 | 2 | 4 |
| **Toplam (100)** | **74** | **76** | **59** | **46** | **79** |

Duyarlılık: Efor ağırlığı 15→30 (diğerleri orantılı düşerse) A'yı öne geçirir; Değer 25→35 B/E farkını açar. Yani **A–B farkı kararsızdır; karar 250× çarpan ve tek-kiracı elle kurulumun sapma maliyetidir** (varsayım: doğrulanmadı, §6). C, her sektörde yeni modül + migration + kart demektir ve K16'nın "gereksizi baştan alma" ilkesine ters düşer; D, B'nin üzerine oturur (önce B olmadan D kurulamaz) ve imza/tedarik zinciri riskini dışarı açar → **ertelenir** (SaaS ihtiyacı doğarsa).

### 1.3 Sektör yazılımları paketlemeyi nasıl yapıyor (kısa)

| Ürün | Model | Kaynak |
|---|---|---|
| Salesforce Financial Services Cloud / Industries | Sektör **veri modeli** (Insurance Policy, Claim, Producer, "Renewed From Policy", Financial Account…) birinci sınıf nesnelerle; Industries yığını tarihsel olarak **yönetilen paketler** + OmniStudio **DataPack**'lerle (dışa/içe aktarılabilir bileşen paketi) dağıtılır, bir kısmı çekirdek platforma taşınmakta | `[Ö]` help.salesforce.com/developer.salesforce.com veri modeli galerisi, arama özeti 2026-09-20 |
| Zoho | Zoho CRM "verticals" sayfası ve **Zoho Vertical Studio**: iş ortağının beyaz etiketli dikey uygulama yapıp satması ("build once, sell many") | `[K]` zoho.com/verticalstudio (okundu 2026-09-20); verticals sayfası `[Ö]` |
| Dynamics 365 | **Industry Accelerators** (bankacılık/sigorta/sağlık veri modeli, Common Data Model üzerinde, ISV/SI'lar için açık kaynak GitHub deposu) | `[Ö]` github.com/microsoft/Industry-Accelerator-FinancialServices, arama özeti; Learn sayfası (okundu, güncelleme 2026-06-04) yalnız genel metin verir, **hızlandırıcıların güncel destek durumu doğrulanamadı `[?]`** |
| Yerli CRM sağlayıcıları | Dikey paketleme pratiği **doğrulanamadı** (aramada yerel sonuç çıkmadı) `[?]` | — |

Çıkarım: büyük oyuncular sektör değerini **veri modeli (yeni nesne türü) + şablon** olarak sunuyor; yalnız yapılandırma paketi (alan+huni) hafif bir alt kümedir. Bizim B seçeneğimiz bu yüzden "Özel Nesneler" gelene kadar bilinçli olarak **sınırlı** kalır.

## 2. Sektör analizi: eksik nesneler ve süreçler, paketin gerçekçi teslimi

Yapı taşları `[D]` (plan durumu): satış çekirdeği M2 (kod var: firma/kişi/potansiyel/fırsat/huni), ticaret M6A/M9C (teklif/sipariş/fatura/PO/tedarikçi/fiyat listesi), servis M6B (talep + SLA), M4 sabit iki onay akışı, M8D özel alan (8 tür + M9C'de fatura/PO/tedarikçi; **lookup, formül, satır/ürün düzeyi alan yok**; süzülebilir ≤10, liste sütunu ≤5, tür başına ≤200 tanım), M9B görünüm/etiket/içe aktarma, M9F rapor/pano (kapalı katalog JSON), M9G kural motoru (**tarih tabanlı tetik var**: `dateField ± offsetDays`; kapalı 6 eylem; kuralın gölge/dry-run durumu var; genel komut gönderimi mimari testle yasak), M9H rol/alan izni, M9I çözümler/belge klasörleri, M9J şablon/PDF/çalışma takvimi/çoklu para birimi, M7 plan bayrakları. **Bunların çoğu henüz merge edilmemiş plandır**; paket motoru kapsayacağı "kind" kümesini merge sırasıyla büyütür (§5).

### 2.1 Sektör tablosu

`Örtü` = mevcut modül üzerinde paket teslimi (✔ tam, ◐ taklit/kısıtlı, ✘ yeni çekirdek ister).

| Sektör | Genel CRM'de eksik nesne/süreç | Paketin teslimi (üstünde) | Yeni çekirdek gerektiren (sert sınır) |
|---|---|---|---|
| **Finans** (banka/finansman/leasing/aracı kurum; hangisi? bilinmiyor `[?]`) | Ürün portföyü (hesap/kredi/fon pozisyonu, bakiye), müşteri segmenti/risk sınıfı, KYC/MASAK kontrol listesi ve periyodik yenileme, limit/teminat, kredi başvuru hattı, uygunluk/risk profili | ◐ başvuru huni (Başvuru→Değerlendirme→Onay→Tahsis), segment/risk/KYC durumu+tarihi alanı, eşikli onay (M4), KYC yenileme hatırlatması (M9G tarih kuralı), rol önerisi, raporlar; belge: Files klasör ACL | ✘ **Finansal hesap/pozisyon 1:N**, bakiye/limit nesnesi, çekirdek bankacılık entegrasyonu (X3 bağlayıcı), lookup |
| **Sigorta** (acente/broker; sigortacı ise farklı) | Poliçe (no, branş, şirket, başlangıç/bitiş, prim, komisyon, taksit), sigortalı ≠ sigorta ettiren, teminat, **yenileme hattı** (T-60/30/7), çok şirketli teklif karşılaştırma, hasar dosyası (no, tarih, ekspertiz, ödeme), araç/konut varlığı (plaka, adres), komisyon mutabakatı | ◐ **poliçe = kazanılmış fırsat** (poliçe alanları özel alan; tutar = brüt prim), **yenileme = tarih kuralıyla üretilen görev/fırsat** (M9G `dateField`, `origin=automation`), hasar = **talep** (`case` özel alan: dosya no, hasar tarihi; SLA politikası), çok şirketli karşılaştırma = **teklif kalemleri** (kalem = şirket teklifi), sigorta şirketi = tedarikçi/firma, huni "Yenileme", görünümler ("30 gün içinde biten"), raporlar, rol | ✘ **Poliçe birinci sınıf 1:N** (müşteri başına çok poliçe, kendi yaşam döngüsü, yenileme zinciri: Salesforce "Renewed From Policy" `[Ö]`), sigortalı ilişkisi (lookup tipi yok), varlık (araç/konut) nesnesi, komisyon defteri |
| **Trading** (mal ticareti: emtia/sanayi ürünü; **tanım doğrulanmalı**, §7-4) | RFQ→teklif→sözleşme→sipariş→sevkiyat/ödeme zinciri; **emtia kalemi** (kalite/spesifikasyon, miktar+birim, tolerans ±%, Incoterms, liman, ödeme (akreditif/TT), fiyat formülü indeks+prim), karşı taraf kredi limiti, marj/maliyet gizliliği | ✔ RFQ = potansiyel/fırsat, tedarikçi teklifi = PO/teklif, teklif→sipariş→fatura (M6A/M9C), çoklu para birimi (M9J), marj/limit onayı (M4 eşik), başlık düzeyi özel alanlar (Incoterms, liman, ödeme şekli), fiyat listesi; ◐ maliyet/marj gizliliği (M9H alan izni) | ✘ **Satır/ürün düzeyi özel alan** (M8D bilerek dışarıda; küçük uzantı: `product` + `quoteLine/orderLine` türü), **Sözleşme** varlığı (yok, teslim takvimi), sevkiyat/lot, fiyat formülü (hesaplanan alan yok) |
| **Kamu** | (a) **Kamuya satış:** ihale takibi (İKN, idare, usul, yaklaşık maliyet, ihale/son teklif tarihi), şartname/evrak kontrol listesi, teminat (teklif/kesin) ve mektup vadesi, itiraz/şikayet süreleri, sözleşme/hakediş. (b) **Kamu kurumu kiracı:** dilekçe/başvuru, protokol, yazışma (sayı/tarih), yasal süreli talep | ◐ (a) ihale = fırsat + "Kamu ihale" hunisi (Keşif→Şartname→Teklif hazırlık→İhale→Sözleşme→Uygulama), süre alanları + tarih kuralı hatırlatması, belge klasörü (M9I), onay; (b) dilekçe = talep + SLA (yasal süre iş takvimiyle, M9J) `[?]` yasal süre değerleri doğrulanmadı | ✘ **Teminat/mektup defteri** (vade, banka, tutar), sözleşme, protokol nesnesi; EKAP ihale beslemesi (üçüncü taraf erişilebilirliği doğrulanmadı `[?]`), e-Yazışma uyumu |
| **Sağlık** | Hasta, randevu, muayene/protokol, provizyon, hasta yolculuğu, doktor/uzman; kurumsal tarafta: tıbbi cihaz satışı/servis sözleşmesi, hastane ihalesi, doktor ziyareti | ✔ **B2B varyantı** (hastane=firma, cihaz servis talebi=talep+SLA, ihale hunisi) düşük duyarlılık; ◐ hasta odaklı: randevu/lead + memnuniyet şikayeti (talep), **minimum veri** | ✘ Hasta/randevu nesnesi ve klinik veri **hedeflenmez** (CRM EMR/HBYS değildir); özel nitelikli veri kuralları §4 |
| **Diğer** | Genel B2B satış, hizmet/danışmanlık (proje bazlı), boş şablon | ✔ "Grup Temeli" ve "Kurumsal B2B" paketleri | — |

### 2.2 Sert sınır: hangi ihtiyaç hangi katmanda?

| Sınıf | Anlamı | Örnekler | Paket kapsamı |
|---|---|---|---|
| **S1** mevcut varlık + mevcut alan tipi | Bugünkü/planlı kapasiteyle tam | huni, SLA, etiket, görünüm, rapor, şablon, kural, rol, tarih alanı, seçim listesi | v1 içinde |
| **S2** mevcut varlık + **küçük uzantı** | Var olan mekanizmaya ekleme, yeni tablo yok | `product` ve `quoteLine/orderLine` için özel alan türü; **lookup alan tipi**; süzülebilir alan tavanı; `dataClass`; kayıtlı hesaplanmış ölçü | Paket bekler; ayrı küçük kart (C-S5b), ~2–3 ew |
| **S3** **yeni varlık türü** | Yeni tablo, liste/detay/kapsam/denetim/rapor/içe aktarma yüzeyi | Poliçe, finansal hesap, teminat mektubu, sözleşme, protokol, sevkiyat, hasta | **Paket v1 dışı**; taklit + "Özel Nesneler" kararı |

Özel Nesneler'in maliyeti tek başına M8D+M9B+M9F+M9G+M9H'nin genelleştirilmesi demektir (her varlık türünün beş kartta bir katalog kaydı olması); bu belge onu **paket kapsamına katmaz**, ancak paket manifest'inin `kind` alanı ileride `customObject` tanımını da taşıyabilecek şekilde tasarlanır (§3.3).

### 2.3 Değer / risk / efor sıralaması

Değer, uygunluk (mevcut modüle oturma), duyarlılık riski ve efor **ön yargıdır**; grubun gerçek şirket karışımı bilinmiyor `[?]` ve çalıştayda (§6) değişebilir. Uygunluk ve değer yüksek iyi; risk yüksek kötü.

| Paket | Değer (1–5) | Uygunluk (1–5) | Duyarlılık riski (1–5) | Efor (ew, içerik+test) | Not | Sıra |
|---|---|---|---|---|---|---|
| Grup Temeli (ortak seçim listeleri, segment, etiket, roller, görünüm) | 4 (250 kiracı tutarlılığı) | 5 | 1 | 1,5 | Motoru kanıtlar | 0 |
| **Ticaret (mal ticareti)** | 4 | 4 (S2 uzantısıyla 5) | 2 (ticari sır: marj) | 3 (+2–3 uzantı) | En az yasal yük | **1** |
| **Sigorta (acente)** | 5 (yenileme geliri) | 3 (poliçe taklidi) | 3 (sağlık/hayat dışarıda kalırsa) | 4 | Poliçe=fırsat sınırlaması müşteri ile sınanmalı | **2** |
| Kamu (ihale takibi) | 3–4 (gruba bağlı) | 4 | 2 (ihale bilgisi büyük ölçüde kamuya açık; gizli belge yok) | 3 | Ucuz; çalıştay sonucuna göre 2. sıraya çıkabilir | 3 |
| Finans | 3–4 | 2 | 5 (müşteri sırrı, denetim) | 6+ | S3 + düzenleyici yük | ertelenir |
| Sağlık (hasta odaklı) | 3 | 2 | 5 (özel nitelikli) | 6+ | B2B varyantı ucuz; hasta odaklı **engelli** | ertelenir |
| Diğer | 2–3 | 5 | 1 | 1 (şablonlar) | Grup Temeli içinde | — |

**Seçim:** 1–2 paket = **Ticaret + Sigorta** (öncesinde Grup Temeli); çalıştayda Kamu ihale ağırlığı yüksek çıkarsa Sigorta yerine Kamu.

### 2.4 Grup holdingin gerçek karışımı

Bilinen: 250 şirket, birkaç sektör `[D: kullanıcı brief'i]`; hangi şirketin hangi sektörde olduğu, sayıları ve mevcut araçları **bilinmiyor `[?]`**. Öngörülen etki: (a) az sayıda şirket büyük sektörde toplanıyorsa o paket ilk yatırımın büyük kısmını geri öder; (b) sektör başına bir–iki "pilot şirket" ile yükleme, geri bildirim ve sürüm-1 sonrası gerçek ihtiyaç belirlenir. Çalıştay çıktısı: şirket × sektör matrisi + her sektörde en çok 5 zorunlu nesne/süreç + mevcut sistemler (poliçe, ERP vb.) (§6).

## 3. Paket mekanizmasının teknik tasarımı

### 3.1 Mevcut desenler (yeniden kullanılır) `[D]`

| İhtiyaç | Yeniden kullanılan desen |
|---|---|
| Kiracıya varsayılan yapılandırma tohumlama | `DefaultPipelineSeeder` / `DefaultSlaPolicySeeder` (advisory lock, kiracı kapsamı, idempotent), `OrganizationCreated` olayı |
| Katalog yükleme | Migrator `sync-plans` komutu (yapılandırma → küresel tablo) |
| Uzun iş | `lists.jobs` + küresel `job_queue` (`FOR UPDATE SKIP LOCKED`, kira, kalp atışı; yeni `IgnoreQueryFilters` yok) |
| Modüllerin kendi türünü kaydetmesi | `CustomEntityRegistration`, `IReportSource`, `IUsageReporter` (modül → Contracts portu) |
| Yetki yükseltmeyi engelleme | `DelegationGuard` (M7 hardening), M8B "kapsam ⊆ oluşturan", M9G "eylem başına ek izin" |
| Yan etkisiz deneme | M9G kural `test`/`shadow` |
| Kapalı-katalog + sürümlü JSON tanım | M9F rapor tanımı (`"v":1`), M9B `FilterDefinition` |

**Tasarım kuralı 1 (M9G D9'dan):** paket uygulayıcı **genel komut gönderici (dispatcher) değildir**; yalnız `kind` başına dar, kayıtlı portları çağırır (mimari test: paket modülü başka modülün `Application`'ına referans veremez, dispatcher'a bağlanamaz). **Kural 2:** uygulama **isteği başlatan kullanıcının kimliğiyle** yürür (API anahtarı "oluşturan adına" desenindeki gibi), sistem aktörüyle değil; her port kendi `[RequiresPermission]`, plan limiti (`EntitlementBehaviour`), doğrulama ve denetimden geçer. Böylece paket, elle yapılabilecek olanın **ötesine geçemez** (ve `org.*` izinleri gerektiren nesneler yalnız yetkili yöneticide çalışır).

### 3.2 Modül yerleşimi

Yeni çekirdek (kapı olmayan) modül **`Sense.Crm.Modules.Packs`** (şema `packs`): `Domain/Application/Contracts/Infrastructure/Api`; yaprak modül (yalnız `Shared.*`). Her nesne türünün sahibi modül `Shared.Contracts.Packs.IPackKindHandler` uygular (`Kind`, `ContractVersion`, `Validate`, `Plan` (mevcut durumla kıyas, yan etkisiz), `Apply`, `Snapshot`, `Retire`); `Add<Modül>ContractServices()` içinde kaydolur (M8D `CustomEntityRegistration` deseni). Packs, hangi kind'lerin **yüklü/plana açık** olduğunu bu kayıttan öğrenir. Küresel tablolar (kiracı olmayan, `ITenantEntity` değil): `packs.catalog` (paket, sürüm, manifest özeti, imza durumu), `packs.job_queue`. Kiracı tabloları: `packs.installs` (kurulum), `packs.installed_objects` (defter: `pack, key, kind, target_id, base_hash, applied_version, status`), `packs.install_jobs`, `packs.apply_results`. Hepsi `TenantId` + global filtre; KVKK imhasında `TenantDataEraser`.

### 3.3 Manifest şeması (JSON kanonik)

YAML yazım kolaylığı **derleme adımında** JSON'a çevrilir; sunucu YAML ayrıştırmaz (etiket/çapa/milyar-kahkaha yüzeyi yok). Şema `manifestSchema: 1`, `additionalProperties:false`, yinelenen anahtar reddi.

```json
{
  "manifestSchema": 1,
  "code": "trade",
  "version": "1.2.0",
  "name": { "tr": "Ticaret (Mal Ticareti)", "en": "Trading (Goods)" },
  "publisher": "algosense",
  "requires": { "modules": ["commerce"], "kinds": { "customField": 1, "pipeline": 1, "savedView": 1 }, "packs": [{ "code": "group-base", "range": ">=1.0.0 <2" }] },
  "dataClasses": ["personal", "commercialSecret"],
  "objects": [
    { "kind": "customField", "key": "quote.incoterm", "entityType": "quote",
      "field": { "key": "incoterm", "dataType": "singleSelect", "labels": { "tr": "Teslim şekli", "en": "Incoterm" },
                 "dataClass": "none", "options": [ { "key": "fob", "labels": { "tr": "FOB", "en": "FOB" } } ] } },
    { "kind": "pipeline", "key": "deal.rfq", "pipeline": { "name": { "tr": "RFQ", "en": "RFQ" }, "stages": [ … ] } }
  ]
}
```

Kurallar: `objects[].key` paket içinde benzersiz, değişmez, `^[a-z][a-z0-9_.]{0,79}$`; her etiket **tr + en zorunlu** (lint); iç içe serbest metin yalnız düz metin etiket/yardım (React metin olarak basar; HTML yok); şablon gövdesi yalnız M9J `SafeTemplate` dilinde; filtre/kural gövdesi yalnız sahibi modülün mevcut kapalı DSL'inde (M9B `FilterDefinition`, M9G kural JSON'u) ve **sahibinin doğrulayıcısından** geçer. Yasak: URL/ana bilgisayar adı, `$ref`/dahil etme, ifade/betik, ikili veri, kimlik (GUID) — manifest **hiçbir kiracı/kayıt kimliği içermez** (çapraz-kiracı referans mümkün değil). Sınırlar: manifest ≤ 512 KiB, ≤ 2000 nesne, iç içe ≤ 8 düzey, dize ≤ 2000 karakter, dosya paketi ≤ 5 MiB.

**Nesne türü kataloğu** (kapalı; yeni tür = kod + mimari test + sürüm; `[D]` durum plana göre):

| `kind` | Sahibi | Bugün | Uygulama notu | Varsayılan durum / geri alma |
|---|---|---|---|---|
| `customField` (+ seçenekler, yerleşim bölümü) | Customization (M8D) | plan | `key`/`dataType` değişmez → tip değişimi = yeni anahtar; seçenek silinmez, **arşivlenir** | alan arşivlenir (silinmez) |
| `pipeline` (+ aşamalar, kayıp nedeni) | Sales (M2) | **kod var** | aşama kullanımdaysa silinmez (`pipeline.stage_in_use`) | kullanımdaysa bırakılır |
| `slaPolicy` (+ takvim bağı) | Service (M6B, M9J) | kod/plan | öncelik başına | eski değere dönmez; yalnız ayrılır |
| `tag`, `savedView` | Lists (M9B) | plan | kapalı renk paleti | kaldırılabilir |
| `report`, `dashboard`, `goal` | Analytics (M9F) | plan | kapalı katalog; hassas alan boyut olamaz | kaldırılabilir |
| `documentTemplate`, `emailTemplate` | Company (M9J) | plan | `SafeTemplate`; `org.templates.manage` | kaldırılabilir |
| `automationRule`, `assignmentRule` | Automation (M9G) | plan | **her zaman `shadow`/devre dışı** kurulur; etkinleştirme yöneticinin ayrı eylemi; `callWebhook` **yasak** | devre dışı bırakılır |
| `webForm` | Automation (M9G) | plan | halka açık yüzey: **taslak** kurulur, yayınlama ayrı eylem | taslak silinir |
| `role` (izin kümesi) | Identity (M9H) | kod/plan | izinler kapalı katalogdan; `org.*` **yok**; uygulayan kişi tüm izinlere sahip olmalı; **üye ataması yok** | üyesizse silinir |
| `workflowTemplate` | Workflows (X1 TPD) | X1 Faz 4 | TPD sürümü taslak olarak | taslak |
| `sampleData` | ilgili modül (ayrı paket) | — | §3.7 | §3.6 |
| `customObject` | (Özel Nesneler) | **yok** | gelecekte; şimdilik desteklenmez | — |

**Kesinlikle kapalı:** entegrasyon/webhook/API anahtarı/bağlayıcı, SSO, kullanıcı/üyelik/davet, plan/kiracı ayarı, sır/kimlik bilgisi, egress ana bilgisayarı, kuralın dış çağrı eylemi.

### 3.4 Uygulama akışı, idempotans, dry-run, fark

1. **Ön kontrol (sync, yan etkisiz; `POST /packs/{code}/plan`):** imza + şema + `requires` (modüller açık mı, `kinds` sürümleri yeterli mi), yetki (`org.packs.manage` + her kind'in gerektirdiği izinlerin başlatan kullanıcıda varlığı), plan limit payı (ör. `maxCustomFieldsPerEntity` aşılacak mı → **başlamadan reddet**), her nesne için `create | update | unchanged | conflict | skip(plan) | blocked`. Çıktı **fark** (nesne bazlı JSON fark + okunur özet); **dry-run = bu plan** (M9G `test` deseni).
2. **Onay + kuyruk:** `POST /packs/{code}/install` → `install_job` (kiracı başına aynı anda tek iş, `pg_advisory_xact_lock(tenant, pack)`), Worker `PackJobRunnerService` alır (M9B D16 deseni), **başlatanın kimliğinde** çalışır.
3. **Sıralı uygulama:** bağımlılık DAG'ı (alan → görünüm/rapor → kural; huni → kural). Her nesne kendi işlemi; **modüller arası atomik geri alma yok** (farklı DbContext); güvenlik "yeniden çalıştırılabilirlik"tir: uygulama **idempotent** olduğundan hata sonrası "devam/yeniden dene" aynı sonuca yakınsar. Sonuç nesne başına `created/updated/unchanged/conflict/failed` (`apply_results`), toplu `partial` durumu açık gösterilir.
4. **Kimlik (idempotans anahtarı):** `(packCode, objectKey)` → defter `target_id`. Defterde yoksa **adopt**: kiracıda aynı doğal anahtarlı nesne (ör. aynı `entityType+key` alan) varsa tanımı **uyumluysa** benimsenir (defter satırı açılır, `base_hash` = bugünkü kanonik hash), uyumsuzsa (ör. farklı `dataType`) `conflict`.
5. **Denetim:** kayıt değişiklikleri **kayıt denetiminde** `"Paket: <kod>@<sürüm> (<kullanıcı>)"` + `CorrelationId = pack:<installId>`; kurulum/kaldırma `IAuditLogged`; halka açık uç yok.

### 3.5 Çakışma, sürüm ve uyumluluk

**Üç yönlü karşılaştırma (yalnız paketin yönettiği özellikler üzerinde kanonik hash):** `base` (paketin en son uyguladığı), `current` (kiracıda şimdi), `new` (yeni sürüm).

| Durum | Davranış |
|---|---|
| `current == base` | güncelle (`update`) |
| `current != base`, `new == base` | kiracı düzenini **koru** (`unchanged`) |
| ikisi de değişti | **`conflict`: kiracıyı ezme yok**, raporla; yönetici nesne başına `keepTenant` (varsayılan) / `takePack` (denetimli) seçer |
| kiracı nesneyi sildi | yeniden yaratma (mezar taşı); yalnız "yeniden uygula" ile |
| paket nesneyi kaldırdı (`retire`) | varsayılan **ayrılır** (kiracıda kalır), `retire: archive` ile arşiv |
| kiracı ek nesne | dokunulmaz |

M8D değişmezlik kuralları nedeniyle **yazar lint'i**: `dataType`/`key`/`isSensitive`/`dataClass` değişimi → yeni anahtar; seçenek yalnız eklenir/arşivlenir; etiket değişimi serbest.

**Sürümleme:** paket `semver` (major = kaldırma/ayrılma içerir, onay ister; minor = ekleme; patch = etiket/açıklama). **Uyumluluk çekirdek sürümüne değil, kind sözleşme sürümüne bağlanır** (`requires.kinds.customField = 1`; her handler `[min,max]` bildirir; desteklenmiyorsa `pack.kind_unsupported`). Manifest ileri-uyumsuz katıdır (bilinmeyen alan → ret). **Geri düşürme desteklenmez** (kaldır + yeniden kur). **Veri dönüşümü yoktur** (paket veri taşıyamaz/dönüştüremez); gerekirse çekirdek migration'ı yazılır.

### 3.6 Kaldırma (veri kaybı yok)

| Mod | Anlam |
|---|---|
| **`detach` (varsayılan)** | Defter kapanır, nesneler kiracıya kalır (kiracı sahipliğine geçer); hiçbir şey silinmez |
| **`retire`** | Yalnız **kayıt verisine bağlı olmayan** nesneler kaldırılır (görünüm, etiket, rapor, pano, şablon, devre dışı kural, üyesiz rol, taslak form); alanlar **arşivlenir** (değerler satırda kalır, M8D D5), kullanımdaki huni/aşama, veri içeren alan, üyeli rol **bırakılır** ve raporda "korundu" görünür |
| **`removeSample`** | Yalnız `sample` etiketli, sonradan **kullanıcı etkileşimi almamış** kayıtlar yumuşak silinir; kalanlar rapor edilir; KVKK kalıcı silmesi kiracı imha hattına aittir (M7) |

### 3.7 Örnek veri ve KVKK

Örnek veri **ayrı, isteğe bağlı** pakettir (`sampleData` kind; varsayılan kapalı; üretim kiracısında yalnız açık onay kutusuyla). Yalnız **sentetik**: gerçek kişi/kurum yok; **geçerli görünen TCKN/VKN/IBAN/telefon yok** (M9E doğrulama toplamları nedeniyle örnek firmalarda vergi no boş bırakılır); özel nitelikli/duyarlı alan içermez. Kayıtlar `Origin = pack` (M9G D12 köken kuralı: otomasyon/bildirim/webhook **tetiklemez**), `sample` etiketi taşır, `maxRecords` sayacına girer (uyarı). Deterministik tohum (aynı çıktı).

### 3.8 İzin, plan ve denetim

- Yeni izin **`org.packs.manage`** (paket kurma/kaldırma/plan görme; `org.*` → API anahtarına **verilemez**, M8B D10). Kind izinleri (§3.4-1) ayrıca aranır: paket, kurucunun yapamayacağı bir şeyi yapamaz.
- Plan: çekirdek `features["packs"]`; sektör paketi kullanılabilirliği **katalog kuralı** (`Packs:Catalog` yapılandırması: paket × plan/kiracı izin listesi) + SaaS'ta `features["packs.<kod>"]` (ek modül); kapalı kind modülü → nesne `skip(plan)` + uyarı (başarısızlık değil). Kurulum sayısı (`maxInstalledPacks`) teknik tavan.
- `IUsageReporter`: `packs.installed` (kayıt sayısına girmez). Metrikler düşük kardinaliteli (paket kodu etiketi kapalı kümedir, kiracı kimliği asla).

### 3.9 Dağıtım (hava boşluklu veri merkezi) ve yerelleştirme

Paketler depoda `packs/<kod>/<sürüm>/{manifest.json, locales/*.json}`; derlemede `pack-lint` (Migrator `packs-validate`) ve imzalama; çıktı `.crmpack` (zip; iç içe arşiv yok, izinli dosya adları, zip-slip güvenli, ≤ 5 MiB). **İmza:** ayrık imza (ECDSA P-256; .NET yerleşik) manifest + dosya özeti üzerine; güvenilen anahtarlar operatör yapılandırmasında/Docker secret'ta (`Packs:TrustedKeys`, döndürme + iptal listesi); manifest `notBefore`/monoton sürüm denetimi (eski imzalı paketin tekrar oynatılması reddedilir). **Yükleme:** operatör paketleri sürüm artefaktıyla taşır (imaj/hacim `/packs`), Migrator `packs-sync` katalog tablosuna alır (`sync-plans` deseni); **çalışma zamanında internet/indirme yok** (K17 egress kapalı). Kiracı yöneticisi yalnız **katalogdaki** imzalı paketi kurar; kiracının kendi paketini yüklemesi v1'de **yok** (Faz 4 isteğe bağlı: imzasız "kiracı paketi", yalnız güvenli alt küme: alan/etiket/görünüm; rol/otomasyon/form yok).
Yerelleştirme: anahtarlar **dil-nötr İngilizce snake_case** (API/`cf.` süzgecinde görünür ve değişmez), etiketler `tr`+`en` zorunlu; ek diller `locales/` ile; TR'ye özgü içerik (il/ilçe) `GeoCatalog`'dan (M9E), örnek veri dil başına.

### 3.10 Güvenlik: manifest güvenilmeyen girdidir

| Tehdit | Kontrol |
|---|---|
| Kod/ifade çalıştırma | Kapalı kind kataloğu; betik/ifade/HTTP/`$ref` yok; DSL'ler sahibinin doğrulayıcısında; çalışma anında yorumlanmaz, **yalnız veri** olarak kaydedilir |
| Etiketle XSS / şablon enjeksiyonu | Yalnız düz metin; şablon `SafeTemplate` (M9J); HTML temizleme yok (HTML zaten yok) |
| JSON bombası / DoS | Boyut/derinlik/nesne sınırları; yinelenen anahtar reddi; plan limit payı **başlamadan** denetlenir; kiracı başına tek iş |
| ReDoS | Alan `pattern` M8D `NonBacktracking` + zaman aşımı kuralına tabidir |
| SSRF / veri sızdırma | URL alanı, webhook, bağlayıcı, `callWebhook` **kind/eylem olarak yok**; `webForm` taslak |
| Yetki yükseltme | Başlatan kimliğiyle çalışma, `org.*` rol yasağı, kullanıcıda olmayan izin yasağı, üye atama yok |
| Otomasyonla sessiz yan etki | Kural `shadow`/devre dışı; etkinleştirme ayrı, izinli eylem; `Origin=pack` yan etki bastırma |
| Tedarik zinciri | İmza + güven deposu + iptal listesi + değişmez sürüm; paket PR'ı `CODEOWNERS` + iki kişi + hukuk/alan sahibi onayı (duyarlı sınıflarda) |
| Çapraz-kiracı | Uygulama kiracı kapsamında; manifestte kimlik yok; defter kiracı satırı; çapraz-kiracı testi zorunlu |
| Zip-slip / yol gezme | Yalnız izinli düz dosya adları, dizin yok |

### 3.11 Test

| Test | İçerik |
|---|---|
| **Golden apply** | Sabit kiracı + manifest → `Snapshot()` kanonik JSON; depodaki altın dosya ile birebir; **iki kez uygula → sıfır değişiklik** (idempotans) |
| Yükseltme | vN-1 altını → vN; kiracı düzenlemesi (alan etiketi, yeni seçenek) korunur; çakışma senaryoları tablosu (§3.5) satır satır |
| Kaldırma | `detach`/`retire`/`removeSample` sonrası snapshot; **kayıt verisi değişmez** (değer/sayı) |
| **Çapraz-kiracı** | A'da kur → B'de hiçbir nesne/defter/iş görünmez; A'nın iş kimliği B'de 404; manifestte kimlik alanı reddedilir; kiracı imhası defteri siler |
| Güvenlik | Sınır dışı/bozuk/yinelenen anahtarlı/bilinmeyen kind'li manifest → ret; imza bozuk/eski/iptal edilmiş → ret; yetkisiz kullanıcı → `403`; `org.*` içeren rol → ret; API anahtarı → `403` |
| Plan | Limit payı yetersiz → başlamadan ret; kapalı modül → `skip(plan)` |
| Yerelleştirme | Tüm etiketlerde `tr`+`en` (lint), anahtar deseni |
| Mimari | Packs yaprak modül; genel dispatcher yok; yeni `IgnoreQueryFilters` yok; her kind'in handler kaydı |
| Fuzz / property | Rastgele nesne sırası → aynı sonuç (DAG); kısmi hata + yeniden dene → yakınsama |

## 4. Duyarlı veri: pakete öncelik veren ön koşullar

`[K]` KVKK özel nitelikli veri listesi (sağlık, cinsel hayat, biyometrik/genetik, ceza mahkûmiyeti, dernek/vakıf/sendika üyeliği vb.); işleme koşulları: açık rıza, kanunda açık düzenleme, sağlık hizmeti amaçlı yetkili kişiler vb.; veri sorumlusu **yeterli önlemleri** almalıdır (Kurul kararı 2018/10 anılıyor) — kvkk.gov.tr, okundu 2026-09-20. **Uyarı:** sayfanın 2024 KVKK değişikliğini (özel nitelikli veri koşulları ve yurt dışı aktarım) yansıtıp yansıtmadığı doğrulanmadı `[?]`; hukuk teyidi gerekir.

### 4.1 Paketten önce tamamlanacak ön koşullar (P1–P7)

| # | Ön koşul | Durum `[D]` | Not |
|---|---|---|---|
| P1 | **Veri sınıfı** (`dataClass`: `none / personal / special / financialSecret / commercialSecret / publicConfidential`) alan tanımında; bugünkü `isSensitive` bool yerine | M8D henüz merge değil → şimdi eklemek ucuz | `special` ve `financialSecret` varsayılan **gizli** |
| P2 | **Alan düzeyi izin** (`hidden/readOnly`) tüm okuma/yazma yollarında | M9H planı (D13/D14); M8D D11 bunu **kapsam dışı** bıraktı | Paket duyarlı alan içeriyorsa M9H'siz **kurulamaz** (`requires`) |
| P3 | Duyarlı alan **arama metninden, dışa aktarımdan, webhook zarfından, AI bağlamından ve rapor boyutundan** dışlanır | webhook zarfı PII'siz `[D]`; arama/AI/dışa aktarma bağlanmalı (M9A/X2/M9B) | `special` alanlar hiçbir dışa aktarmada varsayılan yok |
| P4 | **Okuma denetimi** (kim baktı) `special`/`financialSecret` alanlarda | Bugün denetim yalnız **değişikliği** kaydeder `[D]` | Yeni yetenek; küçük kart |
| P5 | **Açık rıza/aydınlatma kanıtı** (rıza durumu + tarih + kaynak) | M6C pazarlama izni benzeri olabilir, genel yok `[?]` | Paket "rıza" alan seti ve zorunluluk kuralı taşır; tam CMP değil |
| P6 | **Saklama/silme** (kayıt düzeyinde) | M7 yalnız kiracı imhası `[D]` | Sınıf başına saklama süresi ileride; v1'de belgelenmiş elle süreç |
| P7 | **Hukuki**: VERBİS/envanter, aydınlatma metni, veri sorumlusu/işleyen rolleri (grup şirketi=veri sorumlusu; SaaS'ta Algosense=işleyen `[?]` sözleşme), sektör düzenleyici yazışma | — | Her sektör için yazılı onay, paket sürüm koşulu |

### 4.2 Sektör başına duyarlılık ve paket kuralı

| Sektör | Duyarlı veri / yükümlülük | Kaynak/doğrulama | Paket kuralı |
|---|---|---|---|
| **Finans** | **Müşteri sırrı** (5411 md. 73; bankayla müşteri ilişkisini gösteren her bilgi dahil) ve BDDK "Sır Niteliğindeki Bilgilerin Paylaşılması Hakkında Yönetmelik" (RG 4.6.2021, 31501; yürürlük 1.1.2022); paylaşım/aktarım talimat ve kayıt disiplini; bankalar için bilgi sistemleri yönetmeliği: **birincil ve ikincil sistemler yurt içinde**, bulut/dış hizmet sağlayıcının kullandığı sistemler ve yedekleri dahil (RG 15.3.2020) | `[Ö]` Esin/Lexology/AA/BloombergHT arama özetleri 2026-09-20; yönetmelik metinleri okunmadı; **grubun finans şirketi bankacılık kanunu kapsamında mı (banka?) yoksa SPK/finansman/leasing mi bilinmiyor `[?]`**; SPK 6362 sır hükmü (md. 128 iddiası) **doğrulanamadı `[?]`** | Bakiye/pozisyon/limit **tutulmaz** (v1); KYC = durum+tarih; müşteri sırrı alanları `financialSecret` varsayılan gizli; belgeler Files klasör ACL; tarama/rapor yalnız görünür kayıtlar; veri merkezi yurt içi (zaten K13) |
| **Sigorta** | Acente ve çalışanlarının sır saklama yükümlülüğü, acente sicili (5684); sağlık/hayat branşı ve hasar kaydındaki yaralanma bilgisi = **sağlık verisi (özel nitelikli)**; SEDDK (Sigortacılık ve Özel Emeklilik Düzenleme ve Denetleme Kurumu) ve SBM veri paylaşımı | `[Ö]` 5684 (TSB/mevzuat.gov.tr arama özeti; sır hükmü madde numarası **doğrulanmadı**, arama sonucunda "m.30" görüldü `[?]`); SEDDK/SBM `[Ö]` | v1'de **sağlık/hayat branşı ve tıbbi/yaralanma alanı yok**; poliçe alanları ticari; araç/konut kimlik alanları `personal`; TCKN alanı yok (yalnız M9E vergi no doğrulaması) veya `personal`+maskeli; rıza P5 |
| **Trading** | Düşük: kişisel veri sınırlı; **ticari sır** (maliyet, marj, iskonto); yaptırım/ihracat kontrolü CRM dışı `[?]` | — | Marj/maliyet alanları `commercialSecret`, satış rollerinde `hidden` (P2) |
| **Kamu** | İhale bilgisi büyük oranda kamuya açık; yetkili kişilerin kişisel verisi (normal); **gizlilik dereceli belge CRM'de tutulmaz** (kabul edilebilir güvenlik seviyesi tanımsız `[?]`); ihale mevzuatı 4734 ve EKAP e-imza/KEP zorunlulukları | `[Ö]` mevzuat.gov.tr 4734, KİK/EKAP duyuruları, EKAP hizmet bedeli kararı 2025/DK.D-326 (hakedis.org, ikincil); "2026 değişiklikleri" blog yazıları **doğrulanmadı, kullanılmadı `[?]`** | Paket açıkça "gizlilik dereceli belge yükleme" uyarısı taşır; itiraz/şikayet süreleri paketin **yazılı doğru değeri olmadan** otomasyona bağlanmaz `[?]` |
| **Sağlık** | Sağlık verisi **özel nitelikli**; Kişisel Sağlık Verileri Hakkında Yönetmelik: yurt içi aktarım Kanun md. 8, **yurt dışı aktarım md. 9** ve 10.7.2024 tarihli, 32598 sayılı yurt dışı aktarım yönetmeliği | `[Ö]` kvkk.gov.tr, mevzuat.gov.tr, saglik.gov.tr arama özetleri; yönetmelik metinleri okunmadı | **Hasta odaklı paket P1–P7 tamamlanıp hukuk onayına kadar engelli**; B2B varyantı (kurum/cihaz/ihale) serbest; `special` alan paket manifestinde yasak (lint) |
| **Diğer/Grup Temeli** | Kişisel veri (iletişim); rıza | — | P5 rıza alan seti |

**Grup içi paylaşım:** K4/K2 gereği şirketler arası veri görünürlüğü yok; paket **yapılandırmayı** paylaşır, veriyi değil. Grup düzeyi konsolidasyon paketten bağımsız ayrı karar (K4).

## 5. Önerilen yol ve fazlı yol haritası

Sıra, **merge sırasına bağlı** (M8D → M9B → M9F/M9G/M9H → M9J): paket motoru yalnız o an yüklü/planlı kind'leri destekler; her kind ilgili kart merge olunca **küçük ek** olarak gelir.

| Faz | İş | Efor (ew) | Kart (Spec→Backend+Web) | Bağımlılık |
|---|---|---|---|---|
| 0 | **Spike** (manifest→`customField`/`pipeline` işleyici prototipi, idempotent uygulama + fark, 3 yönlü hash, poliçe=fırsat kısıt ölçümü) + **keşif çalıştayı** (§6) + A rehberleri (1 sayfa/sektör) | 2 | **C-S0** (Spec/Backend spike) | M8D planı; çalıştay katılımcıları |
| 1a | **Paket çekirdeği spec** (plan + HTTP kontratı + manifest v1 + kind portu + imza) | 1,5 | **C-S1** (Spec) | Faz 0 çıktısı |
| 1b | **Backend:** `Packs` modülü, katalog (`packs-sync`), imza/güven deposu, defter, plan/fark/uygulama işi, `detach`/`retire`, kind'ler `customField+layout`, `pipeline`, `slaPolicy`, `tag`, `role`; izin `org.packs.manage`; testler §3.11 | 9 | **C-S2** (Backend) | M8D merge; M2/M6B mevcut |
| 1c | **Web:** katalog, önizleme/fark, çakışma çözümü, kurulum ilerlemesi/sonuç, geçmiş, kaldırma | 4 | **C-S3** (Web) | C-S2 kontratı |
| 1d | **Duyarlı veri ön koşulları** (`dataClass`, M9H alan izni bağlama, arama/dışa aktarma/AI dışlama, okuma denetimi, rıza alan seti) | 3 (M9H'nin kendisi hariç) | **C-D1** (Spec→Backend+Web) | M8D (`dataClass` merge öncesi), M9H |
| 2 | **Kind'ler 2:** `savedView`, `report/dashboard/goal`, `automationRule/assignmentRule/webForm` (gölge/taslak), `documentTemplate/emailTemplate`, `sampleData` | 5 | **C-S4** (Backend+Web) | M9B, M9F, M9G, M9J merge |
| 2b | **Küçük uzantı S2:** `product` + `quoteLine/orderLine` özel alan, lookup tipi, süzülebilir alan tavanı | 2–3 | **C-S5b** (Spec→Backend+Web) | M8D/M9C merge (yalnız Ticaret için gerekli) |
| 3a | **İçerik:** Grup Temeli + Ticaret (manifest, TR/EN, altın testler, hukuk/alan sahibi onayı) | 4,5 | **C-S5** (Spec→içerik) | C-S2 (MVP), C-S4 kısmen |
| 3b | **İçerik: Sigorta (acente)** (poliçe=fırsat, yenileme kuralı, hasar=talep, rapor/pano) | 4 | **C-S6** | C-S4, çalıştay, hukuk |
| 3c | (İsteğe bağlı) **Kamu ihale** | 3 | **C-S6b** | çalıştay sonucu |
| 4 | **Yükseltme/filo:** 3 yönlü yükseltme arayüzü, yeni kiracıda otomatik `baseline` uygulama (`OrganizationCreated`), operatör onaylı "teklif edilen yükseltme" ve halka (canary) dağıtımı, kiracı paketi (isteğe bağlı) | 5 | **C-S7** | C-S2/S3, karar #7 |
| — | **Özel Nesneler analizi** (poliçe/hesap/teminat/sözleşme birinci sınıf mı?) | 1 (analiz) + ~20–30 (yapım, kaba) | **C-X8** (analiz) | Faz 0 kanıtı |

**Toplamlar (±%40):** MVP dilimi (Faz 0 + 1a–1d + 3a, otomasyon/rapor olmadan) ≈ **24 ew**; ilk tam yayın (+ Faz 2, 2b, 3b) ≈ **35 ew**; filo/yükseltme (+ Faz 4) ≈ **40 ew**. 2–3 mühendisle takvim tahmini 3–4 ay (MVP ≈ 2 ay). **Paralellik:** Web, Backend'in kontratı kilitlenince başlar; içerik (3a) motorun MVP'si sonrası; Sigorta içeriği çalıştaydan.

## 6. Riskler ve spike/keşif gerektiren bilinmeyenler

| # | Risk / bilinmeyen | Etki | Aksiyon |
|---|---|---|---|
| R1 | **Grubun gerçek sektör karışımı ve öncelik** bilinmiyor | Yanlış paket sırası | **Keşif çalıştayı** (aşağıda) |
| R2 | **Poliçe = fırsat** taklidi gerçek acenteyi tatmin etmeyebilir (poliçe başına yenileme zinciri, sigortalı ilişkisi, komisyon, çok şirketli teklif) | Sigorta paketi değer kaybı; Özel Nesneler baskısı | Spike: 2–3 gerçek acente kullanıcısıyla kağıt üstü senaryo + prototip; **karar eşiği:** ≥2 şirket poliçe bazlı raporlama/yenileme zinciri ister → C-X8 başlat |
| R3 | M8D sınırları (süzülebilir ≤10, liste sütunu ≤5, tür başına ≤200, lookup yok, GIN yalnız eşitlik/içerme) sektör paketine **dar** olabilir (poliçe ≈ 20–25 alan) | Kullanılabilirlik | Faz 0'da alan bütçesi ölçümü; gerekirse M8D tavanlarında paket başına ayrılmış "indeksli alan bütçesi" (S2) |
| R4 | Paket motoru **merge edilmemiş** planlar üzerinde (M8D, M9B/F/G/H/J) → kontrat değişirse yeniden iş | Efor artışı | Kind ekleme kart sonrası; motor çekirdeği yalnız M2/M6B mevcut kind'lerle başlar; `ContractVersion` |
| R5 | Sürüm/çakışma anlambilimi karmaşık; kiracı özelleştirmesi yaygınlaşınca sapma | Destek yükü | Varsayılan "kiracıyı ezme"; altın testler; kaç şirket serbest düzenliyor ölçümü |
| R6 | **İçerik bakım sahipliği**: sektör içeriği düzenleyici/ürün değişimi ile eskir (ör. branş kodları, ihale süreleri) | Yanlış içerik | Her pakete "alan sahibi" (grup şirketinden) + sürüm günlüğü + yıllık gözden geçirme |
| R7 | **Hukuki doğrulamalar** (bkz. §4: BDDK/SPK/SEDDK kapsamı, KVKK 2024 değişikliği, ihale süreleri, sağlık) | Yasak/yanlış paket | Sektör başına yazılı hukuk görüşü; doğrulanmamışlar paket lint'ine alınmaz |
| R8 | İmza anahtarı yönetimi/döndürme/iptal (hava boşluklu) | Tedarik zinciri | Faz 1a: anahtar yaşam döngüsü + runbook maddesi; DevOps |
| R9 | 250 kiracıda **eşzamanlı** paket uygulaması → Worker/DB yükü | Performans | Kiracı başına tek iş, sıralı halka (canary), hız sınırı, `maxConcurrentPackJobs` |
| R10 | "Trading" = mal ticareti mi, finansal alım-satım (SPK) mı? | Kapsam | Çalıştayda net tanım (§7-4) |
| R11 | Otomasyonlar 250 kiracıda **kapalı** kalıp değer üretmeyebilir | Değer kaybı | Yayın kontrol listesi + "önerilen etkinleştirme" kılavuzu; gölge sonucundan tek tık etkinleştirme (M9G) |
| R12 | **Dış doğrulanamayanlar:** yerli CRM'lerin dikey paketleme pratiği, EKAP verisine üçüncü taraf erişimi, MASAK/ÜTS/MEDULA gibi sektör sistemleri, SBM ürün kodları | Yanlış varsayım | Çalıştayda müşteri kanıtı; işaretli `[?]` |

**Keşif çalıştayı (½–1 gün, ürün + BT + 4–6 şirket temsilcisi):** (1) şirket × sektör matrisi (250 şirketin sektör dağılımı, ilk 10–20 şirket ayrıntılı); (2) her sektörde en çok 5 zorunlu nesne/süreç ve bugün nerede tutuluyor (Excel, ERP, poliçe/portföy yazılımı); (3) hacimler (poliçe/ihale/teklif/yıl), kayıt başına alan sayısı; (4) hangi şirket düzenleyici kapsamda (banka/finansman/aracı kurum/sigorta acentesi/hastane) ve hukuk/uyum sorumlusu; (5) mevcut entegrasyonlar (ERP, çekirdek sistem, poliçe yazılımı; X3 keşfiyle birleşik yürütülebilir); (6) "Trading" tanımı; (7) gerçek ihtiyaç: yalnız yapılandırma mı, yoksa poliçe/teminat gibi nesne bazlı raporlama mı; (8) pilot şirket adayı ve paket alan sahibi.

## 7. Ürün sahibinin vereceği kararlar (öneri cevabıyla)

1. **Paket modeli?** *Öneri:* **E (A hemen + B kademeli)**; C ve D yapılmaz/ertelenir.
2. **İlk paketler?** *Öneri:* **Grup Temeli → Ticaret → Sigorta (acente)**; Kamu ihale çalıştaya göre 2. sıraya çıkabilir; Finans ve hasta odaklı Sağlık ertelenir.
3. **Yeni varlık türleri (Özel Nesneler) v1'de yapılsın mı?** *Öneri:* **Hayır**; poliçe=fırsat, hasar=talep, ihale=fırsat taklidi; eşik R2'de tanımlı; C-X8 analiz kartı açılsın.
4. **"Trading" tanımı?** *Öneri:* **mal/emtia ticareti**; finansal alım-satım (SPK düzenlemeli) kapsam dışı; çalıştayda teyit.
5. **Paket yazarı ve sahipliği?** *Öneri:* Ürün ekibi yazar ve sürümler; her pakete grup şirketinden **alan sahibi** atanır; duyarlı sınıflarda hukuk onayı yayın koşuludur.
6. **Kim paket yükleyebilir?** *Öneri:* v1'de yalnız **operatör imzalı katalog**, kurucu kiracı yöneticisi (`org.packs.manage`); kiracının kendi paketini yüklemesi Faz 4'te isteğe bağlı ve yalnız güvenli alt küme.
7. **Filo dağıtımı ve yetki:** platform yöneticisi kiracı verisini okuyamaz (K18) ve yapılandırma yazmak da kiracı kararıdır. *Öneri:* yeni kiracıda yalnız operatörün seçtiği `baseline` paketi otomatik; diğerleri **kiracı yöneticisi onaylı "teklif edilen yükseltme"**; grup içi kiracılar için operatör yapılandırmasında açık, denetlenen **ön-yetki** bayrağı seçeneği.
8. **Duyarlı veri politikası:** *Öneri:* özel nitelikli alan paket manifestinde **yasak** (lint); `financialSecret` ve `commercialSecret` varsayılan gizli; hasta odaklı paket ön koşul P1–P7 ve hukuk onayına kadar **engelli**.
9. **`dataClass` M8D'ye şimdi eklensin mi?** *Öneri:* **Evet** (merge öncesi; `isSensitive` bool onun türevi), C-D1 ile birlikte.
10. **Plan/fiyat:** *Öneri:* motor çekirdek (`features["packs"]`); SaaS'ta sektör paketi ek modül (`features["packs.<kod>"]`); grup içi hepsi açık.
11. **Örnek veri:** *Öneri:* varsayılan kapalı; yalnız demo/eğitim kiracıları; üretimde açık onay ve sentetik veri kuralı.
12. **Manifest biçimi:** *Öneri:* JSON kanonik + YAML yazım kolaylığı derlemede.
13. **Kaldırma varsayılanı:** *Öneri:* `detach`; `retire` yalnız kayıt verisine bağlı olmayan nesneler; veri hiçbir modda silinmez.
14. **Hukuk/danışman bütçesi:** *Öneri:* Sektör başına yazılı hukuk görüşü (özellikle Finans ve Sağlık öncesi), ilk yayın öncesi Sigorta ve Ticaret için kısa görüş.

## 8. Dış kaynaklar (erişim 2026-09-20)

| # | Kaynak | Ne için | İşaret |
|---|---|---|---|
| S1 | help.salesforce.com "Data Models for Financial Services Cloud"; developer.salesforce.com "Insurance | Financial Services | Data Model Gallery"; Trailhead "Insurance Data Modeling" (arama özeti) | Sigorta nesneleri (Policy, Claim, Producer, Renewed From Policy vb.) | `[Ö]` |
| S2 | help.salesforce.com "Omnistudio Package and Salesforce Industries Package", Omnistudio DataPacks (arama özeti) | Industries paketleme (yönetilen paket, DataPack) | `[Ö]` |
| S3 | zoho.com/verticalstudio (okundu); zoho.com/crm/verticals (arama özeti) | Zoho dikey paketleme | `[K]` / `[Ö]` |
| S4 | github.com/microsoft/Industry-Accelerator-FinancialServices; learn.microsoft.com/industry/financial-services/dynamics-365-fsi (okundu, genel metin) | Dynamics endüstri hızlandırıcıları | `[Ö]`; güncel destek durumu `[?]` |
| S5 | kvkk.gov.tr/Icerik/2051/Ozel-Nitelikli-Kisisel-Veriler | KVKK md. 6 listesi ve koşulları | `[K]`; 2024 değişikliği yansıması `[?]` |
| S6 | kvkk.gov.tr Kişisel Verilerin Yurt Dışına Aktarılması Rehberi; mevzuat.gov.tr/saglik.gov.tr Kişisel Sağlık Verileri Hakkında Yönetmelik (arama özeti) | Sağlık verisi aktarımı, 10.7.2024 / 32598 yönetmelik | `[Ö]` |
| S7 | AA ve BloombergHT haberleri; bddk.org.tr Mevzuat "Bankaların Bilgi Sistemleri ve Elektronik Bankacılık Hizmetleri" (arama özeti); RG 15.3.2020 | Birincil/ikincil sistemlerin yurt içi zorunluluğu | `[Ö]` |
| S8 | Esin Avukatlık, Lexology, GSG Hukuk (arama özeti) | 5411 md. 73, Sır Niteliğindeki Bilgilerin Paylaşılması Hakkında Yönetmelik (4.6.2021, 31501; 1.1.2022) | `[Ö]` |
| S9 | tsb.org.tr / mevzuat.gov.tr 5684; seddk.gov.tr (arama özeti) | Acente sır yükümlülüğü, sicil; SEDDK/SBM | `[Ö]`; madde numarası `[?]` |
| S10 | mevzuat.gov.tr 4734; kik.gov.tr; ekap.kik.gov.tr; hakedis.org (2025/DK.D-326, 1.1.2026) | Kamu ihale çerçevesi ve EKAP | `[Ö]`; ihalepro/ihaleyonetim "2026 değişiklikleri" yazıları `[?]` (kullanılmadı) |

**Doğrulanamayanlar (kesin dille yazılmadı):** SPK 6362 sır hükmü ve madde numarası, MASAK yükümlülükleri, ÜTS/MEDULA/EKAP üçüncü taraf erişimi, KİK itiraz/şikayet süreleri, 4982 yasal süreleri, SBM ürün/branş kodları, yerli CRM'lerin dikey paketleme pratiği, Dynamics hızlandırıcılarının güncel destek durumu, grubun hangi şirketinin hangi düzenleyici kapsamda olduğu.
