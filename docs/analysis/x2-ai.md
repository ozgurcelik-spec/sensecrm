# C-X2 — AI analiz worker'ı: analiz ve karar önerisi

Kart: `C-X2` (analiz; kod yok) · Tarih: 2026-09-20 · Kapsam: kullanım senaryoları, mimari seçenekler, güvenlik/KVKK, yol haritası, riskler, ürün sahibi kararları.
Bağlam: K13/K17 (veri merkezinden çıkış yok), K16 (AI/MCP baştan alınmadı), M8B egress modeli ([m8b-entegrasyonlar](../plan/m8b-entegrasyonlar.md)), M9F D15 (opak ML yok, [m9f-analitik](../plan/m9f-analitik.md)), M9A pg_trgm arama ([m9a-kabuk](../plan/m9a-kabuk.md)), M9E yinelenen kontrolü, M9G kural motoru ([m9g-otomasyon](../plan/m9g-otomasyon.md)), M9H kayıt kapsamı/alan izni ([m9h-erisim](../plan/m9h-erisim.md)). M9I (bilgi tabanı) için plan belgesi **henüz yok**; ilgili satırlar "M9I planı sonrası" diye işaretlidir.
Dış olgular köşeli kodlarla `[S#]` Ek A'ya bağlıdır. Doğrulama işaretleri: **[D]** birincil sayfa bu oturumda okundu, **[Ö]** ikincil özet/arama sonucu, **[?]** doğrulanmadı (bilgi/çıkarım). **Bu belgede hiçbir model bu ortamda çalıştırılmadı; performans/donanım sayıları ölçüm değil, hesap veya kaynak iddiasıdır.**

## 0. Özet (karar önerisi)

