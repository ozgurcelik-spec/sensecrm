# M9H — H0 spike bulguları: kayıt kapsamı (RecordScope) mekanizması

Kart: `C-M9H` "Erişim modeli" (v0.7, H0). Plan: [m9h-erisim.md](../plan/m9h-erisim.md) ("Çekirdek mekanizma" D7–D11, "Backend uygulama notları" Adım 0). Kod: `spikes/m9h-record-scope/` (atılabilir, `Sense.Crm.slnx` dışında, hiçbir üretim projesi referans almaz). Yöntem: gerçek `ModuleDbContext` tabanı, gerçek `SalesDbContext` + gerçek migration'lar, gerçek `Program.cs` API host'u; ürün kodu **değiştirilmeden** (bir istisna: geçici yama, aşağıda). Ortam: .NET SDK 10.0.401, EF Core **10.0.12**, Npgsql.EFCore.PostgreSQL 10.0.3, PostgreSQL 17 (`postgres:17-alpine`, container `crm-h0-pg`).

## 1. Özet

| # | Soru | Karar | Tek satırlık gerekçe |
|---|---|---|---|
| Q1 | Aynı varlıkta 3 adlı filtre + `IgnoreQueryFilters(["ad"])` | **Onaylandı (dipnotla)** | Repo zaten adlı filtre kullanıyor; üçüncüsü tabana ~7 satırla ekleniyor. Dipnot: bilinmeyen ad **sessizce hiçbir şey yapmaz**; parametresiz `IgnoreQueryFilters()` yeni filtreyi de kapatır. |
| Q2 | İstek başına parametreleştirme, `= ANY(@p)`, önbellek, EF işlemleri | **Onaylandı, ama plan D7'nin ifadesi YANLIŞ** | "AsyncLocal'e bakan statik çağrı" filtrede **çürütüldü**: EF ilk kullanıcının sahip kümesini SQL'e sabit gömüp önbelleğe alıyor (kullanıcılar arası sızıntı). Filtre ifadesi **DbContext örneğini** (ilk argüman ya da örnek üyesi) içermeli; o zaman her yürütmede parametre olur. Alt kayıt `EXISTS` (D7c) çalışıyor. |
| Q3 | Bağlam kurulmamışsa davranış | **Onaylandı, sıkılaştırma önerisiyle** | Plan D8 doğru çalışıyor; ama "kurulmamış = sınırsız" AsyncLocal kaybında fail-open. API sürecinde `FailClosedWhenUnset=true` öneriliyor. Bulunan yeni delik: `IUsageMeter` istek içinde kapsamlı sayıyor (plan limiti eksik sayılır). |
| Q4 | Postgres performansı | **Onaylandı, bir planlayıcı uyarısıyla** | 200k satır: liste/detay milisaniye, filtre maliyeti ölçüm gürültüsünde. `= ANY(uuid[])` 20k sahipte bile kullanılabilir. Risk: seyrek/sıfır satırlı sahip + `ORDER BY created_at LIMIT` → planlayıcı yanlış plan (≈45 ms/200k satır, satırla doğrusal); `STATISTICS 1000` düzeltiyor. |
| Q5 | Hiyerarşi: CTE / closure / path | **Onaylandı** | Plan D11 (kiracı grafiği önbellekte + BFS) doğru; CTE yalnız yazma-doğrulamasında. Closure/path gereksiz. |
| Q6 | IDOR: gizli = yok (404, aynı gövde) | **Onaylandı (dipnotla)** | Gerçek uçlar (GET/PUT/DELETE, deals/accounts) filtreyle **kendiliğinden** aynı 404'ü veriyor; gövde/başlık yalnız `instance` içindeki kimlik, `traceId`, `X-Request-ID`, `X-Trace-Id` dışında bayt bayt aynı. `403 record.read_only` için `GlobalExceptionHandler.Map`'e **1 kol** yetiyor (bugün 500). |

Testler: **53 test, 52 geçti, 1 atlandı** (atlanan = çürütülen varsayımın "istenen davranış" testi, gerekçeli `Skip`). Soğuk (taze container, yeniden kullanım yok) tam koşu 202 sn. Ürün üzerinde geçici yama ile: `Tests.TenantIsolation` 20/20, `Modules.Sales.Tests` 76/76 (bkz. §3.4).

## 2. Nasıl yeniden üretilir

