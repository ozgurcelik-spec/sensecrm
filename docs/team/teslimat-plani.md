# Teslimat planı — en küçük çalışan parçalar

Amaç: ürünü büyük kartlar yerine, her biri **tek başına çalışan, dağıtılabilir ve kullanıcıya bir şey kazandıran** ince dilimlerle teslim etmek. Pano ([board.md](board.md)) kartların sahibini ve durumunu, bu belge **teslim sırasını ve dilimleri** tutar. Kart planları (`docs/plan/*.md`) bağlayıcı kontrat olarak kalır; bu belge onları dilimlere böler.

## Dilim kuralları

Bir dilim ancak hepsi doğruysa "teslim edildi" sayılır:

1. **Uçtan uca kullanılabilir:** API + web (TR/EN) + yetki + plan bayrağı; yarım ekran veya ölü uç yok. Bitmemiş yetenek varsayılan kapalı (plan/özellik bayrağı) ya da hiç görünmez.
2. **Küçük:** tek ajan, tek dal, en çok ~3 saatlik ajan işi; her migration geri alınabilir. Büyük kartlar birden çok dilime bölünür, her dilim ayrı ayrı `main`'e girer.
3. **Kapı yeşil:** build 0 uyarı, `format`, `restore --locked-mode`, tüm testler; web `tsc`/`eslint`/`vitest`/`build`; yeni kiracı varlığı için çapraz-kiracı testi; **E2E'ye o dilimin mutlu yolu** eklenir.
4. **Güvenli yükseltme:** mevcut veriyle migration (dolu tablo), varsayılanlar bugünkü davranışı aynen korur, runbook'a yükseltme/geri alma notu.
5. **Bağımsız merge:** sonraki dilime bağlı değildir; sonraki dilim gecikirse ürün bozulmaz.

## Sürüm kuralları

Bir sürüm = birkaç dilimin toplamı. Sürüm etiketi (`v0.N.0`) şunlar tamamsa atılır: tam backend + web test paketi, Playwright E2E paketi (`e2e/run.*`) yeşil, canlı duman (`deploy/smoke.sh`), yedek → geri yükleme provası (şema/veri değiştiren sürümlerde), `docs/operations/runbook.md` güncel, kısa sürüm notu. Etiket atılınca pilot yığına (`deploy/docker-compose.prod.yml`) yükseltilir.

## Öncelik sırası ve gerekçe

Öncelik ölçütleri (sırayla): (1) **pilotu güvenle açmak** (güvenlik + kararlılık), (2) **günlük kullanımı belirgin kolaylaştıran** parçalar, (3) **çok sayıda karta bağımlı temeller** (özel alanlar, erişim modeli), (4) Zoho eşdeğerliği için "olsa iyi olur" parçalar. Bitmiş ama birleşmemiş iş her zaman yeni işten önce gelir.

