# Zoho CRM ekran analizi ve eksik listesi

Kaynak: `docs/screenshot/` altındaki 30 ekran görüntüsü (Zoho CRM, Türkçe arayüz, 20.09.2026). Karşılaştırma tarihi: 20.09.2026, `main` = M1–M7 birleşik, M8 (bildirim, webhook/Open API, dosya eki, özel alanlar) geliştirme aşamasında. Not: Ekran görüntüleri şirket adı, e-posta ve telefon içerdiği için depoya eklenmemelidir (`docs/screenshot/` git dışında tutulur).

## 1. Görüntülerin kapsamı

| # | Ekran | Ne gösteriyor |
|---|---|---|
| 1 | Ana Sayfa | KPI kartları (açık fırsatlarım, dokunulmamış fırsatlar, bugünkü aramalar, bana ait potansiyeller), açık görevlerim, toplantılarım, bugünün potansiyelleri, bu ay kapanan fırsatlar, aşamaya göre fırsat hunisi; gruplu sol menü |
| 2 | İş Kuyruğu | Görevler/Toplantılar/Aramalar sekmeleri (sayaçlı), "Bugün ve Geciken" filtresi, kayıt bazlı kayıtlı kuyruklar (bana ait potansiyeller, son 3 saatte atananlar, bu ay kapanan fırsatlar, aktif kampanyalar), gecikme kırmızı ("6 gün geç") |
| 3 | Raporlar | Klasörlü rapor kütüphanesi (E-posta, Toplantı, Satış Ölçüm, Fatura, Sipariş, Teklif, Satın Alma Emri), ad/açıklama/klasör/son erişim/oluşturan, favori yıldızı, arama, "Rapor Oluştur" |
| 4–6 | Analitikler | Pano seçici ve "Pano Oluştur", "Bileşen Ekle"; bileşen türleri: KPI, Grafik, Karşılaştırıcı, Kohort, Bölge, Huni, Hedef Ölçer, Anomali Dedektörü, Çeyrek Daire, Aşama; hedef göstergeleri (aylık potansiyel hedefi, yıllık ciro hedefi), son 3 ay performansı, kaynağa göre potansiyel (halka grafik), üretken satış temsilcileri |
| 7–8 | Müşteri Adayları, Kişiler listesi | Kayıtlı görünümler, sol filtre paneli (sistem tanımlı filtreler + alanlara göre + ilgili modüllere göre), görünüm türleri (liste/kanban/…), sütun seçici, toplu seçim, satır göstergeleri (gecikmiş aktivite, not), tıkla-ara, sayfalama |
| 9 | Kişi Oluştur | Çok bölümlü form, "Sayfa Tasarımını Düzenle", "Kaydet / Kaydet ve Yeni", iki adres bloğu ve "Adres Kopyala", koordinat, ~25 alan (asistan, doğum tarihi, Skype, Twitter, raporlama yapılan kişi, e-posta gönderilmesin…), "Form Görünümleri Oluştur" |
| 10–11 | Satış Fırsatları | Aşama kanban'ı (10 aşama, olasılık ve toplam tutar başlıkta), fırsat formu (tip, beklenen ciro=tutar×olasılık, potansiyel kaynağı, kampanya kaynağı, sonraki adım, olasılık aşamadan) |
| 12 | Belgeler | Klasörlü belge alanı (WorkDrive), paylaşım |
| 13–14 | Kampanyalar | Tip listesi (Konferans, Webinar, Ticari Fuar, Halkla İlişkiler, Ortaklar, Yönlendirme Programları), durum (Planlanıyor, Etkin, Etkin Değil, Tamamlandı), bütçelenen/gerçekleşen masraf, beklenen ciro, beklenen tepki, gönderilme sayısı |
| 15–16 | Görevler | Liste + filtre paneli; görev formu: konu, son tarih, ilgili (kişi/firma), durum, öncelik, **Tekrarla**, **Anımsatıcı** |
| 17 | Toplantılar | Başlık, başlangıç/bitiş, ilgili, sahip; tüm gün, konum, sağlayıcı, davetliler, **giriş (check-in)** alanları |
| 18 | Aramalar | Arama tipi (gelen/giden), saat, süre, ilgili; ajanda, neden, sonuç, giden arama durumu |
| 19 | Fiyat Listesi | Ad, etkin, fiyatlandırma modeli |
| 20–24 | Teklif, Sipariş, Satın Alma Emri | Kalemli form (ürün, miktar, fiyat, tutar, indirim, vergi, toplam), ara toplam/indirim/vergi/**yuvarlama**/genel toplam, fatura ve teslimat adresi, nakliye, **şartlar ve koşullar**, satın alma emri no, gider vergisi, satış komisyonu, durum; **arama penceresi** ("Satış Fırsatı Seç": arama + tablo + "Yeni Satış Fırsatı") |
| 25–26 | Faturalar | Boş durum (oluştur/içe aktar), fatura formu (sipariş, satın alma emri, fatura tarihi, son tarih, durum…) |
| 27 | Tedarikçiler | Resim, ad, telefon, e-posta, web, DK hesabı, kategori, adres |
| 28 | Çözümler | Bilgi tabanı: çözüm no, başlık, ürün, durum (Taslak), soru, cevap |
| 29 | Ayarlar → Modüller | Modül aç/kapa listesi, özel modül, ekip modülü; sol ayar ağacı: Güvenlik (profiller, roller ve paylaşım, bölge yönetimi, güvenilir alan, SAML, Active Directory, oturum açma tarihçesi, denetim günlüğü), Kanallar (e-posta, telefon, SMS, **webformları**, sosyal, sohbet, portallar), Özelleştirme (modüller ve alanlar, işlem hatları, sihirbazlar, şablonlar, çeviriler), Otomasyon (iş akışı kuralları, işlemler, programlar, atama, **puanlama kuralları**, cadence'ler), İşlem Yönetimi (şema/blueprint, onay işlemleri, gözden geçirme) |
| 30 | Şirket ayarları | Şirket bilgileri, mali yıl, çalışma saatleri, tatiller, **para birimleri**, alan adı eşleme, saat dilimi |

## 2. Bizde durum ve boşluk

Simgeler: ✅ var, 🟡 kısmen, ❌ yok. Öncelik: P0 pilot için önemli, P1 Zoho'nun yerine geçmek için gerekli, P2 sonra.

### Kabuk ve gezinme
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Gruplu sol menü (Satışlar / Aktiviteler / Envanter / Destek) | 🟡 düz liste | Gruplama, daraltılabilir bölümler | P1 |
| Genel arama ("kayıtlar ara") | ❌ | Tüm modüllerde tek arama kutusu, son aranan, kısayol | P0 |
| Hızlı oluştur "+" menüsü | ❌ | Her kayıt türü için kısayol | P1 |
| İş Kuyruğu (kayıtlı kuyruklar, sayaçlar, gecikme) | 🟡 Aktiviteler hızlı filtreleri | Kullanıcı bazlı kuyruklar, sayaçlar, "son 3 saatte atanan", modül geneli | P0 |
| Ana Sayfa widget'ları | 🟡 4 kart + 3 grafik | Açık görevlerim, toplantılarım, bugünün potansiyelleri, bu ay kapanan fırsatlar, dokunulmamış fırsatlar, bugünkü aramalar | P0 |
| Takvim | ❌ | Etkinlik/görev/arama takvim görünümü | P1 |

### Liste deneyimi
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Kayıtlı görünümler (özel filtre ve sütun) | ❌ | Kullanıcı/paylaşılan görünümler | P0 |
| Sol filtre paneli (alan bazlı, sistem filtreleri: dokunulmamış/dokunulmuş, aktiviteler, kampanya, kilitli) | 🟡 birkaç filtre | Gelişmiş filtre oluşturucu, "dokunulmamış" mantığı | P0 |
| Sütun seçici, sayfa boyutu, sıralama | 🟡 sabit sütun | Kullanıcı bazlı sütun | P1 |
| Toplu işlemler (toplu güncelle, sil, sahip değiştir, etiket) | ❌ | Toplu seçim ve işlem çubuğu | P0 |
| Etiketler | ❌ | Etiket ekle/filtrele | P1 |
| İçe / dışa aktarma (CSV/XLSX) | ❌ | Eşleme sihirbazı, hata raporu | P0 |
| Kanban (fırsat dışındaki modüller) | 🟡 yalnız fırsat | Potansiyel, talep vb. | P2 |
| Satır göstergeleri (gecikmiş aktivite, not) | ❌ | Rozetler | P2 |

### Formlar ve alan zenginliği
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Sayfa tasarımı, çoklu düzen | 🟡 M8D özel alanlar (yolda) | Düzen bölümleri M8D ile kapanır | — |
| "Kaydet ve Yeni" | ❌ | Tüm formlarda | P1 |
| Arama penceresi (lookup) + pencerede hızlı oluştur | 🟡 seçici var | Tablo görünümlü arama penceresi, "Yeni ekle" | P1 |
| Adres kopyala, iki adres bloğu | ❌ (tek adres) | Fatura/teslimat ve posta/diğer adres; kopyala | P0 |
| Kişi standart alanları (doğum tarihi, asistan, Skype, Twitter, ikincil e-posta, raporlama yapılan kişi, e-posta gönderilmesin) | ❌ | Standart alan paritesi | P1 |
| Fırsat alanları (tip, sonraki adım, beklenen ciro, potansiyel kaynağı, kampanya kaynağı, kayıp nedeni) | 🟡 | Eksik alanlar, olasılığın aşamadan gelmesi zaten var | P0 |
| Kampanya (gönderilme sayısı, beklenen tepki, tip listesi) | 🟡 | Eksik alanlar, tip değerleri | P1 |
| Potansiyel: dönüştürme eşlemesi, kaynak, sektör, derece | 🟡 | Sektör, dönüştürme alan eşlemesi | P1 |

### Aktiviteler
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Görev tekrarı ve anımsatıcı | ❌ | Tekrar kuralı, anımsatıcı (M8A zamanlayıcıları ile) | P0 |
| Toplantı: katılımcılar/davetliler, konum, tüm gün, sağlayıcı bağlantısı | 🟡 tek "meeting" türü | Katılımcı, konum, tüm gün, toplantı bağlantısı | P1 |
| Arama: tip (gelen/giden), süre, sonuç, ajanda | 🟡 | Süre, sonuç, yön, ajanda | P1 |
| Ayrı Görevler/Toplantılar/Aramalar sayfaları | 🟡 tek liste | Tür bazlı sayfa ve sütunlar | P1 |
| İlgili kayıt olarak kişi/firma/fırsat/potansiyel | ✅ | — | — |
| Giriş (check-in) | ❌ | Kapsam dışı (mobil olmadığı için) | Yok |

### Envanter ve satış belgeleri
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Ürünler | ✅ | Kategori, üretici, stok alanları yok | P2 |
| **Fiyat Listeleri** (fiyatlandırma modeli, liste bazlı ürün fiyatı) | ❌ | Yeni modül | P1 |
| Teklif | 🟡 | Fatura/teslimat adresi, nakliye, yuvarlama, şartlar ve koşullar, teklif aşaması adları | P0 |
| Satış siparişi | 🟡 | Satın alma emri no, gider vergisi, komisyon, nakliye, adresler, şartlar, teklif ve fırsat bağlantısı | P0 |
| **Fatura** | ❌ | Yeni modül (siparişten üretme) | P0 |
| **Satın alma emri** ve **Tedarikçi** | ❌ | Yeni modüller | P1 |
| Teklif → sipariş → fatura zinciri | 🟡 teklif→sipariş var | Sipariş→fatura | P0 |

### Destek
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Servis dosyaları (talepler) | ✅ | — | — |
| **Çözümler** (bilgi tabanı: soru/cevap, durum, ürün) | ❌ | Yeni modül, talebe çözüm bağlama | P1 |
| Belgeler (klasörlü belge alanı) | 🟡 M8C dosya ekleri | Klasör ağacı ve paylaşım (kayıttan bağımsız belgeler) | P2 |

### Raporlar ve analitik
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Rapor kütüphanesi (klasör, favori, arama, oluşturan) | 🟡 5 sabit rapor | Rapor oluşturucu (modül, alan, filtre, gruplama, grafik), klasörler, favoriler, dışa aktarma, zamanlanmış e-posta | P0 |
| Hazır rapor kataloğu (satış döngüsü, dönüşüm oranı kaynak/sektör/sahip, fatura/sipariş/teklif durum raporları) | 🟡 | ~20 hazır rapor | P1 |
| Analitik panolar (bileşen ekle, pano oluştur) | ❌ | Pano oluşturucu; KPI, grafik, huni, hedef ölçer, karşılaştırıcı bileşenleri | P1 |
| Hedefler (aylık potansiyel, yıllık ciro) | ❌ | Hedef tanımı (kullanıcı/takım/dönem) ve hedef ölçer | P1 |
| Öngörüler (forecast) | ❌ | Dönemsel tahmin ve kota | P1 |
| Anomali, kohort, bölge, çeyrek daire | ❌ | Kapsam dışı (ilk sürüm) | P2 |

### Ayarlar, otomasyon, güvenlik
| Zoho | Bizde | Boşluk | Ö |
|---|---|---|---|
| Şirket ayarları: mali yıl, çalışma saatleri, tatiller, **para birimleri**, saat dilimi | 🟡 saat dilimi | Çalışma saatleri ve tatil (SLA için), çoklu para birimi ve kur | P1 |
| Modül aç/kapa | 🟡 plan bayrakları (platform tarafı) | Kiracı yöneticisinin modül görünürlüğü | P2 |
| İş akışı kuralları (tetik, koşul, işlem: alan güncelle, e-posta, görev, webhook) | 🟡 iki hazır kural türü | Genel kural motoru | P1 |
| Atama kuralları | 🟡 lead atama round-robin | Kural bazlı atama (bölge/kaynak) | P1 |
| **Webformları** (web'den potansiyel toplama) | ❌ | Halka açık form, spam koruması, atama | P0 |
| **Puanlama kuralları** | ❌ | Potansiyel puanı | P2 |
| Cadence (satış dizisi) | ❌ | Kapsam dışı | P2 |
| Şema / Blueprint (kılavuzlu aşama geçişi, zorunlu alanlar) | ❌ | Aşama geçiş kuralları | P1 |
| Profiller, **roller ve paylaşım (hiyerarşi, kayıt görünürlüğü)** | 🟡 yalnız rol/izin | Rol hiyerarşisi ve kayıt sahipliği görünürlüğü, alan düzeyi izin | P0 |
| Bölge yönetimi | ❌ | P2 | P2 |
| Oturum açma tarihçesi, güvenilir IP/alan, SAML/AD | ❌ | Giriş geçmişi P1; SAML/AD P2 | P1/P2 |
| Kanallar: e-posta, telefon, SMS, sohbet, portallar | 🟡 M8A bildirim e-postası | E-posta gelen kutusu (Salesinbox) ve telefon (tıkla-ara) yok | P2 |
| Şablonlar (e-posta, yazdırma) | ❌ | E-posta ve belge şablonu | P1 |
| Çeviriler (etiket adları) | ❌ | Kiracı bazlı etiket özelleştirme | P2 |

## 3. Yapılmayacaklar (bilinçli)
Salesinbox, Sosyal, Ziyaretler, Hizmetler, Projects, Kiosk Studio, Canvas, Sihirbazlar (ayrı ürün alanları), Zoho Finance/WorkDrive tanıtımları, mobil check-in, anomali/kohort/bölge/çeyrek daire analitik bileşenleri, SAML/AD (pilot sonrası).

## 4. Önerilen yol haritası (M9)
Her kart bugünkü ekip düzeniyle yürür: Spec (plan + kontrat) → Backend + Web (ayrı worktree) → merge → doğrulama. Aynı anda en çok 8 ajan.

| Kart | Kapsam | Ö | Bağımlılık |
|---|---|---|---|
| **M9A Kabuk ve iş kuyruğu** | Gruplu menü, genel arama, "+" hızlı oluştur, İş Kuyruğu (kayıtlı kuyruklar, sayaç, gecikme), yeni Ana Sayfa widget'ları (görevlerim, toplantılarım, bugünün potansiyelleri, bu ay kapanan fırsatlar, dokunulmamış fırsatlar) | P0 | — |
| **M9B Liste deneyimi** | Kayıtlı görünümler, gelişmiş filtre paneli, sütun seçici, toplu işlemler (güncelle/sil/sahip/etiket), etiketler, CSV/XLSX içe-dışa aktarma sihirbazı, "dokunulmamış" mantığı | P0 | M8D (özel alan filtreleri) |
| **M9C Satış belgeleri ve envanter** | Ortak kalem bileşeni + adres blokları/kopyala/nakliye/yuvarlama/şartlar, teklif ve sipariş alan paritesi, **Fatura**, **Fiyat Listesi**, **Tedarikçi**, **Satın Alma Emri**, teklif→sipariş→fatura zinciri, arama penceresi + hızlı oluştur | P0/P1 | M8D |
| **M9D Aktivite paritesi** | Görev tekrarı ve anımsatıcı, toplantı (katılımcı, konum, tüm gün), arama (yön, süre, sonuç), tür bazlı sayfalar, takvim görünümü | P0/P1 | M8A (zamanlayıcı ve bildirim) |
| **M9E Alan ve form paritesi** | Kişi/potansiyel/firma/fırsat/kampanya standart alanları, "Kaydet ve Yeni", potansiyel dönüştürme eşlemesi, kampanya tip listesi | P0/P1 | M8D |
| **M9F Rapor ve analitik** | Rapor oluşturucu + klasör/favori/dışa aktarma/zamanlama, ~20 hazır rapor, pano oluşturucu (KPI, grafik, huni, hedef ölçer), **hedefler**, **öngörü** | P0/P1 | M9B (filtre motoru) |
| **M9G Otomasyon ve kanallar** | Webformları, kural bazlı atama, genel iş akışı kural motoru (alan güncelle, görev, bildirim, webhook), şema/blueprint geçiş kuralları, puanlama (P2) | P0/P1 | M8A, M8B |
| **M9H Erişim modeli** | Rol hiyerarşisi + kayıt sahipliği görünürlüğü + paylaşım kuralları, alan düzeyi izin, giriş geçmişi, güvenilir IP | P0 | M8D |
| **M9I Destek ve belgeler** | Çözümler (bilgi tabanı) ve talebe bağlama, belge klasörleri ve paylaşım | P1/P2 | M8C |
| **M9J Şirket ayarları** | Çalışma saatleri ve tatiller (SLA'ya bağlanır), çoklu para birimi ve kur, mali yıl, e-posta/yazdırma şablonları | P1 | M9C |

**Sıra önerisi:** M9A ve M9C hemen başlayabilir (M8 ile çakışma az). M9B, M9E ve M9H özel alanlar (M8D) merge edilince. M9D, M8A merge edilince. M9F, M9B'den sonra. M9G, M8A ve M8B'den sonra.

## 5. Ek notlar
- **Çoklu para birimi:** Şu an raporlar farklı para birimlerini toplar; M9J ile kur tablosu gelmeden fatura/sipariş toplamları tek para birimiyle sınırlı kalır.
- **Kayıt görünürlüğü (M9H):** Zoho'da "yalnızca kendi kayıtlarım" temel bir beklentidir; bizde her `crm.*.read` sahibi tüm kayıtları görüyor. Grup şirketleri için pilot öncesi kapatılması önerilir.
- **Ekran görüntüleri:** Kaynak görseller repoya eklenmedi.
