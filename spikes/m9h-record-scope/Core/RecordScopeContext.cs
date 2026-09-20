using System.Collections.Frozen;

namespace Sense.Crm.Spikes.M9h.Core;

/// <summary>Bir kaynak (ör. "deal") için kullanıcının okuma/yazma kümesi. Diziler paylaşılan başvurudur (kullanıcı başına bir kez üretilir).</summary>
public sealed record ResourceScope(bool ReadAll, Guid[] ReadOwners, bool WriteAll, Guid[] WriteOwners, bool IncludeUnowned)
{
    public static readonly ResourceScope None = new(false, [], false, [], false);

    public static ResourceScope Own(Guid user) => new(false, [user], false, [user], false);

    public static ResourceScope OwnAnd(IEnumerable<Guid> owners, bool includeUnowned = false)
    {
        var arr = owners.ToArray();
        return new ResourceScope(false, arr, false, arr, includeUnowned);
    }

    public static readonly ResourceScope All = new(true, [], true, [], true);
}

/// <summary>(kullanıcı, kiracı) için kaynak başına kapsam anlık görüntüsü (plan D11).</summary>
public sealed class RecordScopeSnapshot(Guid userId, bool unrestricted, IDictionary<string, ResourceScope> resources)
{
    private readonly FrozenDictionary<string, ResourceScope> _resources = resources.ToFrozenDictionary(StringComparer.Ordinal);

    public Guid UserId { get; } = userId;

    public bool Unrestricted { get; } = unrestricted;

    /// <summary>Bilinmeyen kaynak = hiçbir şey (kapalı-başarısız).</summary>
    public ResourceScope Get(string resource) => Unrestricted ? ResourceScope.All : _resources.GetValueOrDefault(resource, ResourceScope.None);

    public static readonly RecordScopeSnapshot System = new(Guid.Empty, true, new Dictionary<string, ResourceScope>());

    /// <summary>Kimliksiz/anonim: hiçbir kaynakta hiçbir kayıt.</summary>
    public static readonly RecordScopeSnapshot Deny = new(Guid.Empty, false, new Dictionary<string, ResourceScope>());
}

/// <summary>
/// <c>TenantContext</c> deseninde AsyncLocal bağlam (plan D7/D8). Filtre ifadeleri bu statik üyeleri çağırır; EF bunları her yürütmede
/// sorgu PARAMETRESİ olarak değerlendirir (bkz. Q1/Q2 testleri). <see cref="EvaluationCount"/> yalnız spike ölçümü içindir.
/// </summary>
public static class RecordScopeContext
{
    private static readonly AsyncLocal<RecordScopeSnapshot?> Current = new();
    private static long _evaluations;

    /// <summary>
    /// true: bağlam kurulmamışsa (Unset) HİÇBİR ŞEY döner (API sürecinde önerilen kapalı-başarısız kip);
    /// false: sınırsız (plan D8 "HTTP dışı"). Q3 iki kipi de doğrular.
    /// </summary>
    public static bool FailClosedWhenUnset { get; set; }

    public static long EvaluationCount => Interlocked.Read(ref _evaluations);

    public static bool IsSet => Current.Value is not null;

    private static RecordScopeSnapshot Effective => Current.Value ?? (FailClosedWhenUnset ? RecordScopeSnapshot.Deny : RecordScopeSnapshot.System);

    public static IDisposable Use(RecordScopeSnapshot snapshot)
    {
        var previous = Current.Value;
        Current.Value = snapshot;
        return new Restorer(previous);
    }

    public static IDisposable UseSystem() => Use(RecordScopeSnapshot.System);

    // ---- Filtre ifadelerinin çağırdığı giriş noktaları (EF bunları yürütme başına değerlendirir) ----
    public static bool ReadAll(string resource)
    {
        Interlocked.Increment(ref _evaluations);
        return Effective.Get(resource).ReadAll;
    }

    public static Guid[] ReadOwners(string resource) => Effective.Get(resource).ReadOwners;

    public static bool IncludeUnowned(string resource) => Effective.Get(resource).IncludeUnowned;

    // Stil C: ilk argüman DbContext örneği. EF bu argümanı "bağlam bağımlı" sayıp çağrının tamamını YÜRÜTME başına değerlendirir (Q2 testi).
    public static bool ReadAll(Microsoft.EntityFrameworkCore.DbContext context, string resource) => ReadAll(resource);

    public static Guid[] ReadOwners(Microsoft.EntityFrameworkCore.DbContext context, string resource) => ReadOwners(resource);

    public static bool IncludeUnowned(Microsoft.EntityFrameworkCore.DbContext context, string resource) => IncludeUnowned(resource);

    public static RecordScopeSnapshot Snapshot => Effective;

    private sealed class Restorer(RecordScopeSnapshot? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Örnek üzerinden (DbContext üyesi) erişim: mevcut "Tenant" filtresi <c>this.CurrentTenantId</c> kalıbıyla aynı (seçenek B).</summary>
public sealed class RecordScopeView
{
    public bool ReadAll(string resource) => RecordScopeContext.ReadAll(resource);

    public Guid[] ReadOwners(string resource) => RecordScopeContext.ReadOwners(resource);

    public bool IncludeUnowned(string resource) => RecordScopeContext.IncludeUnowned(resource);
}

/// <summary>Interceptor'ın (SaveChanges) fırlattığı: görünür ama yazma kapsamı dışı (plan D9).</summary>
public sealed class RecordReadOnlyException(string resource) : Exception($"record.read_only:{resource}")
{
    public string Resource { get; } = resource;
}
