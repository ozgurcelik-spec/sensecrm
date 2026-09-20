using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Memberships;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Configuration;

namespace Sense.Crm.Modules.Identity.Application.Provisioning;

public enum PlatformAdminOutcome
{
    /// <summary>Hesap ve "Platform" organizasyonu oluşturuldu.</summary>
    Created = 0,

    /// <summary>Var olan hesap platform yöneticisi yapıldı (parola değişmez).</summary>
    Promoted = 1,

    /// <summary>Hesap zaten platform yöneticisiydi; hiçbir şey değişmedi.</summary>
    Unchanged = 2,

    /// <summary>Girdi geçersiz (<see cref="PlatformAdminResult.Problem"/>).</summary>
    Invalid = 3,
}

/// <param name="TenantId">Yalnız <see cref="PlatformAdminOutcome.Created"/>: yeni işletim organizasyonu (Migrator Platform hesabını <c>is_system</c> yazar).</param>
/// <param name="UserId">
/// Oluşturulan/terfi ettirilen/zaten yönetici olan hesap (C-SEC2 H1: Migrator, <c>Promoted</c>/<c>Unchanged</c> için hesabın kiracılarını da <c>is_system</c> işaretler).
/// </param>
public sealed record PlatformAdminResult(PlatformAdminOutcome Outcome, string? Problem = null, Guid? TenantId = null, Guid? UserId = null);

/// <summary>
/// İlk platform yöneticisini oluşturur (Migrator <c>create-platform-admin</c>; ortam değişkenlerinden). İdempotenttir: hesap varsa
/// parolasına dokunulmaz, yalnız bayrak verilir. Yeni hesap için oturum açabilmesi adına "Platform" adlı işletim organizasyonu
/// (yalnız yönetici hesabını barındırır, müşteri verisi içermez) ve Administrator üyeliği açılır. HTTP'den çağrılamaz.
/// </summary>
public sealed class PlatformAdminBootstrapper(
    IUserRepository users,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    OrganizationProvisioner provisioner,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public const string DefaultOrganizationName = "Platform";
    public const string DefaultDisplayName = "Platform Admin";

    public async Task<PlatformAdminResult> EnsureAsync(
        string? email,
        string? password,
        string? displayName,
        string? organizationName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || !Sense.Crm.Shared.Kernel.ValueObjects.EmailAddress.IsValid(email))
        {
            return new PlatformAdminResult(PlatformAdminOutcome.Invalid, "A valid e-mail address is required (PLATFORM_ADMIN_EMAIL).");
        }

        var existing = await users.GetByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IsPlatformAdmin)
            {
                return new PlatformAdminResult(PlatformAdminOutcome.Unchanged, UserId: existing.Id);
            }

            existing.GrantPlatformAdmin();
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformAdminResult(PlatformAdminOutcome.Promoted, UserId: existing.Id);
        }

        if (string.IsNullOrEmpty(password) || password.Length < IdentityDefaults.PlatformAdminMinPasswordLength || password.Length > IdentityLimits.PasswordMaxLength)
        {
            return new PlatformAdminResult(
                PlatformAdminOutcome.Invalid,
                $"PLATFORM_ADMIN_PASSWORD must be {IdentityDefaults.PlatformAdminMinPasswordLength}-{IdentityLimits.PasswordMaxLength} characters.");
        }

        // Parola politikası (yaygın parola listesi, e-posta kullanıcı adı) platform yöneticisi için de geçerlidir (H4-e).
        if (PasswordPolicy.Evaluate(password, email, IdentityDefaults.PlatformAdminMinPasswordLength) is { Count: > 0 } violations)
        {
            return new PlatformAdminResult(PlatformAdminOutcome.Invalid, "PLATFORM_ADMIN_PASSWORD violates the password policy: " + string.Join(", ", violations.Select(v => v.Code)));
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName.Trim();
        if (name.Length > IdentityLimits.DisplayNameMaxLength)
        {
            return new PlatformAdminResult(PlatformAdminOutcome.Invalid, "PLATFORM_ADMIN_NAME is too long.");
        }

        var organization = string.IsNullOrWhiteSpace(organizationName) ? DefaultOrganizationName : organizationName.Trim();
        if (organization.Length > IdentityLimits.OrganizationNameMaxLength)
        {
            return new PlatformAdminResult(PlatformAdminOutcome.Invalid, "PLATFORM_ORG_NAME is too long.");
        }

        var provisioned = await provisioner.CreateAsync(organization, Cultures.TurkishLanguage, cancellationToken, Contracts.OrganizationOrigin.Bootstrap).ConfigureAwait(false);

        // C-SEC2 M6: başlangıç parolası bir dosyada/ortamda durur; ilk girişte değiştirilmek zorundadır (yalnız /me ve parola değiştirme uçları açık).
        var user = User.Create(email.Trim(), name, Cultures.TurkishLanguage, hasher.Hash(password), mustChangePassword: true);
        user.GrantPlatformAdmin();
        user.SetDefaultTenant(provisioned.Tenant.Id);
        users.Add(user);
        memberships.Add(Membership.Create(provisioned.Tenant.Id, user.Id, provisioned.Administrator.Id, clock.GetUtcNow().UtcDateTime));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PlatformAdminResult(PlatformAdminOutcome.Created, TenantId: provisioned.Tenant.Id, UserId: user.Id);
    }
}
