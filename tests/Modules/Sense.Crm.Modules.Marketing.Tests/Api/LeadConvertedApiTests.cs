using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Marketing.Tests.Api.MarketingApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Marketing.Tests.Api;

/// <summary>Yardımcılar: dönüşüm olayını Worker yerine testte yayınlar / Sales outbox'ını boşaltır.</summary>
internal static class LeadConvertedKit
{
    /// <summary>Worker'ın yaptığı işi taklit eder: <c>LeadConverted</c> olayını <c>IEventBus</c> ile (kiracı bağlamıyla) yayınlar.</summary>
    public static async Task PublishLeadConvertedAsync(this CrmApiFactory factory, Guid tenantId, Guid leadId)
    {
        var bus = factory.Services.GetRequiredService<IEventBus>();
        await bus.Publish(new LeadConverted(tenantId, leadId, Guid.NewGuid(), Guid.NewGuid(), DealId: null), Ct);
    }

    /// <summary>Sales outbox'ını boşalana kadar işler (gerçek Worker yolu: outbox → IEventBus → Marketing işleyicisi).</summary>
    public static async Task DrainSalesOutboxAsync(this CrmApiFactory factory)
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OutboxProcessor<SalesDbContext>>().ProcessAsync(Ct) == 0)
            {
                return;
            }
        }
    }
}

