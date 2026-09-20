namespace Sense.Crm.Shared.Contracts.Retention;

/// <summary>İmha adımı sonucu: tablo (veya mantıksal ad) → silinen satır sayısı. Kişisel veri içermez.</summary>
public sealed record EraseReport(IReadOnlyDictionary<string, long> DeletedRows)
{
    public static EraseReport Empty { get; } = new(new Dictionary<string, long>());

    public long Total => DeletedRows.Values.Sum();
}

/// <summary>
/// Kiracı verisinin kalıcı imhası (KVKK). Adımlar <see cref="Order"/> sırasıyla çalışır; her adım <b>idempotent</b> ve yeniden
/// başlatılabilirdir. Her modülün DbContext'i için genel bir uygulama <c>AddModuleDbContext</c> ile otomatik kaydedilir.
/// </summary>
public interface ITenantDataEraser
{
    /// <summary>Kararlı adım adı (<c>erased_steps</c>'te saklanır).</summary>
    string Name { get; }

    int Order { get; }

    Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default);
}
