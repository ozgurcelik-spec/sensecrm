# Ekran eksik analizi (kıdemli CRM + UI/UX gözüyle)

Tarih: 2026-09-20. Kapsam: `web/src` formları/listeleri/detayları/ortak bileşenleri + `src/Modules` domain modeli.
Yöntem: **yalnız kod okuma** (7 paralel salt-okunur inceleme + 134 metin girişinin tek tek sınıflandırılması). Uygulama çalıştırılıp ekranlar gezilmedi; "doğrulanmadı" işaretli maddeler çalışır uygulamada teyit edilmeli.
`zoho-ekran-analizi.md`'de zaten kayıtlı bulgular tekrarlanmadı (yalnız yeni ayrıntı eklendi).

Öncelik: **P0** = veri bütünlüğü / yasal risk / kırık akış, **P1** = üründe beklenen ana eksik, **P2** = olgunluk, **P3** = cila.

---

## 1. Ana tespit: backend web'in önünde

Ticari modülde backend'de olup web'e hiç bağlanmamış olanlar (`zoho-ekran-analizi.md` bunları hâlâ "yeni modül" diye gösteriyor; backend için bu artık doğru değil):

| Özellik | Backend | Web |
|---|---|---|
| Fatura (`POST /orders/{id}/invoice`), fiyat listesi, tedarikçi, satın alma emri | var | yok |
| Teklif durumu `negotiation` + `/negotiate` | var | durum listesinde, rozette, eylemde yok. Bu durumdaki teklif etiketsiz/eylemsiz kalır (P0) |
| Belgede adres blokları, nakliye (`carrier`), yuvarlama (`adjustment`), fiyat listesi | var | editörde yok (P0) |
| Sipariş: müşteri SAS no, vade tarihi, gider vergisi, komisyon | var | form yok (P0) |
| Ürün: `vendorId`, `purchasePrice` | var | form yok (P2) |
| Listelerde kişi/fırsat/tarih aralığı/para birimi filtreleri | var | UI kullanmıyor (P2) |
| Denetim günlüğü `entityType`/`entityId` filtresi | var | UI'da yok (P1) |

---

## 2. "Textbox olan ama combo/lookup olması gereken" alanlar

Envanter: test dışı `.tsx` dosyalarında **134 metin girişi** (93 TextInput, 18 NumberInput, 18 Textarea, 5 PasswordInput) tek tek sınıflandırıldı: **88 doğru, 27 yanlış kontrol, 19 eksik doğrulama**. Yanlış kontrollerin 26'sı tarih alanı (22 `type="date"` + 4 `datetime-local`), biri sektör. Filtre, arama, entegrasyon, webhook, API key, workflow, SLA ve pipeline formlarında yanlış kontrol **bulunmadı** (Select/MultiSelect/Switch zaten kullanılıyor). E-posta alanlarının hepsi `type="email"` + zod'lu, URL alanları doğrulamalı, parola alanları doğru.