/// <summary><c>LeadConverted</c> otomatik dönüşüm: tüm üyelikler <c>converted</c> olur; idempotent, kiracıya bağlı, kilitli.</summary>
[Collection(ApiCollection.Name)]
public sealed class LeadConvertedApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Convert_ThroughTheRealOutbox_MarksEveryOpenMembershipConverted_AndLocksIt()
    {
        var org = await factory.NewOrgAsync("Mkt Convert E2E");
        var admin = org.Admin;
        var lead = (await admin.CreateLeadAsync("Dönüşecek", extra: new { FirstName = "Deniz" })).Id();
        var otherLead = (await admin.CreateLeadAsync("Kalacak")).Id();
        var active = await admin.NewCampaignIdAsync("Aktif", extra: new { status = "active" });
        var completed = await admin.NewCampaignIdAsync("Tamamlanan", extra: new { status = "active" });
        var deleted = await admin.NewCampaignIdAsync("Silinecek");
        await admin.AddMembersAsync(active, "lead", [lead, otherLead]);
        await admin.AddMembersAsync(completed, "lead", [lead]);
        await admin.AddMembersAsync(deleted, "lead", [lead]);

        // Ön koşul: bir üye "yanıtladı", diğeri "gönderildi"; kampanya kapanır, biri silinir (durum önemsiz).
        var activeRow = (await admin.StatusOfAsync(active, lead)).Id();
        var completedRow = (await admin.StatusOfAsync(completed, lead)).Id();
        await admin.PostJsonAsync($"{CampaignsPath}/{active}/members/status", new { memberIds = new[] { activeRow }, status = "responded" }, HttpStatusCode.OK);
        await admin.PostJsonAsync($"{CampaignsPath}/{completed}/members/status", new { memberIds = new[] { completedRow }, status = "sent" }, HttpStatusCode.OK);
        await admin.PostJsonAsync($"{CampaignsPath}/{completed}/status", new { status = "completed" }, HttpStatusCode.NoContent);
        await admin.DeleteJsonAsync($"{CampaignsPath}/{deleted}");
        var before = (await admin.StatusOfAsync(active, lead)).GetProperty("statusChangedAt").GetDateTime();

        var converted = await admin.PostJsonAsync($"{Base}/leads/{lead}/convert", new { createDeal = false }, HttpStatusCode.OK);
        await factory.DrainSalesOutboxAsync();

        var inActive = await admin.StatusOfAsync(active, lead);
        var inCompleted = await admin.StatusOfAsync(completed, lead);
        (inActive.Str("status"), inCompleted.Str("status")).ShouldBe(("converted", "converted"));
        inActive.GetProperty("statusChangedAt").GetDateTime().ShouldBeGreaterThan(before);
        (await admin.StatusOfAsync(active, otherLead)).Str("status").ShouldBe("added", "başka lead etkilenmez");

        // Silinmiş kampanyanın üyeliğine dokunulmaz.
        (await factory.ScalarAsync<string>("SELECT status FROM marketing.campaign_members WHERE campaign_id = @id", ("id", deleted))).ShouldBe("Added");

        // Metrikler yeniden hesaplanır: 2 lead, 1 dönüşen, 1 ulaşılan dışı.
        var metrics = await admin.MetricsAsync(active);
        (metrics.Int("leadCount"), metrics.Int("convertedCount"), metrics.Dec("conversionRate")).ShouldBe((2, 1, 50m));
        metrics.GetProperty("statusCounts").Int("converted").ShouldBe(1);

        // Dönüşen üyenin durumu kilitlidir: toplu güncellemede atlanır, tek üyeli çağrıda da 200 + skippedCount 1.
        var single = await admin.PostJsonAsync($"{CampaignsPath}/{active}/members/status", new { memberIds = new[] { activeRow }, status = "sent" }, HttpStatusCode.OK);
        (single.Int("updatedCount"), single.Int("skippedCount")).ShouldBe((0, 1));
        var bulk = await admin.PostJsonAsync($"{CampaignsPath}/{active}/members/status", new { memberIds = (await admin.MembersAsync(active)).Select(m => m.Id()), status = "unsubscribed" }, HttpStatusCode.OK);
        (bulk.Int("updatedCount"), bulk.Int("skippedCount")).ShouldBe((1, 1));
        (await admin.StatusOfAsync(active, lead)).Str("status").ShouldBe("converted");

        // Yine de çıkarılabilir; kapalı (completed) kampanyada da.
        (await admin.PostJsonAsync($"{CampaignsPath}/{completed}/members/remove", new { memberIds = new[] { completedRow } }, HttpStatusCode.OK)).Int("removedCount").ShouldBe(1);

        // Dönüşen lead artık kampanyaya eklenemez; kişisi otomatik üye olmamıştır.
        var again = await admin.AddMembersAsync(active, "lead", [lead]);
        (again.Int("addedCount"), again.Int("alreadyMemberCount")).ShouldBe((0, 1), "zaten üye: önce üyelik kontrolü");
        var newCampaign = await admin.NewCampaignIdAsync("Yeni");
        (await admin.AddMembersAsync(newCampaign, "lead", [lead])).GetProperty("skipped")[0].Str("reason").ShouldBe("lead_converted");
        var contactId = converted.GetProperty("contactId").GetGuid();
        (await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=contact&memberId={contactId}")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task LeadConverted_DeliveredTwice_GivesTheSameResult_AndAuditsOnce()
    {
        var org = await factory.NewOrgAsync("Mkt Convert Idempotent");
        var admin = org.Admin;
        var lead = (await admin.CreateLeadAsync("Tekrar")).Id();
        var first = await admin.NewCampaignIdAsync("Bir");
        var second = await admin.NewCampaignIdAsync("İki");
        await admin.AddMembersAsync(first, "lead", [lead]);
        await admin.AddMembersAsync(second, "lead", [lead]);
        var rowId = (await admin.StatusOfAsync(first, lead)).Id();

        await factory.PublishLeadConvertedAsync(org.TenantId, lead);
        var afterFirst = (await admin.StatusOfAsync(first, lead)).GetProperty("statusChangedAt").GetDateTime();
        await factory.PublishLeadConvertedAsync(org.TenantId, lead);

        (await admin.StatusOfAsync(first, lead)).Str("status").ShouldBe("converted");
        (await admin.StatusOfAsync(second, lead)).Str("status").ShouldBe("converted");
        (await admin.StatusOfAsync(first, lead)).GetProperty("statusChangedAt").GetDateTime().ShouldBe(afterFirst, "ikinci teslim satıra dokunmaz");
        (await admin.MetricsAsync(first)).GetProperty("statusCounts").Int("converted").ShouldBe(1);

        // Denetim: oluşturma + tek güncelleme (kullanıcı boş: sistem bağlamı).
        var audit = await admin.GetJsonAsync($"{Base}/audit?entityType=CampaignMember&entityId={rowId}");
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.Str("action")).ShouldBe(["updated", "created"]);
        var update = items[0];
        update.Has("userId").ShouldBeFalse();
        var status = update.GetProperty("changes").GetProperty("status");
        (status.Str("old"), status.Str("new")).ShouldBe(("added", "converted"));
    }

    [Fact]
    public async Task LeadConverted_ForALeadWithoutMemberships_IsANoOp()
    {
        var org = await factory.NewOrgAsync("Mkt Convert NoOp");
        var admin = org.Admin;
        var member = (await admin.CreateLeadAsync("Üye")).Id();
        var campaign = await admin.NewCampaignIdAsync();
        await admin.AddMembersAsync(campaign, "lead", [member]);

        await factory.PublishLeadConvertedAsync(org.TenantId, Guid.NewGuid());
        await factory.PublishLeadConvertedAsync(org.TenantId, (await admin.CreateLeadAsync("Üyesiz")).Id());

        (await admin.StatusOfAsync(campaign, member)).Str("status").ShouldBe("added");
    }

    [Fact]
    public async Task LeadConverted_OfAnotherTenant_DoesNotTouchThisTenantsMemberships()
    {
        var a = await factory.NewOrgAsync("Mkt Convert Iso A");
        var b = await factory.NewOrgAsync("Mkt Convert Iso B");
        var lead = (await a.Admin.CreateLeadAsync("A'nın lead'i")).Id();
        var campaign = await a.Admin.NewCampaignIdAsync();
        await a.Admin.AddMembersAsync(campaign, "lead", [lead]);

        // B kiracısının olayı aynı LeadId ile gelse bile A'nın üyeliğini etkilemez (kiracı bağlamı olaydan kurulur).
        await factory.PublishLeadConvertedAsync(b.TenantId, lead);
        (await a.Admin.StatusOfAsync(campaign, lead)).Str("status").ShouldBe("added");

        await factory.PublishLeadConvertedAsync(a.TenantId, lead);
        (await a.Admin.StatusOfAsync(campaign, lead)).Str("status").ShouldBe("converted");
    }

    [Fact]
    public async Task Contacts_AreNeverConverted_EvenWhenTheirIdMatchesAnEvent()
    {
        var org = await factory.NewOrgAsync("Mkt Convert Contact");
        var admin = org.Admin;
        var contact = (await admin.CreateContactAsync("Kişi")).Id();
        var campaign = await admin.NewCampaignIdAsync();
        await admin.AddMembersAsync(campaign, "contact", [contact]);

        await factory.PublishLeadConvertedAsync(org.TenantId, contact);

        (await admin.StatusOfAsync(campaign, contact)).Str("status").ShouldBe("added");
    }
}
