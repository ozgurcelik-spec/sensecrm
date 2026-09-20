using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// C-SEC2 L7: bekleyen davet kabulü/reddi (kiracı kapısından muaf kullanıcı-düzeyi komutlar) askıdaki (salt okunur/engelli), silme bekleyen organizasyonların üyelik tablosuna
/// <b>yazmaz</b> (403 <c>tenant.suspended</c>); askı kalkınca kabul çalışır.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvitationLifecycleApiTests(CrmApiFactory factory)
{
    [Theory]
    [InlineData("readOnly")]
    [InlineData("blocked")]
    [InlineData("pending_deletion")]
    public async Task Accepting_OrDeclining_AnInvitationToASuspendedOrDeletingOrganization_IsRejected_AndWritesNothing(string state)
    {
        var (target, platform) = await factory.OrgWithPlatformAsync(Token("inv") + " davetli");
        var home = await factory.SyncedOrgAsync(Token("home") + " ev");
        var (invitationId, roleId) = await InviteAsync(target, home);
        _ = roleId;

        switch (state)
        {
            case "pending_deletion":
                await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(target.TenantId)}/deletion-request", await platform.DeletionBodyAsync(target.TenantId, "x", 7), HttpStatusCode.OK);
                break;
            default:
                await platform.SuspendAsync(target.TenantId, "test", state);
                break;
        }

        var accept = await home.Admin.PostAsync($"{Base}/me/invitations/{invitationId}/accept", null, Ct);
        await AssertBlockedAsync(accept, state);
        var decline = await home.Admin.PostAsync($"{Base}/me/invitations/{invitationId}/decline", null, Ct);
        await AssertBlockedAsync(decline, state);

        // Üyelik tablosuna yazılmadı: davet hâlâ bekliyor.
        (await factory.ScalarAsync<string>("SELECT status FROM identity.memberships WHERE id = @i", ("i", invitationId))).ShouldBe("Pending");

        if (state != "pending_deletion")
        {
            await platform.ReactivateRawAsync(target.TenantId);
            var ok = await home.Admin.PostAsync($"{Base}/me/invitations/{invitationId}/accept", null, Ct);
            ok.StatusCode.ShouldBe(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync(Ct));
            (await factory.ScalarAsync<string>("SELECT status FROM identity.memberships WHERE id = @i", ("i", invitationId))).ShouldBe("Active");
        }
    }

    private async Task<(Guid InvitationId, Guid RoleId)> InviteAsync(TestOrg target, TestOrg home)
    {
        var roleId = await ErasureKit.StandardRoleAsync(target.Admin);
        var invited = await target.Admin.PostAsJsonAsync($"{Base}/organization/members", new { email = home.AdminEmail, roleId }, Ct);
        invited.StatusCode.ShouldBe(HttpStatusCode.Created, await invited.Content.ReadAsStringAsync(Ct));

        var invitations = await home.Admin.GetJsonAsync($"{Base}/me/invitations");
        return (invitations.EnumerateArray().Single(i => i.GetProperty("organizationId").GetGuid() == target.TenantId).GetProperty("id").GetGuid(), roleId);
    }

    private static async Task AssertBlockedAsync(HttpResponseMessage response, string what)
    {
        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, what);
    }
}