```
cd spikes/m9h-record-scope
dotnet build
./bin/Debug/net10.0/Sense.Crm.Spikes.M9hRecordScope.Tests.exe                       # hepsi
./bin/Debug/net10.0/Sense.Crm.Spikes.M9hRecordScope.Tests.exe -class "<Ad.Alanı.Sınıf>"  # tek sınıf (xunit v3; `dotnet test -- --filter-class` bu projede süzmüyor)
```
- Sabit adlı container `crm-h0-pg` testler bitince silinir. Geliştirme hızı için `H0_KEEP=1` container'ı ve tohumlanmış 300k satırı korur (artık kalırsa: `docker rm -f crm-h0-pg`).
- Perf sınıfları `[Trait("Category","Perf")]`. Kanıt dosyaları `spikes/m9h-record-scope/evidence/` (SQL metinleri, EXPLAIN planları, tablolar).
- Q6 testleri `CrmApiFactory`'yi kullanır (kendi rastgele adlı Testcontainers Postgres'i; tüm test projeleriyle aynı standart).
- Ölçümler **paylaşımlı, yüklü bir geliştirici makinesinde** alındı: mutlak değer değil büyüklük mertebesi ve karşılaştırma için okuyun (p95'ler gürültülü).
- `spikes/m9h-record-scope/patches/0001-record-scope-base-and-sales-registration.patch`: §3.4'te doğrulanan ürün yaması (uygulanmış hâli commit'lenmedi; `git apply` ile geri getirilir).

## 3. Bulgular

### Q1 — Adlı filtreler (Confirmed, caveats)
- EF 10'da `HasQueryFilter(string ad, expr)` ve `IgnoreQueryFilters(IEnumerable<string>)` var; `TenantQueryFilterConventionTests` zaten `GetDeclaredQueryFilters()` kullanıyor. Depo bugün `ModuleDbContext.TenantFilter`/`SoftDeleteFilter` adlarıyla seçici atlıyor (`IdentityReadStore`, `Repositories`).
- Testler (`Q1_NamedFilterTests`): bir fırsat varlığında filtre anahtarları `Tenant, SoftDelete, RecordScope`; kapsamsız katalogda yalnız `Tenant`; alt kayıtta `Tenant + RecordScope`. `IgnoreQueryFilters(["RecordScope"])` yalnız kapsamı atlar (kiracı ve silinmişler kalır); `[Tenant]` yalnız kiracıyı atlar.
- **Dipnot 1 — yazım hatası sessiz:** bilinmeyen ad hata **fırlatmaz**, hiçbir filtre atlanmaz (güvenli yönde başarısız: kapsam sürer). Çağrılar yalnız `const` adla yazılmalı.
- **Dipnot 2 — parametresiz `IgnoreQueryFilters()` her şeyi kapatır**, yeni filtre dahil. Bugünkü tek çağrı `Workflows/.../Repositories.cs` (sistem bağlamı): kabul, ama `RecordScopeBypassInventoryTests` "parametresiz çağrı" için de sayaç tutmalı.
- **Minimal fark (ölçüldü, §3.4):** kaynak yapılandırmasına `b.HasRecordScope("lead", x => x.OwnerUserId)` (annotation bırakır) + `ModuleDbContext.OnModelCreating` döngüsüne 5 satır. `ModuleDbContext` kurucu imzası değişmiyor (yeni dosya yalnız yardımcı tipleri taşır).

### Q2 — Parametreleştirme, önbellek, EF işlemleri

**Ana sonuç (plan düzeltmesi):** filtre gövdesinde AsyncLocal'e bakan **statik** çağrı yazılırsa EF bunu sorgu derlenirken **bir kez** değerlendirip sonucu SQL'e sabit gömüyor:

```
-- statik çağrı (ÇÜRÜTÜLDÜ), R1 ile derlenen sorgu X için de kullanıldı:
WHERE d.tenant_id = @ef_filter__CurrentTenantId AND NOT (d.is_deleted) AND d.owner_user_id = '7a895846-…'
```
`Q2_StaticStyleDisprovedTests`: ilk kullanıcı R1'in kapsamı derlenmiş sorguya girdi; X, kendi kaydı yerine R1'inkini gördü (kullanıcılar arası sızıntı, yeşil "kanıt" testi). İstenen davranış testi `Skip` (gerekçe metni içinde).

Çalışan iki biçim (ikisi de tüm testleri geçti; SQL aynı):
- **B — örnek üyesi** (`this.RecordScope.ReadAll("deal")`; mevcut `CurrentTenantId` kalıbı): taban sınıfa üye/kurucu bağımlılığı gerekir.
- **C — statik yöntem, ilk argüman DbContext örneği** (`RecordScopeContext.ReadAll(this, "deal")`): EF "bağlama bağlı" sayar, her yürütmede değerlendirir. **Önerilen**: taban sınıfta yeni üye/kurucu parametresi yok; mühürlü (sealed) gerçek `SalesDbContext` üzerinde `IModelCustomizer` ile bile çalışıyor (Q4/Q6 bunu kullandı).