| Ekran | Alan | Şimdi | Olmalı | Ö |
|---|---|---|---|---|
| Hesap, Kişi (`address-fields.tsx:30-33`) | Ülke, il, şehir/ilçe | Serbest TextInput | Ülke aranabilir Select (ISO); TR seçilince il→ilçe bağımlı Select. "TR / Türkiye / Turkey" raporu bozar | P1 |
| Hesap, Kişi (`address-fields.tsx:32`) | Posta kodu | Serbest TextInput | `inputMode=numeric`, 5 hane maskesi, `regex(/^\d{5}$/)`, ülkeye bağlı | P2 |
| Hesap, Kişi (`address-fields.tsx:29`) | Sokak/adres | Tek satır TextInput | Autosize Textarea | P3 |
| Hesap (`account-form-dialog.tsx:137`) | Sektör | Serbest TextInput; liste filtresi ikinci bir arama kutusu (alt-string eşleşmesi) | Yönetilebilir seçim listesi; filtre Select, kesin eşleşme | P1 |
| Fırsat (`lost-reason-dialog.tsx:31`) | Kayıp nedeni | Serbest Textarea | Seçim listesi (Fiyat, Rakip, Bütçe yok…) + isteğe bağlı not. Kayıp raporu şu an anlamsız | P0 |
| Aday | Kaynak | Sabit 5'li enum (`Lead.cs:7`) | Kiracı yönetilebilir liste + kaynak detayı | P1 |
| Aday | "Uygun değil" nedeni | Alan yok | Zorunlu seçim listesi | P2 |
| Kişi (`contact-form-dialog.tsx:150`) | Unvan / hitap | Serbest TextInput / yok | Unvan için Autocomplete (öneri + serbest), hitap Select | P2 |
| Ürün (`product-form-dialog.tsx:155`) | Birim | Serbest TextInput | Select (adet, kg, saat, lisans…) | P1 |
| Ürün, kalem (`product-form-dialog.tsx:194`, `line-items-grid.tsx:166`) | KDV oranı | NumberInput | Oran seçici (%0/1/10/20), serbest değer izinli | P1 |
| Fırsat, Ürün, Kampanya, Belge | Para birimi | UI'da sabit `TRY/USD/EUR/GBP` (`types/commerce.ts:13`), 3 yerde kopya; backend tüm ISO 4217 kabul eder | Tek kaynak `CURRENCIES`, kiracı para birimi listesi | P1 |
| Belge | Ödeme koşulu, teslimat yöntemi/`carrier` | Alan yok / serbest metin | Combobox (peşin, 30/60 gün) + vade otomatik hesap | P1 |
| Ürün (`product-form-dialog.tsx:150`) | Ürün kodu (SKU) | Serbest TextInput, yalnız `max(64)` | Karakter deseni + büyük harfe normalize (backend kuralı doğrulanmadı) | P3 |
| Talep | Kategori, alt kategori, ürün | Alan yok (`Case.cs:37-76`) | Bağımlı Select + ürün lookup | P1 |
| Talep | Kanal | 4 sabit enum | Kiracı listesi (WhatsApp, sohbet, portal…) | P2 |
| Talep | Kök neden | Yok | Seçim listesi | P2 |
| Aktivite | Arama sonucu | Yok | Select (ulaşıldı/meşgul/mesaj bırakıldı/yanlış no) | P1 |
| Aktivite | Süre | Yok | Hazır süre seçimi (15/30/60), bitiş otomatik dolsun | P2 |
| Kampanya | Tür | 5 sabit enum | Kiracı listesi | P3 |
| Workflow (`rule-form-dialog.tsx`) | Atanacak hedef | Yalnız rol | Kullanıcı / takım / yönetici Select | P1 |
| Workflow | Kaynak modül, alan, operatör, değer | Sabit lead/deal | Modül→alan→operatör→değer zinciri (metadata'dan) | P1 |
| Workflow (`rule-form-dialog.tsx:224`) | En düşük tutar | NumberInput, para birimi göstergesi yok | Para birimi suffix; kural kapsamı belirsiz (doğrulanmadı) | P2 |
| Kurum / Kullanıcı | Ülke, para birimi, tarih/sayı formatı, departman, yönetici, unvan | Yok | Select'ler | P1 |
| Fırsat (`deal-form-dialog.tsx:97`), Belge editörü (`document-editor.tsx:172,176`) | Kişi / fırsat seçici | `pageSize: 100` sabit, arama yok | Sunucu aramalı generic `EntityPicker` (100+ kayıtta fazlası seçilemez) | P1 |
| Kısa listeler (`lead-form-dialog.tsx:189`, `activity-form-dialog.tsx:180-197`, `deal-form-dialog.tsx:257`) | Derece (sıcak/ılık/soğuk), öncelik, para birimi | 3-4 seçenekli açılır liste | SegmentedControl / Radio (tek tıkla seçim) | P2 |
| Organizasyon ayarı (`organization.tsx:28`) | Saat dilimi | Aranabilir ama ham IANA adı ("Europe/Istanbul") | "(UTC+03:00) İstanbul" etiketi | P2 |
| API anahtarı (`api-key-form-dialog.tsx:242`) | IP izin listesi (CIDR) | Satır satır Textarea (doğrulamalı) | TagsInput, etiket başına anlık hata (zorunlu değil) | P3 |
| Tarih girişleri (26 alan) | Tarih / tarih-saat | `TextInput type="date"/"datetime-local"`; `@mantine/dates` yok | DateInput/DateTimePicker, TR biçimi (gg.aa.yyyy, 24 saat), hızlı seçenekler ("yarın 09:00", "+30 gün"), tarih aralığı bileşeni | P2 |

### Sayı girişi (yeni ve yüksek etkili)
13 tutar/oran/adet alanında (`deal-form-dialog.tsx:245`, `lead-convert-dialog.tsx:210`, `campaign-form-dialog.tsx:240,257,274`, `product-form-dialog.tsx:165,194`, `line-items-grid.tsx:132-166`, `rule-form-dialog.tsx:224`) `decimalSeparator` verilmemiş (varsayılan `.`), tema düzeyinde `NumberInput` varsayılanı yok. Bazı alanlarda `thousandSeparator=" "`, bazılarında hiç ayraç yok; gösterim ise `tr-TR` (1.234,50 ₺). Kullanıcı virgüllü sayı yazınca sorun çıkabilir (tarayıcıda denenmedi, **doğrulanmadı**) ve ekranlar arası biçim tutarsız. Öneri: dile bağlı ortak `MoneyInput` / tema `defaultProps`.
Ek: `clampBehavior` tutarsız (deal: varsayılan `blur`, kampanya: `none`); `line-items-grid` alanlarında üst sınır yok; pipeline olasılığında gereksiz ondalık serbest (`pipelines.tsx:169`).

### Uzunluk sınırı eksikliği
Hesap, kişi, aday, fırsat, aktivite, rol, profil, organizasyon, pipeline formlarında (~25 alan) zod `.max()` ve `maxLength` yok; ticari, servis, kampanya, dosya, platform formlarında var. Sunucu sınırlarıyla eşlenmeli (sınırlar doğrulanmadı).

---

## 3. P0 (veri bütünlüğü, yasal risk, kırık akış)

1. **Tekrar kayıt (duplicate) kontrolü yok.** Hesap adı benzersiz değil (`Account.cs:9`), e-posta/telefon/vergi no denetimi yok. Lead dönüşümünde yalnız tam adlı firma önerilir, kişi hep yeni açılır (`LeadUseCases.cs:270`).
2. **Telefon:** ülke kodu, maske, E.164 normalizasyonu yok (`z.string()`); aynı numaranın farklı yazımı tekrar doğurur, arama da zayıflar.
3. **KVKK / İYS:** kişi kaydında rıza durumu-tarihi-kanalı yok; kayıt ekranında aydınlatma/rıza onayı yok; kiracı yöneticisi için kişisel veri silme/anonimleştirme, rıza kaydı, saklama politikası, ilgili kişi veri dışa aktarma yok (yalnız platform yöneticisi kurum silebiliyor).
4. **Parolamı unuttum akışı yok** (web'de yok, API'de reset ucu görünmüyor).
5. **Vergi no / vergi dairesi** hesap modelinde yok (fatura çıkarılamaz); VKN algoritmik doğrulaması gerekir.
6. **Fırsat kayıp nedeni serbest metin.**
7. **Ticari web-backend boşluğu** (bölüm 1).
8. **Listelerde filtre/toplu işlem:** Hesap/Fırsat listesinde satır seçimi yok; Kişi/Aday'da seçim yalnız "kampanyaya ekle". Filtre dar (derece, tarih aralığı, il, tutar aralığı, boş alan yok).

---

## 4. P1 eksikler (alana göre)

**Hesap / Kişi / Aday / Fırsat**
- Birden çok telefon/e-posta (firma ve aday için); detayda `mailto:`/`tel:` yok.
- Hesap: çalışan sayısı, yıllık ciro, üst hesap (hiyerarşi), firma tipi, sosyal bağlantı.
- Aday: unvan, adres, açıklama modelde yok (dönüşümde firma sektörü/adresi/web'i boş kalıyor).
- Fırsat: rakip, kazanma nedeni, beklenen ciro (tutar×olasılık) ve filtreli liste toplamı; aşama geçmişi, "aşamada kaç gün".
- Hızlı arama kapsamı dar: telefon, vergi no yok; telefon aramasında boşluk/tire normalizasyonu yok.
- Detaylarda tek birleşik zaman çizelgesi yok (aktivite, not, aşama değişimi, denetim ayrı sekmelerde).

**Ticari**
- Vergi: ÖTV, tevkifat, damga vergisi yok (yalnız KDV); karar ürün sahibinde.
- Teklif: PDF/e-posta gönderimi, revizyon numarası, indirim eşiğinde onay yok ("Gönder" yalnız durum değiştirir).
- Ürün seçici yalnız ilk 20 sonuç; `LookupDialog` (tablo görünümü, yeni ürün, fiyat listesinden fiyat çözümü) yok.
- Toplam önizlemede yuvarlama yok.

**Servis / Pazarlama / Aktivite / Rapor**
- SLA: iş takvimi ve resmi tatil (dini bayram/arife dahil) yok; `pending` durumunda sayaç durmuyor (`Case.cs:186`); ihlal/risk bildirimi-escalation yok; uyarı eşiği %80 sabit (`CaseSlaCalculator.cs:22`).
- Aktivite yalnız hesap/kişi/aday/fırsata bağlanır; talep, teklif, sipariş, kampanya bağlanamaz (talep detayında aktivite sekmesi bu yüzden yok). Toplantı linki/konum alanı yok.
- Kampanya: segmentten toplu üye ekleme yok, hesap üye olamıyor; ROI/gerçekleşen gelir ve fırsat-kampanya bağı yok.
- Raporlar: servis/pazarlama raporlarında sahip, kanal, öncelik, ürün filtresi yok; kayıtlı ve zamanlanmış rapor yok.

**Yönetim / Platform**
- Kurum ayarları yalnız ad/dil/saat dilimi: logo, vergi no/dairesi, adres, telefon, web, varsayılan para birimi, tarih/sayı formatı, **numaralandırma formatı** (teklif/sipariş/fatura/talep no) yok.
- Özel alan (custom field), seçim listesi (picklist) yöneticisi, sayfa düzeni, kayıt tipleri, tekrar kuralı yönetimi yok. Bölüm 2'deki birçok combo düzeltmesi bu altyapıya bağlı.
- Kullanıcı: departman/takım/yönetici/unvan yok; devre dışı bırakırken kayıt devri yok; davet yönetimi (yeniden gönder/iptal) yok, yeni üyeye geçici parola veriliyor (`add-member-dialog.tsx:57`).
- Roller: düz checkbox listesi (modül×işlem matrisi yok); rol klonlama yok.
- 2FA ve oturum listesi ("tüm oturumları kapat") yok.
- Pipeline: aşama başına zorunlu alan/geçiş kuralı, kayıp/kazanç nedeni listesi yok.
- Workflow yalnız 2 sabit tür (`LeadAssignment`, `DealApproval`): koşul oluşturucu, alan güncelle/görev/e-posta/webhook eylemleri yok.
- Denetim günlüğü: kullanıcı/işlem/tarih/varlık filtresi ve CSV yok.
- E-posta şablonu/imzası ve sağlayıcı seçimi yok.

**Ortak altyapı**
- Generic `EntityPicker` yok: seçici kalıbı en az 3 kez kopyalanmış (`account-picker`, `contact-picker`, `related-record-picker`) + `product-picker`.
- Global arama / Ctrl+K yok.
- Kirli form uyarısı yok (Esc/dış tık formu sessizce kapatır).
- Kolon seçici, kayıtlı görünüm, çoklu sıralama yok; liste sayfalarından dışa aktarma yok (`lib/csv.ts` iyi ama yalnız rapor/platformda kullanılıyor).

---

## 5. Bileşen ve UI/UX değerlendirmesi

Kaynak: ortak bileşenler (`FormDialog`, `DataTable`, pickers, `document-editor`, `line-items-grid`, kanban, shell, auth) ve form/liste/detay kalıpları. Çalışma zamanı davranışı doğrulanmadı.

| Alan | Bulgu | Kanıt | Öneri | Etki | Efor |
|---|---|---|---|---|---|
| Kontrast | Marka rengi `#008cf0` üstünde beyaz metin ≈3,5:1 (AA altı, 14px buton, aktif menü); `c="dimmed"` 12px ≈3,4:1 (hesaplama) | `color-palettes.ts:22`, `mantine-theme.ts:6-16`, `app-layout.module.css:37-41`, `record-detail-shell.tsx:41` | `primaryShade` 7 veya `autoContrast`; dimmed için koyu ton | Yüksek | S |
| Detay eylemleri | 5 eylemin hepsi `default` varyant, birincil yok; "Sil" `default`+kırmızı (gri görünebilir, doğrulanmadı); Sil, Düzenle'nin yanında; breadcrumb yok | `account-detail.tsx:60-91`, `record-detail-shell.tsx:99-104` | Düzenle birincil; Sil ve ikincil eylemler "⋯" menüsünde; breadcrumb | Yüksek | M |
| Boş durum | Ana CRM listeleri `emptyMessage` vermiyor; hep "Kayıt bulunamadı"; filtreli/ilk kullanım ayrımı ve eylem çağrısı yok | `data-table.tsx:247-255`, `accounts.tsx:118-133` | Ortak `EmptyState`: filtreliyse "Filtreleri temizle", boşsa "İlk müşteriyi ekle" | Yüksek | M |
| Sayı girişi | Bölüm 2'deki ayraç sorunu | `deal-form-dialog.tsx:250`, `line-items-grid.tsx:132-175`, `format.ts:13-17` | Ortak `MoneyInput` | Yüksek | M |
| Doğrulama | Hata metni alan adsız ("Bu alan zorunludur"); doğrulama yalnız submit'te; "* zorunlu" açıklaması yok | `auth.json:21`, `account-form-dialog.tsx:80-92` | `mode:"onTouched"`, alan adlı mesaj | Orta | M |
| Hata alanına odak | Select/picker `Controller`'ları `field.ref` geçirmiyor; hatalı ilk alan bunlardansa odaklanamaz (müşteri zorunlu alanı dahil) | `deal-form-dialog.tsx:170-186`, `document-editor.tsx:318-335` | `ref={field.ref}` veya `onInvalid`'de `setFocus` | Orta | S |
| İlk alana odak | Lead/kişi formunda odak isteğe bağlı "Ad"a gidiyor, zorunlu alan "Soyad" | `lead-form-dialog.tsx:125,131`, `contact-form-dialog.tsx:134,140` | `data-autofocus` zorunlu alana | Düşük | S |
| Enter ile gönderme | Belge editörü tek `<form>`: satır açıklamasında Enter tüm teklifi kaydeder | `document-editor.tsx:284`, `line-items-grid.tsx:122-129` | Satır girdilerinde Enter'ı yut / sonraki hücreye geç | Orta | S |
| Modal | Kayıt sürerken Esc/dış tık modalı kapatabiliyor; hesap formu ≈14 alan `lg` modalda kayıyor; belge editöründe yapışkan kaydet çubuğu yok | `form-dialog.tsx:24-46`, `document-editor.tsx:468-479` | `closeOnEscape`/`closeOnClickOutside` = `!loading`; sticky alt çubuk | Orta | S |
| Bağımlı alanlar | Müşteri değişince kişi/fırsat sessizce sıfırlanıyor (bilgi yok); `AccountPicker` `allowDeselect={false}` almıyor, zorunlu müşteri tıklayınca temizlenebilir (Mantine varsayılanı, doğrulanmadı) | `deal-form-dialog.tsx:96-99,176-180`, `account-picker.tsx:324-339` | Sıfırlamada bilgi metni; `allowDeselect={false}` | Orta | M |
| Tablo | `stickyHeader` yok; hücrelerde `truncate`+Tooltip yok; yenilemede yalnız opaklık 0,6; e-posta/telefon düz metin (`mailto:`/`tel:` hiçbir yerde yok) | `data-table.tsx:167-243`, `accounts.tsx:51-52` | Sticky başlık, truncate+Tooltip, bağlantılar | Orta | M |
| Filtre çubuğu | "Sektör" filtresi ana aramayla aynı görünen ikinci kutu; aktif filtre çipi/sayacı yok; `MultiSelect` seçildikçe satırı büyütüyor | `accounts.tsx:110-114`, `campaigns.tsx:113-131`, `list-page-frame.tsx:445-455` | Sektör Select, filtre çipleri + sayaç, `maxValues`/"+N" | Orta | M |
| Belge satır gridi | `minWidth=1040`: 1280px ekranda ≈976px alan, yatay kaydırma (hesaplama); satır silmede onay/geri alma yok | `line-items-grid.tsx:86,205-213` | Sütunları daralt/birleştir; "Geri al" toast'ı | Orta | M |
| Silme onayı | Mesaj geri alınamazlığı belirtmiyor; `has_dependents` hatası toast olarak çıkıp dialog kapanıyor | `confirm-dialog.tsx:518-528`, `accounts.tsx:81-84` | Hata dialog içinde, bağımlı sayısıyla | Düşük | S |
| Başarı toast'ı | Başlık/ikon yok, yalnız yeşil renk (hata toast'ında başlık var) | `use-toast.ts:26-33` | ✓ ikonu + başlık | Düşük | S |
| Tutarlılık | Auth ekranı tema/koyu modu yok sayıyor (sabit renk); marka "Biriksin" vs "Sense CRM"; odak halkası %12 opaklık; kanban tutamağı ≈22px; dropzone sabit renk | `auth-layout.css:1-40`, `auth-layout.tsx:20,63`, `common.json:3`, `deal-board.tsx:157-166` | Tema değişkenleri, tek marka adı, ≥24px hedef, belirgin `:focus-visible` | Orta | M |
| Terminoloji | Aynı kavram: "Müşteri" (picker/kişi/fırsat) vs "Şirket" (aday); adres etiketleri "İl-İlçe / Bölge / Ülke" belirsiz; `ThemeSwitcher`'da sabit TR `defaultValue` | `crm.json:9,14-18`, `theme-switcher.tsx:15` | Sözlük; adres: Sokak/Cadde, İl, İlçe, Posta kodu, Ülke | Orta | S |
| Üst çubuk | 3 zil + organizasyon değiştirici (`maw 260`) + dil + kullanıcı menüsü `nowrap`; dar ekranda taşma olasılığı (doğrulanmadı) | `app-layout.tsx:106-113` | Küçük ekranda tek "⋯" menü | Orta | M |

**Olumlu (korunmalı):** liste durumu URL'de (`use-list-params.ts`), tablo `aria-sort`/`aria-label`, renk+metin rozetler (renk körlüğüne uygun), kanban'da klavye sensörü + ekran okuyucu duyuruları + "taşı" menüsü, kayıp nedeni akışı, ikon-yalnız `ActionIcon`'larda `aria-label` (dosya başına sayımla, tek tek doğrulanmadı), yanıt kutusunda Ctrl+Enter ipucu, butonlarda `loading` + çift tık koruması, `route-boundary`/`load-error`/`permission-guard`.

**Diğer ortak durum:** i18n TR/EN anahtar sayısı eşit (21 namespace); sabit kodlanmış metin az (`auth-layout.tsx:20,31,36`, `webhook-form-dialog.tsx:137`); koyu mod var; mobilde tablo yalnız yatay kaydırma (kart düzeni yok); ham `<Table>` kullanan 26 dosya ortak `DataTable`'a geçmeli.

---

## 6. P2 / P3 özet

- Ortak `MoneyInput`, telefon girişi (ülke kodlu), tarih/tarih aralığı bileşenleri yok (`@mantine/dates` bağımlılığı yok).
- Etiket (tag) alanı/filtre/toplu etiketleme yok; zengin metin yok; geri al (undo) yok; klavye kısayolu yok; otomatik taslak yok.
- Ana sayfa widget'ları sabit (gizle/sırala yok), rapor aralığı sabit "son 6 ay".
- Lead detay "Genel" sekmesinde şirket adı tekrarı; kişi detayında teklif/sipariş yok; hesap detayında kampanya sekmesi yok.
- Talep: ilk yanıt süresi listede/detayda gösterilmiyor; CSAT yok; çözüm kodu/bilgi tabanı bağlantısı yok.
- Web sitesi alanı `firma.com` gibi protokolsüz girişi reddediyor (otomatik `https://` eklenmeli).

---

## 7. Önerilen sıra

1. **Ortak altyapı (combo düzeltmelerinin önkoşulu):** picklist/seçim listesi yöneticisi + özel alan altyapısı + generic `EntityPicker` + `MoneyInput` + telefon/tarih/adres bileşenleri (`@mantine/dates`, tr-TR) + tek `CURRENCIES` + ülke/il/ilçe verisi.
2. **Hızlı UX kazanımları (S efor):** kontrast (`primaryShade`/`autoContrast`), detay eylem hiyerarşisi, `EmptyState`, form doğrulama (`onTouched`, `field.ref`, autofocus, kayıt sırasında modal kilidi), Enter davranışı, terminoloji sözlüğü.
3. **Veri kalitesi:** tekrar kayıt kontrolü + lead dönüşümünde kişi eşleştirme, telefon normalizasyonu, VKN/vergi dairesi, kayıp nedeni listesi, uzunluk sınırları.
4. **Ticari web boşluğunu kapat:** müzakere, sipariş alanları, adres/nakliye/yuvarlama, fatura, fiyat listesi.
5. **KVKK/İYS ve parola sıfırlama.**
6. **Liste olgunluğu:** filtre, toplu işlem, dışa/içe aktarma, kolon seçici/kayıtlı görünüm, sticky başlık, global arama.
7. **SLA (iş takvimi, tatil, duraklatma, escalation)**, aktivite bağlama kapsamı, kampanya segment/ROI, workflow builder.

## Doğrulanmadı (çalıştırılıp teyit edilmeli)
- Tüm bulgular statik koddan çıkarıldı; ekran görüntüsüyle teyit edilmedi. Özellikle: virgüllü sayı girişinin reddedilmesi, kırmızı "Sil" düğmesinin gerçek rengi, dar ekranda üst çubuk taşması, `allowDeselect` davranışı.
- İçe aktarma (import) akışı, kayıt sayfalarında özel alan desteği, SLA ihlali bildirimi (Notifications modülü), zamanlanmış workflow (Conductor), backend tekrar kaydı için veritabanı benzersiz indeksi (migration'lar okunmadı).
- Backend alan sınırları (posta kodu, SKU, olasılık, uzunluklar), `pageSize: 100` için API üst sınırı, "sistem" tema seçeneğinin arayüzde görünürlüğü, workflow para birimi kapsamı.
