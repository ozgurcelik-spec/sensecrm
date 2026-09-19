using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Marketing.Domain;
using Sense.Crm.Modules.Marketing.Domain.Members;
using Sense.Crm.Modules.Marketing.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Marketing.Tests.Api.MarketingApiKit;

namespace Sense.Crm.Modules.Marketing.Tests.Api;

/// <summary>
/// Kilitsiz (savunma hattı) yol: iki transaction aynı üyeliği aynı anda eklerse benzersiz ihlali hata değil "zaten üye"dir
/// (<c>AddRangeIgnoringDuplicatesAsync</c>). Yarışı deterministik kurar: A ekler ve commit'i bekletir, B aynı satırda bloklanır.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MemberWriteRaceTests(CrmApiFactory factory)
{
    [Fact]
    public async Task UniqueViolation_OfASecondWriter_IsTreatedAsAlreadyMember_AndTheTransactionStaysUsable()
    {
        var org = await factory.NewOrgAsync("Mkt Write Race");
        var campaign = await org.Admin.NewCampaignIdAsync();
        var lead = (await org.Admin.CreateLeadAsync("Yarış")).Id();
        var other = (await org.Admin.CreateLeadAsync("Yarış 2")).Id();

        var aInserted = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();

        async Task<IReadOnlySet<Guid>> WriteAsync(Guid[] leads, bool holdCommit)
        {
            using var system = CurrentUserAccessor.UseSystem();
            await using var scope = factory.Services.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(org.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<MarketingDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<ICampaignMemberRepository>();
            var rows = leads.Select(id => CampaignMember.Create(org.TenantId, campaign, CampaignMemberType.Lead, id, org.AdminUserId, DateTime.UtcNow)).ToList();

            IReadOnlySet<Guid> added = new HashSet<Guid>();
            await db.ExecuteInTransactionAsync(async ct =>
            {
                added = await repo.AddRangeIgnoringDuplicatesAsync(rows, ct);
                if (holdCommit)
                {
                    aInserted.SetResult();
                    await releaseA.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }

                // Çakışma sonrası aynı transaction kullanılabilir kalmalı (savepoint geri alındı): sorgu çalışır.
                (await db.CampaignMembers.CountAsync(m => m.CampaignId == campaign, ct)).ShouldBeGreaterThanOrEqualTo(0);
            }, Ct);
            return added;
        }

        var first = WriteAsync([lead], holdCommit: true);
        await aInserted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = WriteAsync([lead, other], holdCommit: false); // lead'de A'nın kilidinde bloklanır
        await Task.Delay(500, Ct);
        second.IsCompleted.ShouldBeFalse("B, A'nın işlenmemiş satırında bekliyor olmalı");
        releaseA.SetResult();

        (await first).Count.ShouldBe(1);
        (await second).Count.ShouldBe(1, "yalnız çakışmayan üyelik eklenir");
        (await org.Admin.MembersAsync(campaign)).Select(m => m.Str("memberId")).OrderBy(x => x).ShouldBe(new[] { lead.ToString(), other.ToString() }.OrderBy(x => x));
        (await factory.ScalarAsync<long>("SELECT count(*) FROM marketing.campaign_members WHERE campaign_id = @id", ("id", campaign))).ShouldBe(2);
    }
}
