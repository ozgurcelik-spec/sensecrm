using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;

namespace Crm.Modules.Identity.Domain.Memberships;

/// <summary>
/// Kullanıcı ↔ organizasyon ↔ rol bağı (K1). Kiracıya aittir: listeleme/değiştirme kiracı query filter'ı altında yapılır,
/// böylece bir organizasyonun yöneticisi başka organizasyonun üyelerini göremez. Bir kullanıcının organizasyonda en fazla
/// bir üyeliği vardır; tek rol (basit RBAC, K7).
/// </summary>
public sealed class Membership : TenantAggregateRoot<Guid>, IAuditLogged
{
    private Membership()
    {
    }

    private Membership(Guid id, Guid tenantId, Guid userId, Guid roleId, DateTime joinedAt) : base(id, tenantId)
    {
        UserId = userId;
        RoleId = roleId;
        JoinedAt = joinedAt;
        IsActive = true;
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime JoinedAt { get; private set; }

    public static Membership Create(Guid tenantId, Guid userId, Guid roleId, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), tenantId, Guard.NotDefault(userId), Guard.NotDefault(roleId), nowUtc);

    public void ChangeRole(Guid roleId) => RoleId = Guard.NotDefault(roleId);

    public void SetActive(bool isActive) => IsActive = isActive;
}
