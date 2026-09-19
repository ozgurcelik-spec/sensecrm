using Crm.Modules.Identity.Application.Roles;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Shared.Contracts.Events;
using Microsoft.Extensions.Options;

namespace Crm.Modules.Identity.Application.Provisioning;

/// <summary>Yeni açılan organizasyon ve tohumlanan Administrator rolü (kalıcılaştırma çağıranın sorumluluğundadır).</summary>
public sealed record ProvisionedOrganization(Tenant Tenant, Role Administrator);

/// <summary>
/// Organizasyon açılışının ortak adımları: benzersiz slug, <see cref="Tenant"/>, sistem rolleri ve <see cref="OrganizationCreated"/>
/// outbox olayı (modüller varsayılan verilerini bununla tohumlar). Üç yoldan kullanılır: herkese açık kayıt (<c>auth/signup</c>),
/// platform yöneticisi ucu (<c>platform/organizations</c>) ve Migrator <c>create-platform-admin</c>. Hiçbirini kaydetmez.
/// </summary>
public sealed class OrganizationProvisioner(
    ITenantRepository tenants,
    IRoleRepository roles,
    IPermissionCatalog catalog,
    IIntegrationEventOutbox outbox,
    ISecretGenerator secrets,
    IOptions<IdentityOptions> options)
{
    public async Task<ProvisionedOrganization> CreateAsync(string organizationName, string locale, CancellationToken cancellationToken)
    {
        var slug = await UniqueSlugAsync(Tenant.SlugFrom(organizationName), cancellationToken).ConfigureAwait(false);
        var tenant = Tenant.Create(organizationName, slug, locale, options.Value.DefaultTimeZone);
        tenants.Add(tenant);

        // Modüller (ör. Sales: varsayılan satış hunisi) kendi varsayılan verilerini bu olayla tohumlar; aynı transaction'da outbox'a yazılır.
        outbox.Enqueue(new OrganizationCreated(tenant.Id, tenant.Name, tenant.DefaultLocale));

        // Kiracı verisi açıkça yeni organizasyonun TenantId'siyle yazılır.
        var seeded = SeedSystemRoles(tenant.Id);
        return new ProvisionedOrganization(tenant, seeded[SystemRoleCodes.Administrator]);
    }

    /// <summary>Tüm sistem rollerini katalogdaki izinlerle oluşturur.</summary>
    private Dictionary<string, Role> SeedSystemRoles(Guid tenantId)
    {
        var result = new Dictionary<string, Role>(StringComparer.Ordinal);
        foreach (var code in SystemRoleCodes.All)
        {
            var role = Role.CreateSystem(tenantId, code, SystemRoleDefinitions.PermissionsFor(code, catalog.All));
            roles.Add(role);
            result[code] = role;
        }

        return result;
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var candidate = baseSlug;
        for (var attempt = 0; attempt < IdentityDefaults.SlugSuffixAttempts; attempt++)
        {
            if (!await tenants.SlugExistsAsync(candidate, ct).ConfigureAwait(false))
            {
                return candidate;
            }

            candidate = string.Concat(baseSlug, IdentityLimits.SlugSeparator.ToString(), secrets.NewSuffix());
        }

        return string.Concat(baseSlug, IdentityLimits.SlugSeparator.ToString(), Guid.NewGuid().ToString("N")[..8]);
    }
}