| Sıra | Sürüm | Ne kazandırır | Neden bu sırada |
|---|---|---|---|
| 1 | **v0.1 Güvenli pilot** | Bugünkü çekirdek + sertleştirme; gerçek kullanıcı çalışabilir | C-SEC2 hazır ve prod açmanın ön koşulu; E2E bulguları küçük ama görünür |
| 2 | **v0.2 Bildirimler** | Uygulama içi + e-posta bildirim, hatırlatma/SLA | Backend hazır (web `main`'de); günlük kullanımı en çok değiştiren küçük parça |
| 3 | **v0.3 Özel alanlar** | Kiracıya özel alan | Backend hazır; M9B/M9E/M9H ve dilimlerin temeli; grup şirketlerinin farklı alan ihtiyacı |
| 4 | **v0.4 Satış belgeleri** | Fatura, tahsilat, fiyat listesi, satın alma | Backend `main`'de, web yolda; satış ekibi için doğrudan değer |
| 5 | **v0.5 Çalışma alanı** | Menü, arama, İş Kuyruğu, ana sayfa | Backend bitiyor; her kullanıcıya her gün dokunur |
| 6 | **v0.6 Listeler** | Kayıtlı görünüm, filtre, toplu işlem, içe/dışa aktarma | Veri girişini/taşımayı kolaylaştırır (pilot için içe aktarma kritik) |
| 7 | **v0.7 Erişim modeli** | Kayıt görünürlüğü, alan izni, giriş geçmişi | 250 şirketlik grupta veri ayrımı; **portal/SSO/dış kullanıcıdan önce zorunlu** |
| 8 | **v0.13 Şirket ayarları** (J1–J2) | Para birimi, iş takvimi/SLA | Rapor/belge sürümlerinin girdisi; backend başladı |
| 9 | **v0.8 Aktivite** | Takvim, toplantı, anımsatıcı, tekrar | M8A sonrası; değer yüksek ama v0.5'e göre ikincil |
| 10 | **v0.9 Alan ve form** | Alan paritesi, yinelenen uyarı | M8D + M9B sonrası; veri kalitesi |
| 11 | **v0.10 Rapor ve analitik** | Hazır raporlar, pano kurucu | M9B/M9H/M9J sonrası; erken çıkarsa yanlış kapsamla rapor verir |
| 12 | **v0.11 Otomasyon** | Atama, web formu, kural motoru | Halka açık uç + kullanıcı tanımlı eylem: güvenlik riski yüksek, v0.7 sonrası |
| 13 | **v0.12 Destek ve belgeler** | Bilgi tabanı, klasörler | Servis ekibi için; en sona yakın, bağımsız |

v0.13'ün J3 (şablon + PDF) dilimi, PDF denemesi geçtikten ve ihtiyaç doğduktan sonra (fatura/teklif çıktısı) öne alınabilir.

## Sürümler ve dilimler

| Sürüm | Dilimler (her biri ayrı merge) | Durum |
|---|---|---|
| **v0.1** | R1a: C-SEC2 birleştir · R1b: E2E bulguları F-1, F-2, F-4, erişilebilirlik · R1c: Prometheus'a `Sense.Crm.Files`/`Integrations` metrik adları + prod `.env` yükseltme notu (`TRUSTED_PROXY_CIDR`, `EDGE_SUBNET`) · R1d: sürüm provası (yedek/geri yükleme, smoke, E2E) + etiket | M8B/M8C `main`'de; C-SEC2 birleştiriliyor |
| **v0.2** | N1: bildirim çekirdeği + zil/tercihler · N2: e-posta relay + kiracı ayarları · N3: hatırlatma/SLA zamanlayıcıları | Backend hazır, birleşme sırada |
| **v0.3** | C1: alan tanımı yönetimi + firma/kişi · C2: diğer varlıklar · C3: liste kolonu + `cf.` filtre/sıralama | Backend hazır, web çalışıyor |
| **v0.4** | S1: teklif/sipariş alan paritesi + Kaydet ve Yeni · S2: fatura + tahsilat · S3: fiyat listeleri · S4: tedarikçi + satın alma · S5: `LookupDialog` hızlı oluşturma | Backend `main`'de, web çalışıyor |
| **v0.5** | A1: gruplu menü + hızlı oluştur + rozetler · A2: genel arama (`pg_trgm`) · A3: İş Kuyruğu + Ana Sayfa widget'ları | Backend bitiyor |
| **v0.6** | L1: filtre + kayıtlı görünüm + kolon seçici · L2: etiketler · L3: toplu işlemler · L4: CSV dışa aktarma · L5: CSV içe aktarma (kuru koşu + hata raporu) | Plan hazır |
| **v0.7** | H0: EF sorgu-filtresi denemesi (spike) · H1: yönetici hattı + kayıt görünürlüğü (varsayılan tüm organizasyon) · H2: alan izinleri · H3: giriş geçmişi + oturumlar | Plan hazır |
| **v0.8** | D1: gecikme kolonları + arama kaydı · D2: toplantı + RSVP · D3: takvim · D4: anımsatıcılar · D5: görev tekrarı | Plan hazır |
| **v0.9** | E1: firma/kişi alanları + telefon/VKN doğrulama · E2: potansiyel/fırsat/talep alanları · E3: yinelenen uyarı + dönüştürme önizleme · E4: il/ilçe seçimi | Plan hazır |
| **v0.10** | F0: dinamik GroupBy denemesi (spike) · F1: hazır raporlar · F2: pano kurucu · F3: hedefler · F4: rapor kurucu · F5: öngörü | Plan hazır |
| **v0.11** | G1: atama kuralları + M4 kural göçü · G2: web formları · G3: kural motoru | Plan hazır |
| **v0.12** | I1: klasörler + ACL · I2: çözümler · I3: talep–çözüm bağı · I4: arama entegrasyonu | Plan hazır |
| **v0.13** | J1: profil + para birimleri + kurlar · J2: iş takvimi + SLA + tatil paketi · J3: şablonlar + PDF | Backend çalışıyor |

## Analiz kartları (karar bekleyen, sürüm planının dışında)

C-X1 BPMN, C-X2 AI, C-X3 ERP/imza/SFTP, C-X4 portal, C-X5 Kubernetes, C-X6 SSO, C-X7 sektör paketleri: `docs/analysis/x*.md`. Hangisinin hangi sürümden sonra yapılacağı ürün sahibinin kararına bağlıdır; her birinde en küçük ilk dilim (spike veya Faz 0) belgede tanımlıdır.

## Çalışma düzeni

- Toplam ajan sayısı 8 kalır; her ajan **bir dilim** yapar, bitince dal `main`'e girer, worktree silinir, öncelik tablosundaki sıradaki dilim açılır.
- Yeni ajan istemleri kartın tamamını değil, bu tablodaki **tek dilimi** ister; dilim bitince ajan raporlar ve durur (devamı ayrı görev).
- Aynı sıcak dosyalara (Platform limitleri, `Entitlements.cs`, Program.cs, resx, belge numaraları) dokunan dilimler sırayla merge edilir; iki açık dal aynı anda aynı tabloya migration eklemez.
- Boşalan yuvaya sırayla: (1) bitmiş ama birleşmemiş işin birleştirme/çakışma çözümü, (2) sürüm sırasındaki en öndeki hazır dilim, (3) sonraki dilimin Spec/Backend/Web işi, (4) analiz kartları.
- Sürüm etiketi ve pilot yükseltmesi Lead'in işidir; etiket atılmadan sonraki sürüme geçilmez.
