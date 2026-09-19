using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Security;

namespace Crm.Shared.Infrastructure.Context;

/// <summary>
/// Kiracı bağlamı. HTTP isteğinde middleware, job/outbox işleyicilerinde <see cref="BeginScope"/> ile kurulur.
/// AsyncLocal sayesinde aynı mantıksal akıştaki tüm servisler aynı kiracıyı görür.
/// </summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    private const string NotResolvedMessage = "Tenant context is not resolved. An authenticated request or an explicit tenant scope is required.";

    private static readonly AsyncLocal<TenantScope?> Current = new();

    public Guid TenantId => Current.Value?.TenantId ?? throw new InvalidOperationException(NotResolvedMessage);

    public bool IsResolved => Current.Value is not null;

    public string? TenantSlug => Current.Value?.Slug;

    /// <summary>
    /// DI kapsamından bağımsız, statik AsyncLocal'e doğrudan erişim: Serilog enricher gibi tek seferlik
    /// (root provider'dan) oluşturulan bileşenlerin, mantıksal akışın güncel kiracısını görmesini sağlar.
    /// </summary>
    public static Guid? CurrentTenantIdOrNull => Current.Value?.TenantId;

    public IDisposable BeginScope(Guid tenantId, string? slug = null)
    {
        var previous = Current.Value;
        Current.Value = new TenantScope(tenantId, slug);
        return new ScopeRestorer(previous);
    }

    private sealed record TenantScope(Guid TenantId, string? Slug);

    private sealed class ScopeRestorer(TenantScope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Arka plan/sistem bağlamı için kullanıcı: platform yetkili, kimliksiz.</summary>
public sealed class SystemUser : ICurrentUser
{
    public static readonly SystemUser Instance = new();

    public bool IsAuthenticated => true;

    public Guid? UserId => null;

    public string? Email => null;

    public string? DisplayName => WellKnownRoles.System;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>([WellKnownRoles.System], StringComparer.Ordinal);

    public bool IsPlatformAdmin => true;

    public string? CorrelationId => null;

    public string? IpAddress => null;
}

/// <summary>Scoped olarak kayıtlı; HTTP'de middleware doldurur, job'larda SystemUser'a delege eder.</summary>
public sealed class CurrentUserAccessor : ICurrentUser
{
    private static readonly AsyncLocal<ICurrentUser?> Override = new();

    private ICurrentUser _inner = AnonymousUser.Instance;

    public void Set(ICurrentUser user) => _inner = user;

    public static IDisposable UseSystem() => Use(SystemUser.Instance);

    public static IDisposable Use(ICurrentUser user)
    {
        var previous = Override.Value;
        Override.Value = user;
        return new Restorer(previous);
    }

    private ICurrentUser Effective => Override.Value ?? _inner;

    public bool IsAuthenticated => Effective.IsAuthenticated;

    public Guid? UserId => Effective.UserId;

    public string? Email => Effective.Email;

    public string? DisplayName => Effective.DisplayName;

    public IReadOnlySet<string> Roles => Effective.Roles;

    public bool IsPlatformAdmin => Effective.IsPlatformAdmin;

    public string? CorrelationId => Effective.CorrelationId;

    public string? IpAddress => Effective.IpAddress;

    private sealed class Restorer(ICurrentUser? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}

public sealed class AnonymousUser : ICurrentUser
{
    public static readonly AnonymousUser Instance = new();

    public bool IsAuthenticated => false;

    public Guid? UserId => null;

    public string? Email => null;

    public string? DisplayName => null;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsPlatformAdmin => false;

    public string? CorrelationId => null;

    public string? IpAddress => null;
}

/// <summary>Kimliği doğrulanmış kullanıcının değişmez anlık görüntüsü (JWT claim'lerinden üretilir).</summary>
public sealed record AuthenticatedUser(
    Guid UserId,
    string? Email,
    string? DisplayName,
    IReadOnlySet<string> Roles,
    bool IsPlatformAdmin,
    string? CorrelationId,
    string? IpAddress) : ICurrentUser
{
    public bool IsAuthenticated => true;

    Guid? ICurrentUser.UserId => UserId;
}
