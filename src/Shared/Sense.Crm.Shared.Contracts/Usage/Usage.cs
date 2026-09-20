namespace Sense.Crm.Shared.Contracts.Usage;

/// <summary>Bir kullanım metriği: anahtar <c>{modül}.{varlık}</c> (modül toplamı <c>{modül}.records</c>).</summary>
public sealed record UsageMetric(string Key, long Value);

/// <summary>
/// Modülün kullanım sayaçları. <b>Yalnız kiracı kapsamında</b> çağrılır (çağıran <c>ITenantContextSetter.BeginScope</c> kurar), yalnız
/// <c>COUNT</c> yapar, kişisel veri döndürmez, iptal edilebilir. Her modülün Infrastructure'ı tam bir uygulama kaydeder
/// (<see cref="Module"/> = <c>IModule.Name</c>; mimari test zorlar).
/// </summary>
public interface IUsageReporter
{
    string Module { get; }

    Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default);
}

/// <summary>Kullanım metrik anahtarı yardımcıları.</summary>
public static class UsageKeys
{
    public const string RecordsSuffix = "records";

    public static string Records(string module) => module + "." + RecordsSuffix;
}
