using System.Net;
using System.Net.Http.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Marketing.Tests.Api.MarketingApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Marketing.Tests.Api;

/// <summary>Kampanyalar: CRUD, doğrulama, sahip kuralı, durum uçları, silme.</summary>
[Collection(ApiCollection.Name)]
public sealed class CampaignsApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Campaign_Crud_DefaultsResponseShapeAndFullReplace()
    {
        var org = await factory.NewOrgAsync("Mkt Crud");
        var admin = org.Admin;

        var created = await admin.CreateCampaignAsync("  Sonbahar E-posta  ", "email", new
        {
            startDate = "2026-10-01",
            endDate = "2026-10-31",
            budget = 12000.5m,
            expectedRevenue = 90000m,
            actualCost = 0m,
            description = "  İndirim dönemi  ",
        });
        var id = created.Id();

        (created.Str("name"), created.Str("type"), created.Str("status"), created.Str("currency")).ShouldBe(("Sonbahar E-posta", "email", "planned", "TRY"));
        (created.Str("startDate"), created.Str("endDate")).ShouldBe(("2026-10-01", "2026-10-31"));
        (created.Dec("budget"), created.Dec("expectedRevenue"), created.Dec("actualCost")).ShouldBe((12000.5m, 90000m, 0m));
        created.Str("description").ShouldBe("İndirim dönemi");
        created.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        created.Str("ownerName").ShouldBe(org.AdminName);
        created.Int("memberCount").ShouldBe(0);
        created.Has("createdAt").ShouldBeTrue();
        created.Has("updatedAt").ShouldBeFalse();

        var minimal = await admin.CreateCampaignAsync("Sade", "other");
        foreach (var absent in new[] { "startDate", "endDate", "budget", "expectedRevenue", "actualCost", "description", "updatedAt" })
        {
            minimal.Has(absent).ShouldBeFalse($"{absent} null iken yanıtta yazılmamalı");
        }

        var location = await admin.PostAsJsonAsync(CampaignsPath, new { name = "Konum", type = "event" }, Ct);
        location.StatusCode.ShouldBe(HttpStatusCode.Created);
        location.Headers.Location!.ToString().ShouldEndWith("/campaigns/" + (await location.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).Id());

        // PUT tam değiştirmedir: gönderilmeyen isteğe bağlı alan temizlenir, para birimi TRY'ye döner, sahip ve durum korunur.
        await admin.PostJsonAsync($"{CampaignsPath}/{id}/status", new { status = "active" }, HttpStatusCode.NoContent);
        await admin.PutJsonAsync($"{CampaignsPath}/{id}", new { name = "Sonbahar E-posta v2", type = "webinar", currency = "usd", budget = 500m });
        var updated = await admin.GetJsonAsync($"{CampaignsPath}/{id}");
        (updated.Str("name"), updated.Str("type"), updated.Str("status"), updated.Str("currency")).ShouldBe(("Sonbahar E-posta v2", "webinar", "active", "USD"));
        updated.Dec("budget").ShouldBe(500m);
        foreach (var cleared in new[] { "startDate", "endDate", "expectedRevenue", "actualCost", "description" })
        {
            updated.Has(cleared).ShouldBeFalse($"{cleared} tam değiştirmede temizlenmeli");
        }

        updated.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        updated.Has("updatedAt").ShouldBeTrue();

        await admin.PutJsonAsync($"{CampaignsPath}/{id}", new { name = "Para birimi yok", type = "webinar" });
        (await admin.GetJsonAsync($"{CampaignsPath}/{id}")).Str("currency").ShouldBe("TRY");

        // PUT durumu değiştirmez (gövdedeki status yok sayılır).
        await admin.PutJsonAsync($"{CampaignsPath}/{id}", new { name = "Durum denemesi", type = "webinar", status = "cancelled" });
        (await admin.GetJsonAsync($"{CampaignsPath}/{id}")).Str("status").ShouldBe("active");
    }

    [Fact]
    public async Task Create_WithActiveStatus_Works_AndClosedStatusesAreRejected()
    {
        var admin = (await factory.NewOrgAsync("Mkt Create Status")).Admin;

        (await admin.CreateCampaignAsync("Aktif başlat", "email", new { status = "active" })).Str("status").ShouldBe("active");
        (await admin.CreateCampaignAsync("Planlı", "email", new { status = "planned" })).Str("status").ShouldBe("planned");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", status = "completed" }, Ct)).ShouldBeValidationErrorAsync("status");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", status = "cancelled" }, Ct)).ShouldBeValidationErrorAsync("status");
        (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", status = "bogus" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Validation_RejectsBadInput_WithFieldErrors()
    {
        var admin = (await factory.NewOrgAsync("Mkt Validation")).Admin;

        await (await admin.PostAsJsonAsync(CampaignsPath, new { type = "email" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "", type = "email" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = new string('x', 201), type = "email" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x" }, Ct)).ShouldBeValidationErrorAsync("type");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", description = new string('x', 4001) }, Ct)).ShouldBeValidationErrorAsync("description");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", budget = -1 }, Ct)).ShouldBeValidationErrorAsync("budget");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", expectedRevenue = -0.5 }, Ct)).ShouldBeValidationErrorAsync("expectedRevenue");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", actualCost = -10 }, Ct)).ShouldBeValidationErrorAsync("actualCost");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", currency = "TL" }, Ct)).ShouldBeValidationErrorAsync("currency");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", currency = "TRY1" }, Ct)).ShouldBeValidationErrorAsync("currency");
        await (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", ownerUserId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("ownerUserId");
        (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "bogus" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", startDate = "not-a-date" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Sıfır tutar geçerlidir.
        var zero = await admin.CreateCampaignAsync("Sıfır", "email", new { budget = 0, expectedRevenue = 0, actualCost = 0 });
        (zero.Dec("budget"), zero.Dec("expectedRevenue"), zero.Dec("actualCost")).ShouldBe((0m, 0m, 0m));
    }

    [Fact]
    public async Task DateRange_EqualIsValid_EarlierEndIsRejected_WithEndDateFieldError()
    {
        var admin = (await factory.NewOrgAsync("Mkt Dates")).Admin;

        (await admin.CreateCampaignAsync("Eşit", "event", new { startDate = "2026-10-05", endDate = "2026-10-05" })).Str("endDate").ShouldBe("2026-10-05");
        await admin.CreateCampaignAsync("Yalnız başlangıç", "event", new { startDate = "2026-10-05" });
        await admin.CreateCampaignAsync("Yalnız bitiş", "event", new { endDate = "2026-10-05" });

        var bad = await admin.PostAsJsonAsync(CampaignsPath, new { name = "Ters", type = "event", startDate = "2026-10-05", endDate = "2026-10-04" }, Ct);
        await bad.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "campaign.invalid_date_range");
        var body = await bad.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        body.GetProperty("errors").TryGetProperty("endDate", out var messages).ShouldBeTrue();
        messages.EnumerateArray().Count().ShouldBeGreaterThan(0);

        var ok = await admin.NewCampaignIdAsync("Düzenlenecek", "event", new { startDate = "2026-10-05", endDate = "2026-10-09" });
        var put = await admin.PutAsJsonAsync($"{CampaignsPath}/{ok}", new { name = "Düzenlenecek", type = "event", startDate = "2026-10-09", endDate = "2026-10-05" }, Ct);
        await put.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "campaign.invalid_date_range");
        (await admin.GetJsonAsync($"{CampaignsPath}/{ok}")).Str("endDate").ShouldBe("2026-10-09");
    }

    [Fact]
    public async Task Owner_DefaultsToCaller_MustBeActiveMember_AndIsKeptOnPutWhenOmitted()
    {
        var org = await factory.NewOrgAsync("Mkt Owner");
        var other = await factory.NewOrgAsync("Mkt Owner Other");
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye Kişi", "crm.campaigns.read");

        var explicitOwner = await org.Admin.CreateCampaignAsync("Üyeye ait", "email", new { ownerUserId = memberId });
        (explicitOwner.GetProperty("ownerUserId").GetGuid(), explicitOwner.Str("ownerName")).ShouldBe((memberId, "Üye Kişi"));

        await (await org.Admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", ownerUserId = other.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email", ownerUserId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");

        // PUT: sahip verilmezse korunur; verilen üye değilse reddedilir.
        await org.Admin.PutJsonAsync($"{CampaignsPath}/{explicitOwner.Id()}", new { name = "Üyeye ait", type = "email" });
        (await org.Admin.GetJsonAsync($"{CampaignsPath}/{explicitOwner.Id()}")).GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);
        await (await org.Admin.PutAsJsonAsync($"{CampaignsPath}/{explicitOwner.Id()}", new { name = "x", type = "email", ownerUserId = other.AdminUserId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");

        // Sahibi pasifleşen kampanya sahip değişmediği için düzenlenebilir kalır.
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{memberId}", new { isActive = false }, HttpStatusCode.NoContent);
        await org.Admin.PutJsonAsync($"{CampaignsPath}/{explicitOwner.Id()}", new { name = "Pasif sahipli düzenleme", type = "email", ownerUserId = memberId });
        var after = await org.Admin.GetJsonAsync($"{CampaignsPath}/{explicitOwner.Id()}");
        (after.Str("name"), after.Str("ownerName")).ShouldBe(("Pasif sahipli düzenleme", "Üye Kişi"));
    }

    [Fact]
    public async Task StatusEndpoint_FollowsTheTransitionTable_AndConflictsCarryFromTo()
    {
        var admin = (await factory.NewOrgAsync("Mkt Status")).Admin;
        var id = await admin.NewCampaignIdAsync("Durum");
        var url = $"{CampaignsPath}/{id}/status";

        async Task<string> StatusAsync() => (await admin.GetJsonAsync($"{CampaignsPath}/{id}")).Str("status");

        // planned → completed tabloda yok.
        var invalid = await admin.PostAsJsonAsync(url, new { status = "completed" }, Ct);
        await invalid.ShouldBeProblemAsync(HttpStatusCode.Conflict, "campaign.invalid_status_transition");
        var problem = await invalid.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        var args = problem.GetProperty("args");
        (args.Str("from"), args.Str("to")).ShouldBe(("planned", "completed"));
        (await StatusAsync()).ShouldBe("planned");

        // Aynı duruma geçiş idempotent no-op.
        await admin.PostJsonAsync(url, new { status = "planned" }, HttpStatusCode.NoContent);

        foreach (var (target, expected) in new[] { ("active", "active"), ("completed", "completed"), ("active", "active"), ("cancelled", "cancelled"), ("planned", "planned"), ("cancelled", "cancelled") })
        {
            await admin.PostJsonAsync(url, new { status = target }, HttpStatusCode.NoContent);
            (await StatusAsync()).ShouldBe(expected);
        }

        // cancelled → completed / active tabloda yok.
        await (await admin.PostAsJsonAsync(url, new { status = "completed" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "campaign.invalid_status_transition");
        await (await admin.PostAsJsonAsync(url, new { status = "active" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "campaign.invalid_status_transition");

        await (await admin.PostAsJsonAsync(url, new { }, Ct)).ShouldBeValidationErrorAsync("status");
        (await admin.PostAsJsonAsync(url, new { status = "bogus" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await (await admin.PostAsJsonAsync($"{CampaignsPath}/{Guid.NewGuid()}/status", new { status = "active" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Delete_IsSoft_MembersRemainButAreInvisibleFromEveryEndpoint()
    {
        var org = await factory.NewOrgAsync("Mkt Delete");
        var admin = org.Admin;
        var leads = await admin.NewLeadIdsAsync(2);
        var keep = await admin.NewCampaignIdAsync("Kalacak");
        var doomed = await admin.NewCampaignIdAsync("Silinecek");
        await admin.AddMembersAsync(doomed, "lead", leads);
        await admin.AddMembersAsync(keep, "lead", leads.Take(1));

        await admin.DeleteJsonAsync($"{CampaignsPath}/{doomed}");

        await (await admin.GetAsync($"{CampaignsPath}/{doomed}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PutAsJsonAsync($"{CampaignsPath}/{doomed}", new { name = "x", type = "email" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{CampaignsPath}/{doomed}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.GetAsync($"{CampaignsPath}/{doomed}/members", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.GetAsync($"{CampaignsPath}/{doomed}/metrics", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{CampaignsPath}/{doomed}/members", new { memberType = "lead", memberIds = leads }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{CampaignsPath}/{doomed}/members/status", new { memberIds = leads, status = "sent" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{CampaignsPath}/{doomed}/members/remove", new { memberIds = leads }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.ListIdsAsync()).ShouldBe([keep]);

        // by-member yalnız silinmemiş kampanyaları gösterir.
        var byMember = await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={leads[0]}");
        byMember.EnumerateArray().Select(r => r.Str("campaignId")).ShouldBe([keep.ToString()]);
        (await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={leads[1]}")).GetArrayLength().ShouldBe(0);

        // Üyelik satırları veritabanında kalır (yumuşak bağ); rapor da saymaz.
        (await factory.ScalarAsync<long>("SELECT count(*) FROM marketing.campaign_members WHERE campaign_id = @id", ("id", doomed))).ShouldBe(2);
        (await admin.GetJsonAsync($"{Base}/reports/marketing/summary")).GetProperty("campaignCount").GetInt32().ShouldBe(1);
    }
}
