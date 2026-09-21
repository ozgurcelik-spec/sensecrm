namespace Sense.Crm.Shared.Contracts.Retention;

/// <summary>İmha adımı sonucu: tablo (veya mantıksal ad) → silinen satır sayısı. Kişisel veri içermez.</summary>
public sealed record EraseReport(IReadOnlyDictionary<string, long> DeletedRows)
{
    public static EraseReport Empty { get; } = new(new Dictionary<string, long>());

    public long Total => DeletedRows.Values.Sum();
}

/// <summary>
/// İmha sonrası doğrulama sonucu (<see cref="ITenantDataEraser.VerifyErasedAsync"/>): <see cref="Problems"/> boşsa adım kiracıya ait hiçbir kalıntı bırakmamıştır.
/// Kalıntı tanımları <b>kişisel veri içermez</b> (yalnız tablo/depo adı ve sayı; ör. <c>sales.leads=3</c>).
/// </summary>
public sealed record EraseVerification(IReadOnlyList<string> Problems)
{
    public static EraseVerification Clean { get; } = new([]);

    public bool IsClean => Problems.Count == 0;

    public static EraseVerification Failed(params string[] problems) => new(problems);
}

/// <summary>
/// Kiracı verisinin kalıcı imhası (KVKK). Adımlar <see cref="Order"/> sırasıyla çalışır; her adım <b>idempotent</b> ve yeniden
/// başlatılabilirdir. Her modülün DbContext'i için genel bir uygulama <c>AddModuleDbContext</c> ile otomatik kaydedilir.
/// <para>
/// <b>Adım kaydı (C-SEC2 M2):</b> veritabanı dışı depolar (nesne depolama, webhook/bildirim tabloları vb.) da bu arayüzü uygulayıp kaydolarak imhaya katılır;
/// <see cref="TenantErasureSteps"/> adları benzersizliğini doğrular ve sıralar. İsteğe bağlı <see cref="VerifyErasedAsync"/> kancası, tombstone yazılmadan önce
/// adımın kendi deposunda kalıntı olup olmadığını denetler (kalıntı varsa talep <c>failed</c> kalır ve tombstone yazılmaz). Varsayılan: doğrulanacak bir şey yok.
/// </para>
/// </summary>
public interface ITenantDataEraser
{
    /// <summary>Kararlı adım adı (<c>erased_steps</c>'te saklanır).</summary>
    string Name { get; }

    int Order { get; }

    Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default);

    /// <summary>İmha sonrası doğrulama kancası (varsayılan: temiz). Salt okunur olmalı, kişisel veri döndürmemelidir.</summary>
    Task<EraseVerification> VerifyErasedAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(EraseVerification.Clean);
}

/// <summary>İmha adımı kaydı: kayıtlı <see cref="ITenantDataEraser"/>'ları <c>Order</c>, sonra <c>Name</c> sırasıyla döner; yinelenen ad yapılandırma hatasıdır.</summary>
public static class TenantErasureSteps
{
    public static IReadOnlyList<ITenantDataEraser> Resolve(IEnumerable<ITenantDataEraser> erasers)
    {
        ArgumentNullException.ThrowIfNull(erasers);
        // Aynı uygulamanın birden çok kaydı (bir kompozisyon kökünün iki kez çağrılması) tek adım sayılır; farklı uygulamaların aynı adı kullanması hatadır.
        var unique = erasers.GroupBy(e => (e.Name, e.GetType())).Select(g => g.First()).ToList();
        var duplicate = unique.GroupBy(e => e.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Tenant erasure step name '{duplicate.Key}' is used by more than one eraser implementation.");
        }

        return unique.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }
}
