using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Identity.Domain.Memberships;

/// <summary>Üyelik durumu: davet edilen mevcut hesap onaylayana kadar <see cref="Pending"/> (aktif organizasyon sayılmaz).</summary>
public enum MembershipStatus
{
    Active,
    Pending,
}

/// <summary>
/// Kullanıcı ↔ organizasyon ↔ rol bağı (K1). Kiracıya aittir: listeleme/değiştirme kiracı query filter'ı altında yapılır,
/// böylece bir organizasyonun yöneticisi başka organizasyonun üyelerini göremez. Bir kullanıcının organizasyonda en fazla
/// bir üyeliği vardır; tek rol (basit RBAC, K7). Mevcut bir hesap organizasyona <b>onayı olmadan</b> katılamaz: yönetici
/// <see cref="Invite"/> ile <see cref="MembershipStatus.Pending"/> üyelik açar, hesap sahibi kabul eder (<see cref="Accept"/>) ya da reddeder.
/// </summary>
public sealed class Membership : TenantAggregateRoot<Guid>, IAuditLogged
{
    private Membership()
    {
    }

    private Membership(Guid id, Guid tenantId, Guid userId, Guid roleId, DateTime joinedAt, MembershipStatus status) : base(id, tenantId)
    {
        UserId = userId;
        RoleId = roleId;
        JoinedAt = joinedAt;
        Status = status;
        IsActive = status == MembershipStatus.Active;
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    /// <summary>Aktif üyelik (yalnız <see cref="MembershipStatus.Active"/> ve pasifleştirilmemiş). Bekleyen davet her zaman false.</summary>
    public bool IsActive { get; private set; }

    public MembershipStatus Status { get; private set; }

    /// <summary>Etkin üyelikte katılma anı; bekleyen davette davet anı.</summary>
    public DateTime JoinedAt { get; private set; }

    public bool IsPending => Status == MembershipStatus.Pending;

    /// <summary>Doğrudan aktif üyelik (yeni hesap açılırken veya yeni organizasyonun yöneticisi).</summary>
    public static Membership Create(Guid tenantId, Guid userId, Guid roleId, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), tenantId, Guard.NotDefault(userId), Guard.NotDefault(roleId), nowUtc, MembershipStatus.Active);

    /// <summary>Mevcut hesap için bekleyen davet (hesap sahibi kabul edene kadar hiçbir yetki vermez).</summary>
    public static Membership Invite(Guid tenantId, Guid userId, Guid roleId, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), tenantId, Guard.NotDefault(userId), Guard.NotDefault(roleId), nowUtc, MembershipStatus.Pending);

    /// <summary>Daveti kabul eder; yalnız bekleyen üyelik için etkilidir. Değiştiyse true.</summary>
    public bool Accept(DateTime nowUtc)
    {
        if (!IsPending)
        {
            return false;
        }

        Status = MembershipStatus.Active;
        IsActive = true;
        JoinedAt = nowUtc;
        return true;
    }

    public void ChangeRole(Guid roleId) => RoleId = Guard.NotDefault(roleId);

    /// <summary>Etkin üyeliği açar/kapatır; bekleyen davet <see cref="Accept"/> olmadan aktif olamaz.</summary>
    public void SetActive(bool isActive) => IsActive = isActive && !IsPending;
}
