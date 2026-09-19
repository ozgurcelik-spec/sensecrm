using System.Net;
using Crm.Modules.Sales.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Tests.Shared.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Sales.Tests.Api;

/// <summary><c>ILeadStatusLookup</c> (M6C, Marketing için): yalnız aktif kiracıdaki, silinmemiş ve <c>converted</c> lead'ler döner.</summary>
[Collection(ApiCollection.Name)]
public sealed class LeadStatusLookupTests(CrmApiFactory factory)
{
    [Fact]
    public async Task GetConvertedLeadIds_ReturnsOnlyConvertedLeadsOfTheActiveTenant()
    {
        var org = await factory.NewOrgAsync("Lead Status Org");
        var other = await factory.NewOrgAsync("Lead Status Other Org");

        var open = await org.Admin.CreateLeadAsync("Açık", "A");
        var convertedLead = await org.Admin.CreateLeadAsync("Dönüşmüş", "B");
        await org.Admin.PostJsonAsync($"{Base}/leads/{convertedLead.Id()}/convert", new { createDeal = false }, HttpStatusCode.OK);
        var deletedConverted = await org.Admin.CreateLeadAsync("Silinmiş", "C");
        await org.Admin.PostJsonAsync($"{Base}/leads/{deletedConverted.Id()}/convert", new { createDeal = false }, HttpStatusCode.OK);
        await org.Admin.DeleteJsonAsync($"{Base}/leads/{deletedConverted.Id()}");
        var foreignConverted = await other.Admin.CreateLeadAsync("Yabancı", "D");
        await other.Admin.PostJsonAsync($"{Base}/leads/{foreignConverted.Id()}/convert", new { createDeal = false }, HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(org.TenantId);
        var lookup = scope.ServiceProvider.GetRequiredService<ILeadStatusLookup>();

        var result = await lookup.GetConvertedLeadIdsAsync(
            [open.Id(), convertedLead.Id(), deletedConverted.Id(), foreignConverted.Id(), Guid.NewGuid(), convertedLead.Id()],
            Ct);

        result.ShouldBe([convertedLead.Id()]);
        (await lookup.GetConvertedLeadIdsAsync([], Ct)).ShouldBeEmpty();
    }
}
