using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Application.Provisioning;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// C-SEC2 H1, "eski biçimli veritabanı": yükseltilen kurulumda platform işletim organizasyonunun hesabı <c>is_system = false</c> yazılmış olabilir
/// (<c>AccountBackfill</c> her mevcut kiracıya <c>false</c> yazıyordu). Migrator <c>backfill</c> (<c>migrate</c> sonunda da çalışır) aktif platform yöneticisi üyesi olan her kiracı
/// hesabını <c>is_system = true</c> işaretler; <c>create-platform-admin</c> aynı işareti terfi eden/zaten yönetici olan hesabın kiracıları için de yazar (<c>EnsureSystemAsync</c>).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LegacyPlatformTenantBackfillApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Backfill_MarksEveryTenantWithAnActivePlatformAdminMember_AndLeavesTheOthersAlone()
    {
        var platform = await factory.PlatformAdminAsync();
        var (adminId, adminEmail, operatingTenant) = await platform.WhoAmIAsync();
        var customer = await factory.SyncedOrgAsync(Token("leg") + " musteri");
        var control = await factory.SyncedOrgAsync(Token("ctl") + " kontrol");
        var inactiveHome = await factory.SyncedOrgAsync(Token("ina") + " pasif uye");

        // Yönetici müşteri organizasyonuna da üye olur (davet + kabul); bir başkasında üyeliği pasif.
        var roleId = await ErasureKit.StandardRoleAsync(customer.Admin);
        await ErasureKit.InviteAndAcceptAsync(customer.Admin, adminEmail, roleId, platform, customer.TenantId);
        var inactiveRole = await ErasureKit.StandardRoleAsync(inactiveHome.Admin);
        await ErasureKit.InviteAndAcceptAsync(inactiveHome.Admin, adminEmail, inactiveRole, platform, inactiveHome.TenantId);
        await factory.SqlAsync("UPDATE identity.memberships SET is_active = FALSE WHERE tenant_id = @t AND user_id = @u", ("t", inactiveHome.TenantId), ("u", adminId));

        // Eski biçim: hiçbirinde is_system yok (M7'nin ilk backfill'i hepsine false yazıyordu).
        await factory.DrainOutboxesAsync();
        await factory.SqlAsync("UPDATE platform.tenant_accounts SET is_system = FALSE WHERE tenant_id = ANY(@ids)", ("ids", new[] { operatingTenant, customer.TenantId, control.TenantId, inactiveHome.TenantId }));

        using (var scope = factory.Services.CreateScope())
        {
            var backfill = scope.ServiceProvider.GetRequiredService<AccountBackfill>();
            await backfill.RunAsync(Ct);
            backfill.LastMarkedSystem.ShouldBeGreaterThanOrEqualTo(2);
        }

        (await IsSystemAsync(operatingTenant)).ShouldBeTrue("işletim organizasyonu");
        (await IsSystemAsync(customer.TenantId)).ShouldBeTrue("aktif platform yöneticisi üyesi olan kiracı");
        (await IsSystemAsync(control.TenantId)).ShouldBeFalse();
        (await IsSystemAsync(inactiveHome.TenantId)).ShouldBeFalse("pasif üyelik korumaz");

        // İdempotent.
        using var again = factory.Services.CreateScope();
        var second = again.ServiceProvider.GetRequiredService<AccountBackfill>();
        await second.RunAsync(Ct);
        second.LastMarkedSystem.ShouldBe(0);
    }

    [Fact]
    public async Task CreatePlatformAdminForAnExistingAdmin_MarksTheirTenantsSystem_ThroughTheDirectoryAndTheProvisioner()
    {
        var platform = await factory.PlatformAdminAsync();
        var (adminId, _, operatingTenant) = await platform.WhoAmIAsync();
        await factory.DrainOutboxesAsync();
        await factory.SqlAsync("UPDATE platform.tenant_accounts SET is_system = FALSE WHERE tenant_id = @t", ("t", operatingTenant));

        // Migrator create-platform-admin (Unchanged/Promoted) ile aynı adımlar.
        using (var scope = factory.Services.CreateScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<IPlatformAdminDirectory>();
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var provisioner = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
            foreach (var tenantId in await directory.ListActiveTenantsOfUserAsync(adminId, Ct))
            {
                await provisioner.EnsureSystemAsync((await tenants.FindAsync(tenantId, Ct))!, Ct);
            }

            await scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Platform.Application.IPlatformUnitOfWork>().SaveChangesAsync(Ct);
        }

        (await IsSystemAsync(operatingTenant)).ShouldBeTrue();
    }

    private Task<bool> IsSystemAsync(Guid tenantId) =>
        factory.ScalarAsync<bool>("SELECT is_system FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId));
}