1. **AI'nın değerinin büyük kısmı kural/istatistikle alınabilir** (fırsat riski, lead skor kartı, Türkçe arama, yinelenen skoru). Bunlar GPU'suz, KVKK açısından risksizdir ve **Faz 0** olarak hemen başlar.
2. **Üretken (LLM) özellikler** (özet, yanıt taslağı, sınıflandırma önerisi, doğal dil rapor kurucu) için **öneri: `IAiProvider` portu + yalnız Worker'ın (`ai` rolü) konuştuğu, `internal` ağda, çıkışsız, kendi veri merkezimizde çalışan çıkarım sidecar'ı (seçenek B)**. Dış API (C) portun **ikinci adaptörü olarak tasarlanır ama uygulanmaz**; kiracı bazlı, varsayılan kapalı, hukuki inceleme sonrası (seçenek D'nin sıralı hâli).
3. **Kesin tasarım kuralları:** bağlam **API'de, isteyen kullanıcının kimliğiyle** (M9H kapsamı + alan izni) toplanır, Worker yalnız donmuş yükü modele iletir; model **araç/ağ/HTML çıktısı** alamaz; hiçbir AI çıktısı insan onayı olmadan eylem ya da M9G kuralı tetiklemez; varsayılan **kapalı**, kiracı yöneticisi açar, plan limitli.
4. **GPU yoksa LLM yoktur:** ≥12–14B sınıfı model gerekir (küçük modeller Türkçede belirgin zayıf, [S3]); CPU yalnız gömme (embedding) ve toplu işlere yeter. **Faz 1 öncesi 2 haftalık spike zorunlu** (GPU envanteri + Türkçe CRM metni benchmark'ı).
5. Toplam iş: Faz 0 ≈ 6 mühendis-haftası (MH), Faz 1 (spike + altyapı + arama + özet/taslak) ≈ 16,5 MH, Faz 2 ek özellikler ≈ 11 MH; ayrıntı §4.

## 1. Kullanım senaryoları (değer × risk)

Teknik sütunu: **K** kural/istatistik, **M** klasik ML veya gömme (embedding) modeli, **L** LLM (üretken). Değer/Risk 1–5 (yargı; ölçüm değil). Risk = yanlış çıktının zararı + veri sızıntısı/profilleme/enjeksiyon yüzeyi.

| # | Senaryo | Teknik | Değer | Risk | Not / gerekçe | Faz |
|---|---|---|---|---|---|---|
| 1 | **Fırsat risk bayrakları** (durgun, kapanış tarihi geçmiş, aşamada bekleme, tutar/aşama tutarsızlığı) | K | 4 | 1 | M9A `last_activity_at` + M9F durgun fırsat/öngörü zaten var; her bayrak "neden" ile gösterilir | 0 |
| 2 | **Lead skor kartı** (kaynak, etkileşim, alan doluluğu; kiracı ağırlıklı, şeffaf) | K | 4 | 2 | M9F D15 ML'i reddetti; koşullar sağlanmadıkça skor kuraldır. Kişiler üzerinde skor = profilleme (KVKK m.11/1-g, §3.8) | 0 |
| 3 | **Yinelenen tespiti** | K (+trigram `similarity()`) | 3 | 1 | M9E `crm_dupkey` deterministik; ad benzerliği için `pg_trgm` yeterli, LLM/gömme kazancı kanıtsız | 0 |
| 4 | **Veri kalitesi** (eksik/tutarsız alan panoları, sektör/ülke normalizasyon önerisi) | K, öneri için L | 3 | 2 | Panolar kural; normalizasyon önerisi insan onaylı | 0 (kural) / 2 (L) |
| 5 | **Anlamsal + Türkçe arama** (M9A aramasına hibrit: trigram + gömme, sıralama birleştirme) | M | 4 | 2 | Gömme modeli LLM değildir, CPU'da çalışır; asıl risk **izin oracle'ı** (§3.2) | 1 |
| 6 | **Talep / hesap zaman çizelgesi özeti** | L | 5 | 3 | En yüksek değer: uzun talep/aktivite geçmişi. Müşteri metni **enjeksiyon vektörü** (§3.3); çıktı yalnız gösterilir | 1 |
| 7 | **Yanıt taslağı** (talep yanıtı, e-posta) | L | 4 | 3 | Taslak düzenleyiciye düşer, **gönderim kullanıcı eylemidir** (M8A hattı). Şablonlar (M9J) ile birlikte düşünülmeli | 1 |
| 8 | **Talep sınıflandırma/yönlendirme önerisi** (kategori, öncelik, ekip) | M (gömme+kNN/küçük sınıflandırıcı), L yedek | 4 | 2 | Yalnız **öneri**; otomatik yönlendirme M9G kuralıyla ve **insan tanımlı eşlemeyle** | 1 |
| 9 | **Doğal dil rapor kurucu** ("bu çeyrek kaybedilen fırsatlar nedenine göre") | L → M9F `ReportDefinition` v1 JSON | 3 | 2 | Çıktı **kapalı katalog + mevcut doğrulayıcıdan** geçer, kullanıcının kapsamı/alan iziniyle çalışır; serbest SQL yok. Yapısal olarak güvenli senaryo | 2 |
| 10 | **Bilgi tabanı önerisi** (talebe uygun çözüm makalesi) | M (getirme, üretim yok) | 4 | 2 | M9I planı sonrası | 2 |
| 11 | **İş akışı etki analizi** ("bu alanı/aşamayı değiştirirsem hangi kural/workflow/rapor etkilenir?") | K (bağımlılık grafiği) + L (açıklama) | 3 | 2 | Mimari resimdeki "Etki Analizi (AI Destekli)": **çekirdek deterministik grafiktir**, LLM yalnız anlatır. C-X1 (BPMN) sonrası | 2 |
| 12 | **Sonraki en iyi eylem** | K (M9G + risk bayrakları), L yalnız metin | 3 | 2 | Ayrı model gerekmez | 2 |
| 13 | **E-posta/web formu → alan çıkarımı** (intake) | L | 3 | **5** | Halka açık girdi + yazma yolu = en kötü enjeksiyon profili; yalnız "öneri olarak doldur" kipinde, insan onayıyla; erteli | 3 |
| 14 | **Duygu analizi** | M | 2 | 3 | Türkçede alaycılık/aksansız yazım zayıf; kişi profilleme; değer düşük | atla |
| 15 | **Öngörü yardımı** | K | 2 | 1 | M9F istatistiği kalır. LLM sayıları **üretmez**, en fazla verilen sayıları anlatır | – |

**Sıralama sonucu:** 1–4 (Faz 0) → 5–8 (Faz 1) → 9–12 (Faz 2) → 13 (Faz 3, koşullu). "Sadece LLM ile yapılabilen" tek ana kabiliyet **serbest metni anlama/üretme** (6, 7, 13); geri kalanı kural veya gömme ile alınır. Bu yüzden GPU kararı yalnız 6/7'nin değerine bağlıdır: değer ölçülebilir olana dek (spike + pilot kabul oranı) büyük donanım yatırımı gerekçelendirilemez.

**ML/skor için yeniden değerlendirme koşulları** M9F D15 ile **aynıdır** (kiracı başına ≥ 24 ay ve ≥ 5 000 kapanmış fırsat, kiracıya özel, açık rıza, açıklanabilir model, ayrı kart + güvenlik/KVKK incelemesi, ağırlıklı hattın MAPE'sini ≥ %15 düşürme). Kiracılar arası model eğitimi **yasak** kalır (K2/K13).

## 2. Mimari seçenekler

### 2.1 Seçenekler

- **A — AI yok, kural/istatistiğe yatırım.** Faz 0 senaryoları; ek altyapı yok.
- **B — Kendi veri merkezinde çıkarım sidecar'ı.** `IAiProvider` portu, Worker `ai` rolü, `internal` ağda vLLM (sohbet) + TEI/benzeri (gömme); pgvector.
- **C — Dış API sağlayıcıları.** Egress kapısı (M8B) arkasında, kiracı bazlı opt-in, redaksiyon zorunlu. KVKK m.9: yurt dışı aktarım için yeterlilik kararı **yok** (Mayıs 2026 itibarıyla, [S10] [Ö]) → standart sözleşme (imzadan sonra 5 iş günü içinde Kurul'a bildirim) ya da bağlayıcı şirket kuralları; sözleşme ve sorumluluk kiracıdadır.
- **D — Hibrit.** Aynı port; varsayılan B, C yalnız opt-in adaptör. **Bu belgenin önerisi D'nin B-öncelikli, C'nin uygulanmadığı hâlidir.**

### 2.2 Karşılaştırma (1–5, yüksek = iyi; ağırlıklar yargıdır)

| Ölçüt (ağırlık: taban / değer-ağırlıklı) | A | B | C | D |
|---|---|---|---|---|
| KVKK ve veri çıkışı (20 / 20) | 5 | 5 | 2 | 4 |
| Ürün değeri / açılan senaryo (20 / 40) | 2 | 4 | 5 | 5 |
| Türkçe kalite (10 / 10) | 3 | 3 | 5 | 4 |
| Maliyet: donanım + para (15 / 10) | 5 | 2 | 4 | 3 |
| İşletim yükü Compose→K8s (15 / 10) | 5 | 2 | 4 | 2 |
| Gecikme/ölçek (10 / 5) | 5 | 3 | 4 | 4 |
| Lisans/tedarikçi bağımlılığı (10 / 5) | 5 | 4 | 2 | 4 |
| **Toplam /100, taban ağırlık** | **84** | 68 | 74 | 75 |
| **Toplam /100, değer-ağırlıklı** | 72 | 73 | 80 | **82** |

Okuma: maliyet/risk baskınsa A kazanır (marjinal risk sıfır); değer baskınsa D. Bu nedenle **karar sıralıdır: A hemen, D mimarisi ile B'yi spike sonucuna bağlı.** Türkçe kalite notları: B'de Türkçe için elimizdeki kanıt, açık modellerde **boyutun** belirleyici olduğu ([S3]: 10B altı modeller çok zayıf, Gemma-3-27B %73, Qwen3-Next-80B %75, Llama-3.1-8B %45,7; [Ö]) ve Türkçe'ye özel ince ayarın genel modellere üstünlük getirmediği ([S4] [Ö], özetler kendi arasında çelişkili → doğrulanmadı). C'nin 5 puanı frontier modellerin Türkçe NER'de öndeliğine dayanır ([S5], yalnız 8 makale, zayıf kanıt) — **kanıt zayıftır, spike ile ölçülmeli.**

### 2.3 B için donanım kademeleri (ağırlık baytı = parametre × bit/8; KV önbelleği ek; **hesap, ölçüm değil**)

| Kademe | Donanım | Ne çalışır | Kabiliyet |
|---|---|---|---|
| T0 | GPU yok, 8–16 çekirdek CPU + 32 GB RAM | Gömme modeli (bge-m3 ~0,57 B parametre, Qwen3-Embedding-0.6B) TEI/CPU; LLM yalnız 4–8B Q4 llama.cpp ile toplu | Arama + sınıflandırma öneri mümkün; **özet/taslak kalitesi Türkçede yetersiz beklenir** |
| T1 | 1 × 24 GB GPU | Gemma 4 12B (bf16 ≈ 24 GB sınırda; int8 ≈ 12 GB) veya 26B-A4B MoE int4 (25,2 B parametre ⇒ ≈ 14 GB ağırlık) [S1]; Qwen3.5-27B/35B-A3B int4 ≈ 15–17 GB [S2] | 5–10 eşzamanlı kısa özet/taslak; pilot için yeterli varsayım |
| T2 | 1 × 48–80 GB GPU | Gemma 4 31B (30,7 B; fp8 ≈ 31 GB, int4 ≈ 17 GB) [S1]; büyük MoE/gpt-oss-120b (≈ 60+ GB, lisans/boyut **[?]**) | Kalite üst sınırı; çok kiracılı yük |
| T3 | ≥ 2 GPU / K8s GPU havuzu | Çoklu kopya, ayrı gömme/sohbet | Yüksek erişilebilirlik; ilk pilot için gereksiz |

Fiyat teklifi ve Türkiye'de tedarik süresi **araştırılmadı** (bilinmeyen, §5).
Yük tahmini (varsayım, ölçülecek): 250 kullanıcılı bir kiracıda günde ~1 000 özet/taslak × ~2 K girdi + ~0,4 K çıktı token → tek T1 GPU'da kuyruklanmadan karşılanır; kritik sınır anlık eşzamanlılıktır (≤ 10). Gömme: 1M metin parçası ≈ 4 GB (1024 boyut float32 = 4 096 B; `halfvec` 2 048 B) + indeks; **yalnız metin ağırlıklı alanlar** gömülür, tüm satırlar değil (K3'ün 10M kayıt hedefi gömme için uygun değildir).

### 2.4 Çıkarım sunucusu seçimi

| Sunucu | Uygun | Not |
|---|---|---|
| **vLLM** | Çok eşzamanlı, GPU, OpenAI uyumlu API, yapılandırılmış (şema kısıtlı) çıktı | 8 eşzamanlı kullanıcıda ~2,3× (A100, Llama-3-8B, 187 vs 82 tok/s), yüksek eşzamanlılıkta 10–20× iddiası [S8] [Ö]: blog düzeyi, kontrolsüz kıyas; Apache-2.0 ve API uyumu **[?]** |
| llama.cpp | CPU/küçük GPU, toplu iş | Ollama ile aynı motor; ~5+ eşzamanlıda ölçeklenmez [S8] [Ö] |
| Ollama | Geliştirici makinesi | Üretimde önerilmez (< 5 kullanıcı) [S8] |
| TEI (Hugging Face) | Gömme | Apache-2.0 [S7] [Ö]; CPU/GPU |

Öneri: sohbet için **vLLM**, gömme için **TEI**; ikisi de **görüntü digest'iyle sabitlenir** (C-SEC L10 kuralı; MinIO K21 emsali), model dosyaları **çevrimdışı** indirilip sağlama toplamı doğrulanır, çalışma anında Hugging Face'e çıkış yoktur.

### 2.5 Aday modeller (lisans: ticari kullanım)

| Model | Lisans | Boyutlar | Türkçe kanıtı | Durum |
|---|---|---|---|---|
| Gemma 4 (2026-04-02) | Apache 2.0 [S1] [D] | E2B, E4B, 12B, 26B-A4B, 31B; 35+ dil destekli, 140+ ön eğitim; MMMLU 31B %88,4 / 26B-A4B %86,3 / 12B %83,4 / E4B %76,6 (çok dilli, **Türkçe'ye özgü değil**) | Türkçe'ye özel ölçüm **yok** | Spike adayı (12B, 26B-A4B, 31B) |
| Qwen3.5 / Qwen3 | Apache 2.0 (Qwen3.5-35B-A3B LICENSE [D]; Qwen3 [Ö]) | Qwen3.5: 27B, 35B-A3B, 122B-A10B; Qwen3: 0,6–32B, 30B-A3B; 119 dil [S2] | TurkBench: Qwen3-Next-80B %75, Qwen2.5-14B %66,5 [S3] [Ö] | Spike adayı (27B, 35B-A3B) |
| gpt-oss-120b | Apache 2.0 **[?]** | ~120 B | TurkBench açık modeller arasında birinci (%78,6) [S3] [Ö] | Yalnız T2 varsa |
| Llama sürümleri | Özel topluluk lisansı **[?]** | – | Llama-3.1-8B TurkBench %45,7 [S3] | **Öneri: dışarıda bırak** (lisans koşulu + zayıf sonuç) |
| Gömme: bge-m3 | MIT [S7] [Ö] | 1024 boyut, 100+ dil, yoğun+seyrek | – | Spike adayı |
| Gömme: Qwen3-Embedding 0.6B/4B/8B | Apache 2.0 [S6] [Ö] | 32k bağlam; Türkçe STS (TurkEmbed çalışması) Qwen3-Embedding-8B Pearson 0,701 / Spearman 0,721 [S6] [Ö] | kısmi | Spike adayı |
| Gömme: multilingual-e5-large | MIT [S7] [Ö] (kaynaklar tutarsız → model kartından doğrula) | 1024 boyut | – | Yedek |

Lisans kuralı: yalnız **Apache-2.0/MIT** modeller; her model için lisans dosyası SBOM'a kaydedilir (bkz. §3.10).

### 2.6 Önerilen çalışma mimarisi (B/D)

```
Web ──► API (kullanıcı kimliği, plan/bayrak/limit, M9H kapsam + alan izni)
         │  bağlamı ÖZEL OKUMA PORTLARIYLA toplar, sınırlar, redakte eder
         │  ai.tasks(kiracı, kullanıcı, özellik, DONMUŞ girdi yükü, TTL) yazar → 202 + taskId
         ▼
     Postgres (şema ai)  ◄── Web, GET /ai/tasks/{id} ile yoklar (SignalR yok, K16)
         ▲
   Worker rolü `ai` (ayrı konteyner `ai-worker`, M8A `notification-worker` deseni)
         │  yalnız donmuş yükü okur; iş verisine DOKUNMAZ (gömme işi hariç, aşağıda)
         ▼
   `ai` internal ağı ──► llm (vLLM) · embed (TEI)      (çıkış yok, API/web ağdan erişemez)
```

- **Neden asenkron:** LLM yanıtı zaten saniyeler sürer; tek yol (Worker) kimlik, ağ ve kaynak yüzeyini daraltır (M8B "dış çağrıyı yalnız Worker yapar" ilkesiyle aynı). Bedel: akış (streaming) yok, ~1 sn yoklama gecikmesi. Ürün sahibi bunu kabul etmezse alternatif: API'nin `IAiProvider`'a doğrudan, eşzamanlılık kapılı çağrısı (M9F D7 kalıbı); güvenlik modeli aynı kalır, yalnız `llm` ağına API bağlanır.
- **Bağlam API'de toplanır** çünkü Worker sistem bağlamıdır (`UseSystem`) ve M9G'de olduğu gibi sistem kimliği `[RequiresPermission]` denetimlerini atlar; Worker'a "kullanıcı adına oku" yetkisi vermek yerine **yetkilendirilmiş, boyutu sınırlı, redakte yük** Worker'a geçer.
- **Gömme boru hattı** (istisna): kayıt değişimi outbox olayı → Worker `ai` rolü → **yalnız izinli (gömülebilir) alanlardan** parça → TEI → `ai.embeddings`. Okuma anında görünürlük süzgeci uygulanır (§3.2).
- **Port** (senseik ADR-0006 adlarıyla hizalı; `Microsoft.Extensions.AI` soyutlamalarının sarılması ayrıca değerlendirilir [?]):
  ```csharp
  public interface IAiProvider {
      AiProviderInfo Info { get; }   // Id, LeavesDatacenter, ModelId, ModelRevision, Licence
      Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct);       // AiRequest: TenantId, Feature, Messages, JsonSchema?, MaxOutputTokens, Timeout, DataClass
      Task<AiEmbedding[]> EmbedAsync(AiEmbedRequest request, CancellationToken ct);
  }
  ```
  Adaptörler: `SelfHostedOpenAiCompatibleProvider` (uygulanır), `ExternalApiProvider` (yalnız sözleşme; **çalışma zamanında `LeavesDatacenter = true` adaptör yalnız kiracı opt-in + operatör `AllowExternalAi` + redaksiyon kanıtıyla seçilir**, aksi `ai.provider_not_allowed`).
- **Modül:** yeni kapı modülü `Sense.Crm.Modules.Ai` (şema `ai`; M9F/M8B kalıbı): hiçbir varlık modülüne bağlanmaz, varlık modülleri `IAiContextSource` (yetkilendirilmiş, alan izinli, redakte metin bağlamı üretir) ve `IAiEmbeddingSource` (gömülebilir alan beyaz listesi) uygular; mimari test: `Ai.*`'ı yalnız host'lar ve testler referans eder; `IAiProvider` yalnız `Ai.Infrastructure` içinde çağrılır.
- **Compose:** `deploy/docker-compose.ai.yml` **overlay** + profil `ai` (varsayılan `up`'ın parçası değil, K20 overlay emsali). GPU: `deploy.resources.reservations.devices` (NVIDIA Container Toolkit ana makinede gerekir **[?]** sürüm uyumu). `ai` ağı `internal: true`; yalnız `ai-worker` bağlı. **K8s:** `ai-worker` ayrı Deployment, `llm`/`embed` GPU düğüm havuzunda (device plugin/GPU Operator **[?]**), model ağırlıkları PVC + init işi; C-X5 ile birlikte ele alınır.
- **Hata bağımsızlığı:** sidecar çökse/GPU yoksa yalnız AI özellikleri `503 ai.unavailable` verir; çekirdek CRM etkilenmez (özellik bayrağı + devre kesici).

## 3. Güvenlik ve KVKK

### 3.1 Kiracı izolasyonu

| Konu | Karar |
|---|---|
| İstem (prompt) | Her istem **tek kiracının** verisiyle kurulur; bir istemde iki kiracı asla; ortak yalnız sistem istem şablonu. Toplu (batch) çıkarımda farklı kiracıların dizileri aynı GPU yığınında koşabilir (diziler yalıtık). Ön ek önbelleği (prefix cache) kiracı içeriğini paylaşmamalı: yalnız sabit sistem istemi paylaşılır ya da önbellek **kiracıya özel tuzlanır** (vLLM desteği **[?]**, spike'ta doğrulanır) |
| Vektör deposu | `pgvector` (`ai.embeddings`: `tenant_id`, `entity_type`, `entity_id`, `owner_user_id`, `chunk_no`, `model_id`, `content_hash`, `embedding halfvec`). EF **global kiracı süzgeci** (K2) **+ PostgreSQL RLS'i bu tabloda ilk kez ikinci savunma hattı olarak devreye al** (K2 "ileri aşamada RLS"; `crm_app` tablo sahibi olmadığı için RLS'e tabidir, K17). Uygulama, oturum başına `app.tenant_id` ayarlar |
| ANN + RLS | HNSW'de süzgeç **sonradan** uygulanır; ef_search 40 varsayılanında seçici süzgeçle çok az sonuç kalır, pgvector 0.8 yinelemeli tarama bunu hafifletir [S11] [Ö]. **Varsayılan: indekssiz, `(tenant_id, entity_type)` btree ile daraltılmış tam tarama** (kiracı başına ~100 bin vektöre kadar hızlı varsayımı **[?]**, ölçülecek); büyük kiracıda kiracıya özel kısmi HNSW veya kiracı bölümlemesi. Test: çapraz kiracı sorgusu 0 sonuç (mevcut izolasyon test kalıbı) |
| Altyapı bağımlılığı | `postgres:17.11-alpine` imajında pgvector **yok**; özel imaj ya da pgvector'lü imaj gerekir, uzantı kurulumu init betiğinde (`deploy/postgres/init-roles.sql`); yedek/geri yükleme (runbook) ve `ANALYZE` bakımı etkilenir. `vector` uzantısının "trusted" olup olmadığı **[?]** |
| KVKK imhası | Kiracı imhası `TenantDataEraser<AiDbContext>` ile otomatik (M7); kayıt silindiğinde outbox olayıyla gömme silinir |

### 3.2 Kayıt kapsamı ve alan izni (model kullanıcının göremediğini görmemeli)

1. **Bağlam toplama = kullanıcının okuma yolu.** `IAiContextSource` yalnız kapsamlı okuma depolarından (`db.<X>` M9H kapsam süzgeci + `IFieldAccess.GetReadableAsync`) okur; ham `DbSet` yok (M9F `ReportSourceBase` emsali, mimari test). Gizli alan **istemde yer almaz**. Görünmeyen ilişkili kayıtlar (hesabın başkasının kişileri/fırsatları) bağlama girmez; sayaçlar görünür kümeyi sayar.
2. **Kapalı-başarısız:** `IFieldAccess`/kapsam çözümü hata verirse AI çağrısı yapılmaz (`503`), tüm alanlar açılmaz (M9F ile aynı).
3. **Gömme oracle'ı:** gömme, alan izinli bir alanın içeriğini içeriyorsa **arama sıralaması** o içerikle ilgili bilgi sızdırır (kayıt görünürse bile alan gizliyse). Kural: **gömülebilir alan = PII olmayan, `isSensitive` olmayan ve `IFieldAccessCatalog`'da gizlenebilir olmayan metin alanları** (talep konusu/açıklaması, yorumlar, aktivite notları, makale). Gizlenebilir alan gömme kaynağına **girmez**.
4. **Okuma anı süzgeci:** vektör aday kimlikleri döner → `IRecordVisibility.GetVisibleAsync` ile süzülür (fazla getir + kırp); mümkünse `owner_user_id = ANY(kapsam)` ön süzgeç (M9F anlık görüntü satırı deseni). Görünmeyen kayıt yok sayılır, sayı/sıra sızmaz.
5. **Çıktı önbelleği:** üretilmiş özetler **kullanıcılar arası paylaşılmaz**; anahtar `(kiracı, kayıt sürümü, scopeKey, fieldKey, özellik, istem sürümü, model)` (M9F önbellek anahtarı emsali) ve kısa TTL, ya da hiç saklanmaz.
6. **Oracle testleri:** kapsamlı kullanıcı, alan izni kısıtlı kullanıcı ve kiracılar arası için "sızıntı = 0" matrisi (M9H kapsam matrisiyle aynı sözleşme).

### 3.3 Enjeksiyon (müşteri kaynaklı metin)

Saldırgan yazılı içerik kaynakları: web formları (M9G halka açık yüzey), gelen e-posta (ileride), talep yorumları, içe aktarılan CSV, firma/kişi adları. Dolaylı istem enjeksiyonu OWASP LLM Top 10 (2025) LLM01'dir [S12] [Ö]. Tehlikeli üçlü (özel veri + güvenilmeyen içerik + dış iletişim kanalı; terim, oturumda doğrulanmadı **[?]**): **tasarım üçüncü ayağı yapısal olarak keser.**

| Katman | Kural |
|---|---|
| Yetenek kısıtı (birincil) | Modelin **araç/fonksiyon çağrısı yok**, ağı yok (`ai` ağı `internal`), URL getirmez, `mcp` yok. Yazma yetkisi yok: çıktı yalnız metin/JSON'dur |
| Çıktı işleme | React düz metin kaçışı; `dangerouslySetInnerHTML` yasak; Markdown açılırsa görüntü/bağlantı/HTML kapalı alt küme (bağlantı yok: sızdırma ve kimlik avı kanalı). Yapılandırılmış çıktı **şema kısıtlı** çözümleme + **sunucuda doğrulama** (etiket kümesi, M9F tanım doğrulayıcısı), uymayan çıktı reddedilir |
| Eylem | **AI çıktısı hiçbir zaman otomatik eylem tetiklemez.** Kullanıcı "uygula" der; komut **kullanıcının kendi kimliğiyle** normal `dispatcher` hattından geçer (`[RequiresPermission]`). AI çıktısı **M9G kural motoruna/`AutomationActor`'a/`WebFormActor`'a girdi olamaz** (mimari test) |
| Bağlam | Yalnız göreve gereken alanlar (en az yetki/veri); kullanıcı-yazımlı metin sınırlayıcılarla işaretlenir ("veri, talimat değildir"), uzunluk sınırı, denetim/bidi karakter temizliği. **Savunma derinliği**, tek başına güvence değil |
| Yönetici hedefli | Saldırgan bir talep yorumuna yazar, yönetici özet ister: çıktı yanıltıcı olabilir. Önlem: "AI üretimi, doğrulayın" etiketi, **kaynak alıntı/kayıt bağlantısı**, çıktıda bağlantı yok |
| Test | CI'da Türkçe+İngilizce enjeksiyon dizeleri (talep yorumu, form, kişi adı) içeren altın küme; sızıntı/talimat izleme/şema ihlali/etiket manipülasyonu ölçütleri; kanarya belirteçleri |

Kalan risk **sıfırlanamaz**; tasarım amacı etki alanını "yanıltıcı metin gösterimi"ne indirmektir.

### 3.4 Çıktı işleme özeti

Uzunluk sınırı, şema doğrulama, bozuk/reddedilen çıktı `ai.output_invalid`; taslak düzenleyiciye düşer, e-posta gönderimi M8A hattında **kullanıcı eylemidir**; boş/reddedilen yanıt kullanıcıya nedeniyle döner. PII sızıntı denetimi (çıktıda bağlamda olmayan e-posta/TCKN/IBAN deseni) basit düzenli ifade + Luhn/TCKN sağlaması ile yapılır, yakalanırsa çıktı bastırılır.

### 3.5 PII en aza indirme ve redaksiyon

- **B'de:** veri veri merkezinden çıkmaz; yine de KVKK ölçülülük ilkesi gereği yalnız gerekli alanlar bağlama girer (ad yerine yer tutucu gerekmez ama e-posta/telefon/TCKN/IBAN/adres **girmez** varsayılan olarak; görev gerektirmedikçe).
- **C'de (yalnız opt-in):** zorunlu redaksiyon: belirlenimci (e-posta, telefon, IBAN, TCKN sağlamalı, kart) + ad/kurum için NER; yer tutucu eşlemesi sunucuda kalır, yanıtta geri konur. Türkçe NER doğruluğu değişkendir (F1 0,68–0,91, 8 makalelik zayıf ölçüm [S5]); redakte metin **anonim sayılmaz**, hâlâ kişisel veridir ve m.9 rejimine tabidir [S10].
- Gömmeler de kişisel veri sayılır (metinden kısmen geri çıkarılabilir): saklama, silme ve yetki kuralları metinle aynıdır.

### 3.6 Saklama ve denetim

| Öğe | Karar |
|---|---|
| İstem/çıktı | **Varsayılan saklanmaz.** `ai.tasks` yükü ve sonucu görev bitince ≤ 24 sa (TTL, Worker temizliği); sonuç yalnız isteyen kullanıcıya görünür |
| Meta günlük | `ai.invocations`: kiracı, kullanıcı, özellik, model kimliği+revizyonu, adaptör, token sayıları, gecikme, sonuç, girdi kayıt kimlikleri, içerik özeti (SHA-256). İçerik yok |
| Hata ayıklama yakalama | Kiracı bayrağı, **varsayılan kapalı**, 7 gün TTL, PII maskeli; yalnız yönetici |
| Denetim | Kullanıcının **kabul ettiği** AI çıktısı ordinary yazma olarak denetlenir; denetim satırına `ai_invocation_id` (M8B `api_key_id` emsali). Etiket/günlük/metriklere kiracı-kullanıcı-içerik **asla** (K20: düşük kardinalite) |
| Kalıcı çıktı | Kullanıcının kaydettiği özet/taslak normal kayıt verisidir (silme/erişim kuralları kaydın) |

### 3.7 Kiracı bayrakları, planlar, izinler

- Özellik bayrakları `features["ai.assist"]`, `["ai.search"]`, `["ai.report-builder"]` (M8A/M9F `features` mekaniği). **Varsayılan kapalı**; kiracı yöneticisi açar (`org.ai.manage`) ve açarken aydınlatma metni/veri sorumlusu uyarısı gösterilir. İzin: `crm.ai.use` (kullanım); Standard rolüne verilir ama yalnız bayrak açıksa etkin.
- Plan limitleri: `maxAiRequestsPerDay`, `maxAiTokensPerMonth`, `maxEmbeddedRecords`; aşım `402 plan.limit_exceeded`; askıda/salt-okunur kiracıda AI çalışmaz (M7 `EntitlementBehaviour`).
- `IAiProvider` seçimi kiracı + operatör bayrağıyla; harici adaptör yalnız ikisi de açıksa.

### 3.8 Açıklanabilirlik ve profilleme (KVKK m.11/1-g)

Skorlar (lead, risk) **kurallıdır ve her sonuç faktör listesiyle gösterilir** ("+15 kaynak: fuar, +10 son 7 günde etkileşim…"); ağırlıklar kiracı yapılandırmasıdır ve görünürdür. Kişiler için skorlamak profilleme sayılır ve m.11/1-g (yalnız otomatik analizle aleyhe sonuca itiraz) tetiklenebilir; önlem: skor **yalnız öncelik sıralama önerisidir**, hiçbir hizmet reddi/otomatik karar üretmez; mümkünse firma/fırsat düzeyinde; kişi düzeyi girdiler hassas olmayan etkileşim alanlarıyla sınırlı. KVKK Kurumu'nun "Üretken Yapay Zekâ ve Kişisel Verilerin Korunması Rehberi" (2025-11-24, 15 soru) veri sorumlusu yükümlülüklerini değerlendirir [S9]; bu oturumda yalnız duyuru sayfası okundu, **rehberin tam metni okunmadı** → aydınlatma, hukuki sebep, VERBİS/DPIA gibi yükümlülükler hukuk incelemesine bırakılır.

### 3.9 Maliyet, kötüye kullanım ve hız sınırları

M9F D7 kalıbı: kullanıcı başına eşzamanlı ≤ 2, kiracı ≤ 4, süreç için GPU semaforu; **iki kuyruk** (etkileşimli öncelikli, gömme/toplu geri dolgu düşük öncelikli); istem/çıktı token tavanı; istek zaman aşımı; kiracı/plan günlük+aylık bütçe; kullanıcı başına dakikalık hız sınırı; devre kesici + **kill switch** (operatör `Ai:Enabled=false`); adil paylaşım (gürültücü kiracı diğerini aç bırakmaz, M8B `PerTenantBatch` emsali). Metrikler `ai_requests_total{feature,outcome,provider}` gibi düşük kardinaliteli.

### 3.10 Model ve sürüm yönetişimi

`ai.models` kaydı: ad, **revizyon (commit/digest)**, lisans dosyası özeti, niceleme, boyut, değerlendirme raporu kimliği, durum (`candidate|approved|retired`). Model değişimi **değerlendirme kapısından** geçer (altın Türkçe CRM kümesi; §5 spike aracı kalıcı hâle gelir; LLM-hakem tek başına ölçüt değildir). İstem şablonları kodda sürümlü (`WorkflowNames.DefinitionVersion` emsali) ve çıktıda saklanan `prompt_version`. **Gömme modeli değişimi tüm vektörleri geçersiz kılar** (`model_id` sütunu + çift yazma + geçiş); bu yüzden gömme modeli seçimi düşük geri dönüş maliyetli değildir, spike'ta dikkatle yapılır. Sidecar imajları digest ile sabit, güvenlik yaması runbook'a girer (MinIO K21 bakım modu dersi).

## 4. Öneri ve yol haritası

**Öneri:** A hemen; ardından D mimarisiyle **B** (spike başarılıysa); C uygulanmaz (yalnız sözleşme). Mimari kararlar (sırasıyla): Worker rolü `ai`, bağlam API'de/kullanıcı kimliğiyle, donmuş yük, çıkışsız `ai` ağı, pgvector + RLS pilotu, varsayılan kapalı.

Yaklaşık emek (mühendis-haftası, Backend+Web+DevOps; **kaba tahmin**, spec sonrası kesinleşir):

| Kart (öneri) | İçerik | MH | Bağımlılık |
|---|---|---|---|
| **C-AI0A** Akıllı kurallar | Fırsat risk bayrakları, lead skor kartı (yapılandırılabilir, faktör listeli), yinelenen `similarity()` skoru, veri kalitesi panoları | Backend 2,5 + Web 2 = **4,5** | M9A (`last_activity_at`), M9E, M9F (durgun/öngörü), M9G |
| **C-AI0B** Türkçe arama iyileştirme | `pg_trgm` sıralama/benzerlik ayarı, eşanlamlı/eksik-aksan durumları (M9A kapsamı ile çakışmayı Lead çözer) | **1,5** | M9A |
| **C-AI-S** Spike | GPU envanteri, Türkçe CRM benchmark'ı (§5), izin oracle prototipi, vLLM ön ek önbelleği tuzlama, pgvector+RLS performansı | **1,5** (2 hafta takvim) | Yok; Faz 1 kapısı |
| **C-AI1** Altyapı | `Ai` modülü, `IAiProvider` + vLLM/TEI adaptörü, `ai.tasks`/kuyruk/TTL, Worker rolü + `ai-worker`, bayraklar/limitler/izinler, denetim, metrikler, `docker-compose.ai.yml`, runbook, model kaydı; güvenlik incelemesi | Backend 4 + DevOps 1,5 + Security 0,5 = **6** | C-AI-S başarılı; **M9H** (kapsam + alan izni) |
| **C-AI2** Anlamsal arama | pgvector migration/imaj, gömme boru hattı (outbox), hibrit arama, oracle testleri | Backend 3 + Web 1 = **4** | C-AI1, M9A |
| **C-AI3** Özet + yanıt taslağı | Talep/hesap özeti, taslak düzenleyici, "AI üretimi" arayüzü, kabul oranı ölçümü | Backend 3 + Web 2 = **5** | C-AI1, M6B, M9D |
| **C-AI4** Sınıflandırma önerisi | Kategori/öncelik/ekip önerisi (gömme kNN), kabul ölçümü; M9G kural eşlemesi kullanıcı tanımlı | **2,5** | C-AI2, M9G |
| **C-AI5** Doğal dil rapor kurucu | LLM → M9F v1 tanımı, doğrulayıcı, önizleme | **3** | C-AI1, M9F |
| **C-AI6** Bilgi tabanı önerisi | Gömme getirme | **2** | C-AI2, **M9I planı** |
| **C-AI7** Etki analizi | Bağımlılık grafiği (kural, workflow, rapor, alan) + LLM açıklama | Backend 2,5 + Web 1 = **3,5** | C-X1, M9G, C-AI1 |
| C-AI8 (koşullu) | Dış adaptör (redaksiyon + opt-in + hukuk) | **2,5** + hukuk | PO kararı 2 |
| C-AI9 (koşullu) | Lead/fırsat ML (M9F koşulları) | **4+** | M9F koşulları |

Toplam: Faz 0 ≈ 6; Faz 1 (spike+altyapı+arama+özet) ≈ 16,5; Faz 2 (sınıflandırma, rapor kurucu, KB, etki) ≈ 11; koşullu ≈ 6,5+. Hat: `C-AI0A/0B ∥ C-AI-S → C-AI1 → (C-AI2 ∥ C-AI3) → C-AI4/5/6/7`. Panoya her kart Spec→Backend+Web akışıyla girer; güvenlik incelemesi merge kapısında (C-AI1, C-AI3, C-AI5).

## 5. Riskler ve spike gerektirenler

| # | Risk / bilinmeyen | Etki | Spike / önlem |
|---|---|---|---|
| R1 | **Veri merkezinde GPU var mı, tedarik süresi/maliyeti/güç-soğutma?** (araştırılmadı) | B'nin tamamı | Envanter + teklif; yoksa B = yalnız gömme (CPU) ve LLM ertelenir |
| R2 | **Türkçe kalite** (kısa, hatalı, aksansız, kod karışık CRM metni) açık modellerde yeterli mi? Elimizdeki kanıt genel benchmark, CRM'e özgü değil [S3][S4] | Değer teorisi | 150–250 gerçek/sentezlenmiş anonim metin; iki insanla altın küme; görev başına ölçüt (özet: kör insan kabul oranı ≥ %70 önerisi; sınıflandırma: makro-F1 ≥ 0,80 önerisi); adaylar: Gemma 4 12B/26B-A4B/31B, Qwen3.5-27B/35B-A3B, (T2 varsa gpt-oss-120b); gömme: bge-m3, Qwen3-Embedding; **tavan referansı** olarak bir bulut modeli **yalnız sentetik/anonim metinle** |
| R3 | Enjeksiyon artığı riski | Yanıltıcı çıktı, hiç yazma yok | §3.3 kırmızı takım altın kümesi; CI kapısı |
| R4 | İzin oracle (gömme/önbellek/özet) | Kapsam ihlali | §3.2 matris testi, kapı |
| R5 | KVKK: hukuki sebep, aydınlatma, profilleme, veri sorumlusu rolü; rehberin tam metni okunmadı [S9] | Yasal | Hukuk incelemesi; PO kararları 2, 5, 8 |
| R6 | Operasyon: NVIDIA sürücü/toolkit, vLLM sürüm hızı, tek GPU tek hata noktası | Kullanılabilirlik | Hata bağımsızlığı (§2.6), digest sabitleme, runbook, uyarılar |
| R7 | pgvector: Alpine imajı, yedek boyutu, RLS+HNSW geri çağırma, "trusted" uzantı [?] | Migration | Spike prototipi |
| R8 | Kullanıcı benimsemesi/halüsinasyon güveni | Değer | Kabul oranı metriği, kaynak alıntı, kapatma bayrağı |
| R9 | Gömme modeli kilitlenmesi (yeniden gömme maliyeti) | Bakım | `model_id` sütunu, çift yazma; erken benchmark |
| R10 | Lisans doğrulaması (Gemma önceki sürümler farklı lisanstı; Llama özel) | Hukuki | Her model kartı/LICENSE dosyası SBOM'da; yalnız Apache/MIT |
| R11 | Çok kiracılı GPU adaletsizliği | Ölçek | Kiracı başına kuyruk kotası (§3.9) |
| R12 | Yerli bulut LLM sağlayıcıları (C için) araştırılmadı | C seçeneği | C'ye geçilirse ayrı araştırma |

## 6. Ürün sahibi kararları (öneri ile)

1. **Faz 0 (kural tabanlı akıllı özellikler) hemen başlasın mı?** Öneri: evet (C-AI0A/0B).
2. **Dış API (C) politikası:** Öneri: uygulanmaz; talep doğarsa yalnız kiracı opt-in + hukuk + standart sözleşme (m.9) sonrası C-AI8.
3. **GPU bütçesi ve sahibi:** Öneri: spike öncesi envanter çıkarılsın; pilot için tek 24–48 GB GPU'lu sunucu varsayımı; onay yoksa yalnız gömme (CPU) ve Faz 0.
4. **Spike başarı eşiği** (Faz 1 kapısı): Öneri: özet kör kabul ≥ %70, sınıflandırma makro-F1 ≥ 0,80, p95 gecikme ≤ 30 sn @ 5 eşzamanlı.
5. **Varsayılan durum ve plan:** Öneri: her kiracıda **kapalı**, yönetici açar, üst plan bayrağı; sınırlar plan limitli.
6. **Kimler kullanır:** Öneri: `crm.ai.use` Standard'a verilir, kapı bayrak.
7. **Lead skoru:** Öneri: şeffaf skor kartı yeterli; ML yalnız M9F koşullarında ve ayrı kartta.
8. **Otomatik eylem:** Öneri: **kesin yasak**; AI çıktısı insan onayı olmadan hiçbir yazma/gönderim/M9G tetiği üretmez.
9. **İstem/çıktı saklama:** Öneri: varsayılan saklanmaz; görev sonucu ≤ 24 sa; hata ayıklama yakalama kiracı opt-in, 7 gün.
10. **Model lisansı politikası:** Öneri: yalnız Apache-2.0/MIT açık ağırlıklı modeller; Llama/özel lisanslı dışarıda.
11. **Asenkron görev modeli** (yoklama, akış yok) kabul mü? Öneri: evet; akış talebi olursa API doğrudan çağrı kipi.
12. **Kişi düzeyi profilleme:** Öneri: skor firma/fırsat düzeyinde; kişi düzeyi yalnız hassas olmayan etkileşim girdileri, "yalnız öncelik önerisi" etiketiyle.
13. **Arayüz etiketleme:** Öneri: her AI çıktısı "AI üretimi, doğrulayın" ve kaynak bağlantısı taşır (zorunlu).
14. **Dağıtım biçimi:** Öneri: `docker-compose.ai.yml` overlay + ayrı `ai-worker`; K8s'te GPU düğüm havuzu C-X5 ile birlikte.
15. **Sıra:** Öneri: özet + yanıt taslağı (C-AI3) ile başla, sınıflandırma ve rapor kurucu sonra; etki analizi C-X1'i bekler.

## Ek A — Kaynaklar (erişim: 2026-09-20)

| Kod | Kaynak | Ne için | Durum |
|---|---|---|---|
| S1 | Gemma 4 model kartı, `ai.google.dev/gemma/docs/core/model_card_4`; yayın 2026-04-02 (Google Open Source Blog, `opensource.googleblog.com/2026/03/gemma-4-expanding-the-gemmaverse-with-apache-20.html`, arama özeti) | Boyutlar, Apache 2.0, MMMLU, 35+/140+ dil. Kartta VRAM tablosu **yok** (donanım rakamları bu belgenin hesabıdır) | [D] (model kartı), yayın tarihi [Ö] |
| S2 | Qwen3.5-35B-A3B LICENSE (`huggingface.co/Qwen/Qwen3.5-35B-A3B/blob/main/LICENSE`) → Apache 2.0; Qwen3 blog/rapor (`qwenlm.github.io/blog/qwen3/`, arXiv 2505.09388) 119 dil, Apache 2.0; Qwen3.5 boyutları `deeplearning.ai/the-batch` özeti | Lisans, boyut | LICENSE [D]; diğerleri [Ö] |
| S3 | TurkBench, arXiv 2601.07020 | Açık modellerin Türkçe sonuçları (27 model; gpt-oss-120b %78,6, Qwen3-Next-80B %75, Gemma-3-27B %73, Qwen2.5-14B %66,5, Llama-3.1-8B %45,7; küçük modeller zayıf) | Sayfa özeti, tam metin doğrulanmadı [Ö] |
| S4 | Cetvel, arXiv 2508.16431 | Türkçe'ye özel modeller genel modelleri geçmiyor iddiası (özet); ikinci özet çelişkili | [?] |
| S5 | osmandagdeviren.com.tr NER kıyası, 2026-06-10 | Türkçe NER F1 0,68–0,91 vs İngilizce 0,93–0,98; 8 makale, 96 varlık | [Ö], zayıf kanıt |
| S6 | Qwen3-Embedding (`qwenlm.github.io/blog/qwen3-embedding/`, GitHub); TurkEmbed arXiv 2511.08376 | Apache 2.0, 0.6/4/8B, 32k, MTEB çok dilli 1. (2025-06-05, 70,58); Türkçe STS Pearson 0,701 / Spearman 0,721 (8B) | [Ö] |
| S7 | bge-m3 (Hugging Face), multilingual-e5-large, TEI (GitHub LICENSE) | MIT / MIT / Apache 2.0 | [Ö]; e5 kaynakları tutarsız |
| S8 | Red Hat Developer 2026-06-15 (llama.cpp vs vLLM), dev.to "vLLM vs Ollama 2026", Towards AI, InsiderLLM | Eşzamanlılıkta vLLM üstünlüğü, Ollama sınırı | [Ö], blog düzeyi kıyas |
| S9 | KVKK, "Üretken Yapay Zekâ ve Kişisel Verilerin Korunması Rehberi (15 Soruda)", 2025-11-24 (`kvkk.gov.tr/Icerik/8547/…`) | Rehberin varlığı ve amacı | Yalnız duyuru sayfası [D]; tam metin okunmadı |
| S10 | 7499 sayılı Kanun (RG 2024-03-12; m.9 değişikliği 2024-06-01 yürürlük); `kvkk.gov.tr/Icerik/2053/Yurtdisina-Aktarim`; hukuk büroları makaleleri (Güneş Partners, Güzeloğlu, 2026) | Üç basamaklı aktarım rejimi, standart sözleşme bildirimi (5 iş günü), Mayıs 2026'ya kadar yeterlilik kararı yok | [Ö]; hukukça teyit gerekir |
| S11 | pgvector filtreleme belgesi (pgEdge `docs.pgedge.com/pgvector/v0-8-1/filtering/`) ve topluluk yazıları | HNSW süzgeçleme sonradan, ef_search 40, 0.8 yinelemeli tarama | [Ö] |
| S12 | OWASP Top 10 for LLM Applications 2025 (LLM01 Prompt Injection), ikincil özetler | Dolaylı enjeksiyon sınıfı | [Ö] |
| S13 | `C:\Users\ALG0013\Documents\GitHub\senseik\docs\adr\0006-llm-and-mcp.md` (yerel, salt okunur) | `ILlmProvider`/`IEmbeddingProvider` port adları, tek araç kataloğu, "yazma araçları kullanıcı onayı ister", kullanıcının izin/kapsamının aynen uygulanması. Senseik'te bir `Ai` modülü **kodu yok** (yalnız ADR + `Anthropic` NuGet sürüm satırı). ADR'nin varsayılan sağlayıcısı bulut (Anthropic) olduğundan CRM'in çıkışsız varsayılanıyla **çelişir**; yalnız port adları ve ilke taşınır | [D] |

Belge dışı bilgi ve doğrulanmamış (**[?]**) maddeler: vLLM lisansı ve OpenAI uyumlu API, vLLM ön ek önbelleği tuzlama, gpt-oss-120b lisansı/boyutu, Llama lisansı, NVIDIA Container Toolkit/K8s device plugin ayrıntıları, `vector` uzantısının "trusted" durumu, "tehlikeli üçlü" teriminin kaynağı, `Microsoft.Extensions.AI` uygunluğu, donanım fiyatları ve tedarik.