Üretilen SQL (biçim C/B ile aynı):
```
WHERE d.tenant_id = @ef_filter__CurrentTenantId AND NOT (d.is_deleted) AND (@ef_filter__p3 OR d.owner_user_id = ANY (@ef_filter__p2))
```
`@p3` = ReadAll (bool), `@p2` = `uuid[]`. Npgsql planı parametre değerleriyle çıkardığından (repoda auto-prepare yok) `true OR …` yönetici sorgusunda sabit katlanıyor (EXPLAIN'de sahip koşulu yok).

Önbellek / zehirlenme (`Q2_ParameterisationTests`, iki biçim de):
- Havuzlu `AddPooledDbContextFactory` üzerinde iki kullanıcı 20 kez iç içe: **tek SQL metni**, filtre 40 yürütmede 40 kez yeniden değerlendirildi (derleme anında sabitlenmedi).
- Sahip kümesi boyutu 0 / 1 / 5 / 200 / 20 000 / 1: **tek SQL metni**, EF `IMemoryCache` girdi sayısı sabit (`IN (@p1..@pN)` genişlemesi yok → kullanıcı/boyut başına plan patlaması yok).
- 240 görev × 7 kullanıcı × 2 kiracı paralel (havuzlu fabrika, eşzamanlılık 24): sızıntı yok. Aynı kimlikli iki kiracı ayrı.
- Boş sahip kümesi ve `Deny` = 0 satır; kapsamsız katalog (`Product`) `Deny`'dan etkilenmez.

EF işlem matrisi (`Q2_EfOperationTests`, hepsi kapsamlı, iki biçimde de):

| İşlem | Sonuç |
|---|---|
| `AsNoTracking`, izleyen, projeksiyon (`Select`), `Count`/`LongCount`/`Sum`/`Any`, `GroupBy` | filtreli; görünmeyen sahibin kaydı `Any`/`Count` ile bile sezilemez |
| `Include(alt koleksiyon)`, `a.Contacts.Count` projeksiyonu, açık yükleme (`Entry.Collection.Query()`) | alt kayıtlara da uygulanır → **sayaçlar yalnız görünür alt kayıtları sayar** |
| `AsSplitQuery` | 2 SQL'in ikisi de kapsam taşır |
| `ExecuteUpdate` / `ExecuteDelete` | yalnız görünür satırlar etkilenir (başkası, silinmiş, başka kiracı dokunulmaz) |
| `FromSql($"SELECT * …")` | filtreler ham kökün **üstüne** sarılır (`FROM (raw) AS s WHERE tenant… AND scope…`); `IgnoreRecordScope()` çalışır |
| `Database.SqlQuery<T>` / `ExecuteSql` | **filtre yok** (beklenen; mevcut ham SQL envanteri yakalar) |
| Alt kayıt `HasRecordScopeThroughParent` | `EXISTS (SELECT 1 FROM cases … AND <üstün Tenant+SoftDelete+RecordScope filtresi> AND c0.id = c.case_id)`; üstün filtresi iç sorguda **özyinelemeli uygulanır** → D7(c) başarılı, `IParentGuard` yedeği **gerekmez** |
| Çift yol + havuz (`assigned OR created`, atanmamış=herkes) | 4 senaryo (X, R3, R1+havuz, M2+R3) doğru. EF, nullable `AssignedUserId` için ek `array_position(@p, NULL) IS NOT NULL` koşulu üretiyor (zararsız) |
| `DbSet.Find/FindAsync` | izlenmeyen kayıtta filtreden geçer (null); **ama `IgnoreRecordScope` ile bağlama yüklenmiş izlenen kayıt `Find` ile döner** (kimlik haritası sızıntısı). Repoda bugün `Find` kullanımı **yok** → yasaklayıcı mimari test öneriliyor |

Ölçülmedi: `Include` + "required navigation + query filter" EF uyarısı (10622) davranışı; M9D `SelfPredicate` (katılımcı `Any`).

### Q3 — Bağlam kurulmadığında (Confirmed)
`Q3_FailClosedContextTests` ve `Q6` (HTTP tarafı):
- Kurulmamış bağlam, varsayılan kipte **sınırsız** (plan D8'in "HTTP dışı" davranışı); `FailClosedWhenUnset=true` iken **0 satır**; açık `UseSystem()` iki kipte de sınırsız ve `Dispose` sonrası önceki kapsama **döner** (iç içe geçişler doğru).
- AsyncLocal `await` ve `Task.Run`'a akar; kardeş `Task.WhenAll` görevleri birbirine ve ana akışa sızmaz.
- `ExecutionContext.SuppressFlow` kapsamı **kaybettirir**: varsayılan kipte sonuç 14/14 (tümü) = **fail-open**; sıkı kipte 0. İstekten başlatılan fire-and-forget görev, istek bitince de **eski kullanıcının (dar) kapsamını** taşır (bayat ama genişlemez).
- HTTP (gerçek host): çözümü olmayan kullanıcı `Deny` → liste 0, GET 404; çözümleyici arızası simülasyonu → 503, asla sınırsız.
- **Öneri:** API sürecinde `RecordScopeContext.FailClosedWhenUnset = true`, Worker/Migrator'da `false`. Bunun bedeli: API sürecinde kapsamlı tabloya dokunan her HTTP-dışı yol açık `UseSystem` ister. Depoda `BeginScope(` çağrısı ~25, `UseSystem()` ~9 yerde; kapsamlı tabloya dokunanlar zaten `UseSystem` kullanıyor (outbox, `InProcessEventBus`, webhook, Conductor, tohumlayıcılar). **İstisna, gerçek bir delik:** `EntitlementServices` `IUsageMeter.CollectAsync` yalnız `BeginScope(tenantId)` yapar ve 8 modülün `IUsageReporter`'ı (`db.Deals.LongCount…`) **HTTP isteği içinde** (`GET /subscription`, `TenantUseCases`) çağrılır → RecordScope açıkken kullanıcının **dar** sayısını döndürür, plan/limit kullanımı **eksik sayılır**. Çözüm tek yerde: `CollectAsync` içinde `RecordScopeContext.UseSystem()`. Plan D10/envanterinde yok; eklenmeli.

### Q4 — Postgres performansı (Confirmed, caveat)
Gerçek `sales.deals` (gerçek migration, gerçek 6 indeks: `(tenant_id, owner_user_id)`, `(tenant_id, created_at)`, `(tenant_id, stage_id)` …), 300k satır (T1 200k + gürültü kiracıları 60k/40k), 250 sahip düzgün dağılımlı, %2 silinmiş. EF uçtan uca (25 satırlık liste, 40 koşu): p50 / p95 ms.

| Senaryo | liste25 | liste25+aşama | COUNT (satır) | detay (görünür) | detay (görünmez) |
|---|---|---|---|---|---|
| Taban çizgisi (bugünkü SQL) | 3,0 / 4,6 | 3,2 / 13,4 | 46 / 87 (195 745) | 3,2 / 5,8 | 3,1 / 7,2 |
| ReadAll (yönetici) filtreden | 5,7 / 43,6 | 4,8 / 9,7 | 34 / 41 | 4,3 / 7,2 | 4,3 / 8,1 |
| own (0 ast, 1 sahip) | 5,9 / 16,8 | 8,6 / 24,2 | 4,2 / 6,5 (783) | 2,6 / 4,1 | 2,7 / 6,6 (0 satır) |
| 5 ast (6 sahip) | 3,6 / 6,2 | 7,0 / 22,3 | 19 / 60 (4 698) | 6,2 / 25,8 | 3,8 / 11,5 |
| 200 ast (201 sahip) | 3,4 / 5,1 | 3,6 / 10,5 | 59 / 102 (157 379) | 3,9 / 15,0 | 3,2 / 6,1 |

- Plan eşikleri (own liste p95 < 150 ms, 201 sahip liste p95 < 400 ms) 200k satırda **çok altında** (testte doğrulanıyor).
- İndeks kullanımı: `own`/5 ast COUNT → `ix_deals_tenant_id_owner_user_id`; liste → `ix_deals_tenant_id_created_at` geri tarama + `Incremental Sort` (sahip koşulu süzgeç) ya da aşamalı listede owner indeksi + `Bitmap` + top-N sort; detay → `pk_deals` (4 sayfa, 0,07–0,13 ms; görünmez kayıt için de aynı → zamanlama farkı yok). ReadAll planında sahip koşulu yok.
- COUNT süresi filtreden değil **eşleşen satır sayısından** gelir (201 sahipte 157k satır ≈ taban çizgisinin 195k satırı).
- İndeks önerisi `(tenant_id, owner_user_id, created_at DESC, id)`: `= ANY` + `ORDER BY` için planlayıcı bu indeksi sıralama için **kullanmadı** (plan aynı kaldı; p50 farkları gürültü) → **eklenmesin**; mevcut `(tenant_id, owner_user_id)` yeterli. (Service/Activities `created_user_id` yolu bu spike'ta ölçülmedi; indeks planda zaten önerilmiş.)
- **Seyrek / sıfır satırlı sahip riski** (`perf-sparse-owner.md`): sahibin en eski 20 kaydı ya da hiç kaydı yoksa (yeni işe girmiş) `ORDER BY created_at DESC LIMIT 25` sorgusunda planlayıcı yine `created_at` indeksini geri tarayıp süzüyor → tüm kiracı taranıyor: needle ≈ 45 ms plan süresi (p50 wall 67 ms), sıfır satırlı sahip ≈ 44 ms (200k satırda; **satırla doğrusal** → 1M'da ≈ 220 ms, planın 400 ms eşiğine yakın). `ALTER TABLE … ALTER COLUMN owner_user_id SET STATISTICS 1000` (250 sahibin tümü MCV'de) planı `owner` indeksi + top-N sort'a çeviriyor: **0,14 / 0,09 ms**. Çok kiracılı tabloda MCV tavanı (10 000) sahip sayısına yetmeyebilir → H1'de pilot verisiyle yeniden ölç; alternatif `CREATE STATISTICS … ON tenant_id, owner_user_id` (ölçülmedi).
- `= ANY(uuid[])` vs alternatifler (`perf-transport.md`; liste25 wall p50, 6 / 201 / 2 001 / 20 001 sahip; A=ANY, B=JOIN unnest, C=IN(SELECT unnest), D=kalıcı özet tablo, E=sabit IN listesi, F=istek başına TEMP tablo):

| sahip | A ANY | B JOIN unnest | C IN(SELECT) | D kalıcı tablo | E literal IN | F TEMP |
|---|---|---|---|---|---|---|
| 6 | 1,7 | 2,0 | 2,5 | 5,6 | 2,0 | 14,7 |
| 201 | 9,4 (p95 19) | 2,0 | 2,5 | 5,9 | 2,8 | 11,9 |
| 2 001 | 5,2 | 6,5 | 8,4 | 15,0 | 12,4 | 20,7 |
| 20 001 | 21,3 (p95 45) | 19,7 | **244** | 2,0 | — | 65,5 |

  201 sahipte A'nın 9,4 ms'si tek koşu gürültüsü (plan süresi 0,09 ms); farklar 2001'e kadar küçük. COUNT'ta `B JOIN unnest` 2 001+ sahipte 3× yavaş. Kalıcı özet tablosu yalnız 20k sahipte belirgin kazanç; ama kullanıcı başına satır bakımı ve kiracı bazlı geçersiz kılma getirir → **tavan (`MaxOwnerSetSize`) altında gerekmez**. EF `Constant` çevirisi (E) SQL metnini kullanıcıya göre değiştirir (plan patlaması) ve zaten seçilmemeli.

### Q5 — Hiyerarşi: ast kümesi (Confirmed)
250 kullanıcı/derinlik 8; 10 000 kullanıcı/derinlik 10; 10 000 kullanıcı düz (kökün 9 999 doğrudan astı). wall p50 ms:

| Şekil / kimin astları | Özyinelemeli CTE (her istek) | Closure tablosu | Path (`LIKE`) | Bellek grafiği + BFS |
|---|---|---|---|---|
| 250, kök (250) | 26,5 | 35,7 | 18,8 | 8 µs |
| 10k, kök (10 000) | **50,5** | 19,9 | 13,6 | 331 µs |
| 10k, orta (448) | 3,4 | 1,7 | 6,2 | 8 µs |
| 10k düz, kök (10 000) | 22,8 | 7,4 | 11,0 | 75 µs |

- Bellek yolu: kiracının tüm kenarlarını yükleme (önbellek ıskası) 250 üye 5 ms, 10 000 üye 9–19 ms; **tüm kullanıcıların** alt kümesini tek geçişte hesaplamak 10 000 üyede 19 ms. Yani plan D11 (kiracı grafiği önbellekte, BFS) ~1000× hızlı ve ek tablo istemez.
- Yeniden bağlama (448 üyelik alt ağaç taşıma): bitişiklik + döngü denetimi CTE'si 14 ms; closure yeniden yazımı 24 ms (+ 84 548 satırlık tablo, kurulum 2,1 s); path yeniden yazımı 45 ms. Closure/path yazma büyütür ve bakım ister, okuma kazancı önbellek varken anlamsız.
- **Öneri:** `manager_user_id` + önbellekteki grafik + BFS (plan D11). Özyinelemeli CTE yalnız **yazma doğrulamasında** (`up` zinciri ile döngü/derinlik, derinlik sınırı 12 ile bağlı) ve önbellek ıskası yedeğinde. Closure/path **eklenmesin**.

### Q6 — IDOR ve hata boru hattı (Confirmed, caveats)
Gerçek `CrmApiFactory` host'u, ürün kodu değişmeden (`ConfigureDbContext<SalesDbContext>` + `ReplaceService<IModelCustomizer>` ile filtre; `IStartupFilter` ile `RecordScopeMiddleware` eşdeğeri; JWT `sub` → anlık görüntü). `Q6_IdorHostTests`:
- Üye kendi fırsatını görür: liste `totalCount=1`, `?ownerUserId=<yönetici>` → 0, kanban panosunda yönetici kaydı yok, `/accounts` 1; yönetici 2.
- `GET/PUT/DELETE /deals/{gizli}` ve `GET/DELETE /accounts/{gizli}` → **404 `not_found`**, mevcut handler'lar/depolar hiç değişmeden (filtre `FirstOrDefaultAsync` yolunu zaten kapsıyor). Gövde ve başlıklar, hiç var olmayan kimlikle **bayt bayt aynı**, şu farklar normalize edilince: yanıt `instance` alanındaki istenen kimlik, `traceId`, `X-Request-ID`, `X-Trace-Id`. **Plan testi 2 bu normalizasyonu açıkça yazmalı** (aksi hâlde "bayt bayt" kırmızı olur). Kayıtlar sonrasında değişmemiş.
- Görünmeyen firmaya fırsat açma → 404 (kod `not_found`; plan D9/uyum tablosu `sales.related_not_found` diyor → **kodu netleştirin**).
- Yönetici fırsatı üyeye atar, firma yöneticide kalır: üyenin listesi 200 döner ama `accountName` **`""`** (boş) → planın `***` kuralı (L2) bir **kod değişikliği** ister (`SalesReadStore.AccountNamesAsync` eksik anahtar için `***`).
- Yazma kapsamı dışı (organization/own kullanıcı, `PUT` görünür kayda): spike `SaveChangesInterceptor` (özgün sahibi `entry.OriginalValues["OwnerUserId"]` ile denetler) `RecordReadOnlyException` fırlatır; **mevcut `GlobalExceptionHandler` bunu 500 `general.internal_error`'a çevirir**. Handler önüne tek kollu bir eşleme (`RecordReadOnlyException => 403 record.read_only`, `args.resource`) → **403 problem+json**, satır değişmez, kendi kaydını yazabilir. Gerçek kartta: `GlobalExceptionHandler.Map`'e 1 kol + tr/en resx (`ErrorResourceParityTests` uyumlu).

### 3.4 Ürün yaması doğrulaması (geçici, geri alındı)
Yeni `Shared.Infrastructure/Persistence/RecordScope/RecordScope.cs` (~97 satır: anlık görüntü, bağlam, `HasRecordScope`, `IgnoreRecordScope`) + `ModuleDbContext` 7 satır + `SalesDbContext`'te 4 `HasRecordScope` çağrısı (deal/account/contact/lead) + envanter testinde +1 dosya. Sonuç (`0001-….patch`, +112 satır): derleme uyarısız (`TreatWarningsAsErrors`), **`Tests.TenantIsolation` 20/20, `Modules.Sales.Tests` 76/76** (filtre kayıtlı, bağlam kurulmamış → sınırsız → davranış aynı: plan Adım 2'nin "varsayılan açık" hedefi). `TenantFilterBypassInventoryTests`'e `IgnoreRecordScope` sarmalayıcı dosyası +1 eklenmesi gerekiyor (patch'te yapıldı; plan D10'daki "+1" doğru). `dotnet ef has-pending-model-changes` bu worktree'de koşmadı (Migrator restore edilmemiş); sorgu filtreleri EF model farkına girmez, bu ölçülmedi.

## 4. Önerilen somut mekanizma (gerçek kart için)

```csharp
// Shared.Infrastructure/Persistence/RecordScope/  (yamada: RecordScope.cs)
public static class RecordScopeContext {                       // TenantContext deseni: AsyncLocal
    public static bool FailClosedWhenUnset { get; set; }       // Api: true, Worker/Migrator: false
    public static IDisposable Use(RecordScopeSnapshot s);  public static IDisposable UseSystem();
    // Filtre giriş noktaları: İLK ARGÜMAN DbContext ZORUNLU (yoksa EF statik sonucu önbelleğe gömer!)
    public static bool   ReadAll(DbContext ctx, string resource);
    public static Guid[] ReadOwners(DbContext ctx, string resource);
    public static bool   IncludeUnowned(DbContext ctx, string resource);
}
// Kaynak yapılandırması (modül başına tek satır)
b.HasRecordScope("lead", x => x.OwnerUserId);                              // tek yol
b.HasRecordScope("case", x => x.AssignedUserId, x => x.CreatedUserId);     // çift yol + sahipsiz havuz (spike'ta var)
b.HasRecordScopeThroughParent<CaseComment, Case>(c => c.CaseId, p => p.Id); // EXISTS (spike'ta var)
// ModuleDbContext.OnModelCreating döngüsü (Tenant/SoftDelete ile aynı yer):
if (entityType.FindAnnotation(FactoryAnnotation)?.Value is FilterFactory f)
    modelBuilder.Entity(clr).HasQueryFilter("RecordScope", f(Expression.Constant(this)));
```
Bağlama noktaları:
1. **Kapsam koşulu:** yukarıdaki döngü; filtre gövdesi `ReadAll(ctx,res) || ReadOwners(ctx,res).Contains(e.Owner)`.
2. **HTTP:** `RecordScopeMiddleware`, `Program.cs`'te `UseRequestContext()`'ten sonra (`await next` çevresinde `using RecordScopeContext.Use(...)`); kimliksiz = `Deny`; çözümleyici hatası = 503.
3. **Sistem bağlamı:** `CurrentUserAccessor.UseSystem()` (9 çağrı yeri) `RecordScopeContext.UseSystem()`'i de kurmalı (tek yerden değişir); Worker/Migrator kurulmamış = sınırsız; API sürecinde `FailClosedWhenUnset=true`.
4. **Yazma savunması:** `RecordScopeWriteInterceptor` (`SaveChangesInterceptor`, `OriginalValues` ile özgün sahip) + `GlobalExceptionHandler.Map` 1 kol.
5. **IDOR:** ek kod yok (repository `FirstOrDefaultAsync` + mevcut `not_found`).
6. **`IUsageMeter.CollectAsync`:** `UseSystem` içine al.
7. **Mimari testler:** (a) her kapsam filtresi ifadesi `DbContext` argümanı içerir (spike'taki "zehir sondası" testi kalıcı regresyon olarak: iki kullanıcı, aynı sorgu şekli); (b) `Find/FindAsync` kapsamlı DbSet'te yasak; (c) `IgnoreQueryFilters(` parametresizinin envanterde ayrı sayılması.

## 5. Plan değişiklikleri (`docs/plan/m9h-erisim.md`)

| Yer | Değişiklik |
|---|---|
| D7 metni ("`RecordScopeContext` üzerinden sorgu parametresi … `e => scope.ReadAll || scope.ReadOwners.Contains(...)`") | İfadenin **DbContext örneği içermesi şart**; statik çağrı yasak (ölçülmüş sızıntı). §4'teki imza yazılsın. Spike (a)(b)(c) "başarılı" olarak kapatılsın; `IParentGuard` yedeği silinsin. |
| D8 | "Kurulmamış = sınırsız" yanına `FailClosedWhenUnset` (API=true) ve `UseSystem` bağlantısı; AsyncLocal kaybı riski (§Q3) ve fire-and-forget bayatlığı belgelensin. |
| D10 / envanter | `IUsageMeter.CollectAsync` (8 `IUsageReporter`) için `UseSystem`; `Find/FindAsync` yasağı; parametresiz `IgnoreQueryFilters()` sayacı; `Database.SqlQuery/ExecuteSql` zaten ham SQL envanterinde. |
| D11 | `= ANY(uuid[])` doğrulandı (6–20 001 sahipte tek SQL metni, sabit EF önbelleği, ms). `MaxOwnerSetSize=20 000` kalabilir; ≥5 000'de gecikme artışı beklenmeli. **Yeni migration maddesi:** sahip sütunları için `SET STATISTICS 1000` (seyrek/sıfır satırlı sahip planı); composite `(tenant, owner, created_at)` indeksi eklenmesin. Hiyerarşi: bellek grafiği + BFS; CTE yalnız yazma doğrulaması. |
| D9 / Testler §2 | 404 gövde karşılaştırması `instance` içindeki kimliği, `traceId`, `X-Request-ID`, `X-Trace-Id`'yi normalize etsin; `record.read_only` eşlemesi `GlobalExceptionHandler.Map` kolu olarak yazılsın (bugün 500); "ilişkili kayıt yok" kodu (`sales.related_not_found` mı `not_found` mı) netleşsin. |
| D14/L2 | Görünür kayıtta görünmez ilişkili adın `***` olması `SalesReadStore.AccountNamesAsync`'te (ve benzerlerinde) kod değişikliğidir; bugün boş metin döner. |
| Uyum (M7) | `TenantFilterBypassInventoryTests`'e `IgnoreRecordScope` dosyası +1 (doğrulandı). |

## 6. Risk kaydı

| # | Risk | Ciddiyet | Durum / azaltma |
|---|---|---|---|
| R1 | Statik çağrılı filtre önbelleğe kullanıcı kapsamı gömer | Kritik | Ölçüldü (sızıntı); ifade `DbContext` içermeli + kalıcı mimari test |
| R2 | `IUsageMeter` istek içinde kapsamlı sayar → plan limiti eksik sayılır | Yüksek | `CollectAsync` `UseSystem` |
| R3 | AsyncLocal kaybı (`SuppressFlow`, kendi iş parçacığı) varsayılan kipte fail-open | Yüksek | API'de `FailClosedWhenUnset=true`; açık `UseSystem` |
| R4 | Seyrek/sıfır satırlı sahipte yanlış plan (kiracı boyutunda tarama) | Orta | `SET STATISTICS 1000`, pilot verisiyle ölç |
| R5 | `Find/FindAsync` izlenen gizli kaydı döndürür | Orta | Yasaklayıcı mimari test (bugün 0 kullanım) |
| R6 | Parametresiz `IgnoreQueryFilters()` yeni filtreyi kapatır | Orta | Envanter (Workflows tek yer, sistem bağlamı) |
| R7 | `record.read_only` eşlemesi yoksa 500 | Orta | `GlobalExceptionHandler.Map` 1 kol |
| R8 | Yazım hatalı filtre adı sessiz | Düşük | `const` ad (güvenli yönde başarısız) |
| R9 | `Include` + gerekli gezinme + filtre uyarısı (10622) ölçülmedi | Düşük | H1'de log yakalamalı test |
| R10 | Ölçümler gürültülü paylaşımlı makinede; 200k satır, 250 sahip, düzgün dağılım | Bilgi | Plan §11 perf testi (1M/10k) H1 sonunda gerçek dağılımla |

## 7. H1..H3 dilim kırılımı (teslimat planı v0.7)

Spike planı olumlu doğruladığı için dilimler aynı kalır; yalnız içerik netleşti.
- **H1 (yönetici hattı + kayıt görünürlüğü)** iki PR'a bölünsün: **H1a** — `RecordScope` altyapısı (§4 yaması), `Contracts` tipleri, Sales kayıtları, `RecordScopeMiddleware`, `UseSystem` bağlantısı, `IUsageMeter` düzeltmesi, mimari/envanter testleri, `FailClosedWhenUnset`; **davranış değişmez** (yama Sales 76/76 ile doğrulandı). **H1b** — Identity `AddAccessModel` (yönetici sütunu, `access_settings`, `role_access`), çözümleyici (önbellek + BFS), hiyerarşi/kapsam uçları, Commerce/Service (çift yol + havuz)/Marketing/Activities kayıtları, `RecordScopeWriteInterceptor` + `Map` kolu, sahip sütunları `STATISTICS` migration'ı, kapsam matrisi testleri. Ekip + paylaşım kuralları (plan dilimi C) H1b'den sonra ayrı PR; kesme çizgisinde ilk kesilen (E→D→C sırası korunur).
- **H2 (alan izinleri):** değişmedi (plan dilimi D); IDOR/oracle testleri gerçek host üzerinde §Q6 yöntemiyle.
- **H3 (giriş geçmişi + oturumlar):** spike ile bağı yok; H1'den bağımsız erkene alınabilir.

## 8. Spike içeriği (dosyalar)
- `Core/` — `RecordScopeContext`, anlık görüntü, `HasRecordScope*` (3 filtre biçimi A/B/C).
- `Model/SpikeModel.cs` — gerçek `ModuleDbContext` üstüne 3 bağlam (A/B/C), varlıklar (fırsat, firma+kişi, talep+yorum, katalog).
- `Perf/` — `PerfDb` (gerçek `SalesDbContext` + `IModelCustomizer`, sunucu tarafı tohumlama), `Q4_*`, `Q5_HierarchyTests`.
- `Tests/` — `Q1` (adlı filtreler), `Q2_*` (parametreleştirme, EF işlemleri, çürütülen statik biçim), `Q3` (bağlam), `Q6` (gerçek host IDOR/403).
- `evidence/` — SQL/EXPLAIN/tablo çıktıları; `patches/` — ürün yaması.
