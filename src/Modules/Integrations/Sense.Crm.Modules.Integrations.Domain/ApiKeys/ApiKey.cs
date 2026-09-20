using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Integrations.Domain.ApiKeys;

/// <summary>
/// API anahtarı (makine istemcisi kimliği). Yalnız <b>özet</b> (<c>SHA-256(secret)</c>) saklanır; ham anahtar yalnız oluşturma yanıtında bir kez döner. Kapsam (izin anahtarları) ve bitiş
/// <b>değiştirilemez</b> (yeniden oluştur); yalnız ad/açıklama/IP listesi güncellenir. Oluşturan kullanıcıya bağlıdır (act-as-creator); etkin izin = kapsam ∩ oluşturanın
/// o anki izinleri. <c>SecretHash</c> denetimde maskelenir. Kullanım (<c>last_used_*</c>) <c>ExecuteUpdate</c> ile yazılır (denetim satırı üretmez).
/// </summary>
public sealed class ApiKey : TenantAggregateRoot<Guid>, IAuditLogged
{
    private ApiKey()
    {
    }

    private ApiKey(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public static IReadOnlySet<string> SensitiveFields { get; } = new HashSet<string>([nameof(SecretHash)], StringComparer.Ordinal);

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>8 haneli onaltılık önek (kiracıda benzersiz); arayüzde <c>crmk_&lt;prefix&gt;</c>.</summary>
    public string Prefix { get; private set; } = string.Empty;

    public byte[] SecretHash { get; private set; } = [];

    public List<string> Scopes { get; private set; } = [];

    public DateTime ExpiresAt { get; private set; }

    /// <summary>İzinli istemci ağları (CIDR); boş = kısıt yok.</summary>
    public List<string> AllowedCidrs { get; private set; } = [];

    public Guid CreatedByUserId { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public DateTime? LastUsedAt { get; private set; }

    public string? LastUsedIp { get; private set; }

    public static ApiKey Create(
        Guid id,
        Guid tenantId,
        string name,
        string? description,
        string prefix,
        byte[] secretHash,
        IEnumerable<string> scopes,
        DateTime expiresAt,
        IEnumerable<string> allowedCidrs,
        Guid createdByUserId)
    {
        Guard.Against(prefix.Length != IntegrationsLimits.ApiKeyPrefixLength, "Invalid API key prefix length.");
        Guard.Against(secretHash.Length != IntegrationsLimits.ApiKeyHashLength, "Invalid API key hash length.");
        var key = new ApiKey(id, tenantId)
        {
            Prefix = prefix,
            SecretHash = secretHash,
            Scopes = [.. scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ExpiresAt = expiresAt,
            CreatedByUserId = Guard.NotDefault(createdByUserId),
        };
        key.SetMeta(name, description, allowedCidrs);
        return key;
    }

    /// <summary>Ad, açıklama ve IP listesi (kapsam/bitiş değişmez).</summary>
    public Result UpdateMeta(string name, string? description, IEnumerable<string> allowedCidrs)
    {
        if (RevokedAt is not null)
        {
            return Error.Conflict(IntegrationsErrors.ApiKeyRevoked);
        }

        SetMeta(name, description, allowedCidrs);
        return Result.Success();
    }

    /// <summary>İptal (idempotent: zaten iptalliyse ilk iptal korunur).</summary>
    public void Revoke(Guid? userId, DateTime now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedByUserId = userId;
    }

    public bool IsRevoked => RevokedAt is not null;

    /// <summary>Süre bitişi anında (<c>now &gt;= expires_at</c>) süresi dolmuştur.</summary>
    public bool IsExpired(DateTime now) => now >= ExpiresAt;

    public bool IsActive(DateTime now) => !IsRevoked && !IsExpired(now);

    public string StatusAt(DateTime now) => IsRevoked ? ApiKeyStatuses.Revoked : IsExpired(now) ? ApiKeyStatuses.Expired : ApiKeyStatuses.Active;

    private void SetMeta(string name, string? description, IEnumerable<string> allowedCidrs)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), IntegrationsLimits.NameMaxLength);
        Description = string.IsNullOrWhiteSpace(description) ? null : Guard.MaxLength(description.Trim(), IntegrationsLimits.DescriptionMaxLength);
        AllowedCidrs = [.. allowedCidrs.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>
/// Anahtar başına günlük kullanım sayaçları (UTC günü): istek, hata (4xx-5xx), kısıtlama (429). Yalnız <c>INSERT … ON CONFLICT DO UPDATE SET n = n + @n</c> ile yazılır
/// (bellek içi toplanıp periyodik boşaltılır); kiracı kimliği daima parametredir. <c>ITenantEntity</c>: kiracı imhasında silinir.
/// </summary>
public sealed class ApiKeyUsageDay : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid ApiKeyId { get; set; }

    public DateOnly Day { get; set; }

    public int Requests { get; set; }

    public int Errors { get; set; }

    public int Throttled { get; set; }
}
