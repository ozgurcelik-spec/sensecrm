using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Roles;
using Sense.Crm.Modules.Identity.Domain.Tenants;
using Sense.Crm.Modules.Identity.Domain.Tokens;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;

namespace Sense.Crm.Modules.Identity.Application.Auth;

/// <summary>Bir kullanıcının belirli organizasyondaki oturum bağlamı: organizasyon + aktif üyeliğindeki rol.</summary>
public sealed record SessionContext(Tenant Tenant, Role Role);

/// <summary>Oturum çözümü: <paramref name="Session"/> yoksa ve <paramref name="BlockedReason"/> doluysa kiracı erişimi <c>none</c> (M7: <c>tenant.suspended</c> nedeni).</summary>
public sealed record SessionResolution(SessionContext? Session, string? BlockedReason);

/// <summary>
/// Oturum üretimi (giriş, kayıt, yenileme, organizasyon değiştirme, parola değiştirme ortak yolu): access token (15 dk, aktif
/// organizasyon `tid` claim'inde) + dönen refresh token. Refresh token ailesinin ömrü mutlaktır (M5); dönüşüm atomiktir.
/// </summary>
public sealed class SessionIssuer(
    ITenantRepository tenants,
    IMembershipRepository memberships,
    IRoleRepository roles,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    ISecretGenerator secrets,
    ITenantContextSetter tenantSetter,
    ITenantEntitlements entitlements,
    TimeProvider clock)
{
    /// <summary>
    /// Kullanıcının verilen organizasyonda aktif üyeliği ve rolü; üyelik yoksa/pasifse/bekleyen davetse, organizasyon pasifse veya kiracının erişimi
    /// <c>none</c> ise (askı <c>blocked</c>, silme bekleyen/silinmiş; M7) null. Sorgular hedef organizasyonun kiracı kapsamında (query filter altında) çalışır.
    /// </summary>
    public async Task<SessionContext?> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct) =>
        (await ResolveDetailedAsync(userId, tenantId, ct).ConfigureAwait(false)).Session;

    /// <summary>
    /// <see cref="ResolveAsync"/> + engel nedeni: kullanıcı organizasyonun aktif üyesi ama kiracı erişimi <c>none</c> ise <see cref="SessionResolution.BlockedReason"/>
    /// etkin durumu (<c>suspended | pending_deletion | deleted</c>) taşır (üye olmayan kullanıcıya kiracı durumu sızdırılmaz).
    /// </summary>
    public async Task<SessionResolution> ResolveDetailedAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive)
        {
            return new SessionResolution(null, null);
        }

        SessionContext? session;
        using (tenantSetter.BeginScope(tenant.Id, tenant.Slug))
        {
            var membership = await memberships.GetByUserAsync(userId, ct).ConfigureAwait(false);
            if (membership is null || !membership.IsActive)
            {
                return new SessionResolution(null, null);
            }

            var role = await roles.GetByIdAsync(membership.RoleId, ct).ConfigureAwait(false);
            session = role is null ? null : new SessionContext(tenant, role);
        }

        if (session is null)
        {
            return new SessionResolution(null, null);
        }

        var snapshot = await entitlements.GetAsync(tenant.Id, ct).ConfigureAwait(false);
        var (status, access) = snapshot.Evaluate(clock.GetUtcNow());
        return access == AccessLevel.None ? new SessionResolution(null, status) : new SessionResolution(session, null);
    }

    /// <summary>Yeni bir oturum (yeni token ailesi) üretir: giriş, kayıt, organizasyon değiştirme, parola değiştirme.</summary>
    public AuthResponse Issue(User user, SessionContext session, string? deviceInfo, string? ipAddress)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return Build(user, session, now, familyId: null, familyExpiresAt: null, deviceInfo, ipAddress).Response;
    }

    /// <summary>
    /// Aynı ailede devam eder: <paramref name="rotateFrom"/> token'ı <b>atomik koşullu güncellemeyle</b> iptal edilir (iki eşzamanlı
    /// yenilemeden yalnız biri kazanır) ve yenisi verilir; aile ömrü mutlak sınırda kırpılır (kayan değil). Yarışı kaybeden çağrı null döner.
    /// </summary>
    public async Task<AuthResponse?> RotateAsync(User user, SessionContext session, RefreshToken rotateFrom, string? deviceInfo, string? ipAddress, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var issued = Build(user, session, now, rotateFrom.FamilyId, rotateFrom.FamilyExpiresAt, deviceInfo, ipAddress, addToStore: false);
        if (!await refreshTokens.TryRotateAsync(rotateFrom.Id, issued.NewHash, now, ct).ConfigureAwait(false))
        {
            return null;
        }

        refreshTokens.Add(issued.Token);
        return issued.Response;
    }

    private (AuthResponse Response, RefreshToken Token, string NewHash) Build(
        User user,
        SessionContext session,
        DateTime now,
        Guid? familyId,
        DateTime? familyExpiresAt,
        string? deviceInfo,
        string? ipAddress,
        bool addToStore = true)
    {
        var access = tokens.IssueAccessToken(user, session.Tenant, session.Role);
        var rawRefresh = secrets.NewToken();
        var hash = secrets.Hash(rawRefresh);

        var refresh = RefreshToken.Issue(
            user.Id, session.Tenant.Id, familyId, familyExpiresAt, hash, now, tokens.RefreshTokenLifetime, tokens.RefreshFamilyLifetime, deviceInfo, ipAddress);
        if (addToStore)
        {
            refreshTokens.Add(refresh);
        }

        var response = new AuthResponse(access.Token, rawRefresh, new DateTimeOffset(DateTime.SpecifyKind(access.ExpiresAtUtc, DateTimeKind.Utc)), user.MustChangePassword);
        return (response, refresh, hash);
    }
}
