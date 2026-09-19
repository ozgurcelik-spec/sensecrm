namespace Sense.Crm.Shared.Contracts.Context;

/// <summary>İstek/iş kapsamındaki kiracı (organizasyon, K1). JWT `tid` claim'inden veya outbox/job argümanından çözülür.</summary>
public interface ITenantContext
{
    /// <summary>Çözülmemişse <see cref="InvalidOperationException"/> fırlatır.</summary>
    Guid TenantId { get; }

    bool IsResolved { get; }

    string? TenantSlug { get; }
}

/// <summary>Kiracı bağlamını job/outbox işleyicileri için elle kurar.</summary>
public interface ITenantContextSetter
{
    IDisposable BeginScope(Guid tenantId, string? slug = null);
}

/// <summary>İstek yapan kullanıcı.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? Email { get; }

    string? DisplayName { get; }

    IReadOnlySet<string> Roles { get; }

    /// <summary>Platform (ürün ekibi) hesabı işareti; bugün yalnız bilgi amaçlı, kiracı verisine ek erişim vermez.</summary>
    bool IsPlatformAdmin { get; }

    string? CorrelationId { get; }

    string? IpAddress { get; }
}
