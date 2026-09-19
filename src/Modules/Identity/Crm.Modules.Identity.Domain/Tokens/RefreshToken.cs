using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;

namespace Crm.Modules.Identity.Domain.Tokens;

/// <summary>
/// Dönen (rotating) refresh token. Her kullanımda yenisi verilir, eskisi ReplacedByTokenHash ile işaretlenir (atomik koşullu
/// güncelleme: <see cref="IRefreshTokenRepository.TryRotateAsync"/>). İptal edilmiş bir token yeniden kullanılırsa (kısa bir
/// eşzamanlılık toleransı dışında) aynı FamilyId'deki tüm token'lar iptal edilir (reuse detection).
/// Bir ailenin (oturumun) ömrü <b>mutlaktır</b> (<see cref="FamilyExpiresAt"/>, ilk verilişten itibaren); dönüşümle uzamaz.
/// Kullanıcı hesabı küresel olduğundan token da küreseldir; hangi organizasyon bağlamında verildiği
/// <see cref="OrganizationId"/>'de tutulur (kiracı filtresine tabi değildir, yalnız hash ile bulunur).
/// </summary>
public sealed class RefreshToken : Entity<Guid>
{
    private RefreshToken()
    {
    }

    private RefreshToken(Guid id, Guid userId, Guid organizationId, Guid familyId, string tokenHash, DateTime expiresAt, DateTime familyExpiresAt, string? deviceInfo, string? ip)
        : base(id)
    {
        UserId = userId;
        OrganizationId = organizationId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        FamilyExpiresAt = familyExpiresAt;
        DeviceInfo = deviceInfo;
        IpAddress = ip;
    }

    public Guid UserId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Ailenin mutlak son kullanma anı (ilk token'ın verilişi + aile ömrü); dönüşümde aynen devralınır.</summary>
    public DateTime FamilyExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public string? ReplacedByTokenHash { get; private set; }

    public string? DeviceInfo { get; private set; }

    public string? IpAddress { get; private set; }

    public bool IsActive(DateTime nowUtc) => RevokedAt is null && ExpiresAt > nowUtc && FamilyExpiresAt > nowUtc;

    /// <summary>Dönüşümle (yenisi verilerek) iptal edilmiş mi; çıkış/iptal ile kapanan token için false.</summary>
    public bool IsRotated => ReplacedByTokenHash is not null;

    /// <summary>
    /// Yeni token. <paramref name="familyId"/> ve <paramref name="familyExpiresAt"/> verilirse aynı aile (dönüşüm) devam eder ve
    /// ömür mutlak sınırla kırpılır; verilmezse yeni aile açılır (mutlak ömür = <paramref name="familyLifetime"/>).
    /// </summary>
    public static RefreshToken Issue(
        Guid userId,
        Guid organizationId,
        Guid? familyId,
        DateTime? familyExpiresAt,
        string tokenHash,
        DateTime nowUtc,
        TimeSpan lifetime,
        TimeSpan familyLifetime,
        string? deviceInfo,
        string? ip)
    {
        var familyEnd = familyExpiresAt ?? nowUtc.Add(familyLifetime);
        var expires = nowUtc.Add(lifetime);
        return new(Guid.CreateVersion7(), userId, organizationId, familyId ?? Guid.CreateVersion7(), Guard.NotEmpty(tokenHash), expires < familyEnd ? expires : familyEnd, familyEnd,
            deviceInfo is null ? null : Truncate(deviceInfo, IdentityLimits.DeviceInfoMaxLength), ip);
    }

    public void Revoke(DateTime nowUtc) => RevokedAt ??= nowUtc;

    /// <summary>İsteği yapan istemci, token'ın verildiği istemciyle aynı mı (IP + kullanıcı aracısı); eşzamanlı yenileme toleransı için.</summary>
    public bool IsSameClient(string? deviceInfo, string? ip) =>
        string.Equals(IpAddress, ip, StringComparison.Ordinal)
        && string.Equals(DeviceInfo, deviceInfo is null ? null : Truncate(deviceInfo, IdentityLimits.DeviceInfoMaxLength), StringComparison.Ordinal);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
