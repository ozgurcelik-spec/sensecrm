using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Tokens;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Context;

namespace Crm.Modules.Identity.Application.Auth;

/// <summary>Bir kullanıcının belirli organizasyondaki oturum bağlamı: organizasyon + aktif üyeliğindeki rol.</summary>
public sealed record SessionContext(Tenant Tenant, Role Role);

/// <summary>
/// Oturum üretimi (giriş, kayıt, yenileme, organizasyon değiştirme ortak yolu): access token (15 dk, aktif organizasyon
/// `tid` claim'inde) + dönen refresh token. Giriş/yenileme/kayıt/organizasyon değiştirme akışlarının ortak kuyruğu tek yerde toplanır.
/// </summary>
public sealed class SessionIssuer(
    ITenantRepository tenants,
    IMembershipRepository memberships,
    IRoleRepository roles,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    ISecretGenerator secrets,
    ITenantContextSetter tenantSetter,
    TimeProvider clock)
{
    /// <summary>
    /// Kullanıcının verilen organizasyonda aktif üyeliği ve rolü; üyelik yoksa/pasifse veya organizasyon pasifse null.
    /// Sorgular hedef organizasyonun kiracı kapsamında (query filter altında) çalışır.
    /// </summary>
    public async Task<SessionContext?> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive)
        {
            return null;
        }

        using var scope = tenantSetter.BeginScope(tenant.Id, tenant.Slug);
        var membership = await memberships.GetByUserAsync(userId, ct).ConfigureAwait(false);
        if (membership is null || !membership.IsActive)
        {
            return null;
        }

        var role = await roles.GetByIdAsync(membership.RoleId, ct).ConfigureAwait(false);
        return role is null ? null : new SessionContext(tenant, role);
    }

    /// <summary>
    /// Yeni oturum üretir. <paramref name="rotateFrom"/> verilirse aynı token ailesinde devam edilir ve eski token
    /// "yerine yenisi verildi" olarak işaretlenir (rotation); verilmezse yeni aile açılır.
    /// </summary>
    public AuthResponse Issue(User user, SessionContext session, RefreshToken? rotateFrom, string? deviceInfo, string? ipAddress)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var access = tokens.IssueAccessToken(user, session.Tenant, session.Role);

        var rawRefresh = secrets.NewToken();
        var hash = secrets.Hash(rawRefresh);
        rotateFrom?.Rotate(hash, now);

        var refresh = RefreshToken.Issue(user.Id, session.Tenant.Id, rotateFrom?.FamilyId, hash, now, tokens.RefreshTokenLifetime, deviceInfo, ipAddress);
        refreshTokens.Add(refresh);

        return new AuthResponse(access.Token, rawRefresh, new DateTimeOffset(DateTime.SpecifyKind(access.ExpiresAtUtc, DateTimeKind.Utc)));
    }
}
