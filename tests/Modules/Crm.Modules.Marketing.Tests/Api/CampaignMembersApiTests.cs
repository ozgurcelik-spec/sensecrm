using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Marketing.Tests.Api.MarketingApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Marketing.Tests.Api;

/// <summary>Kampanya üyeleri: toplu ekleme (idempotent), atlanan kayıtlar, kapalı kampanya, eşzamanlılık, liste, durum, çıkarma, by-member.</summary>
[Collection(ApiCollection.Name)]
public sealed class CampaignMembersApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task BulkAdd_IsIdempotent_DeduplicatesInput_AndReportsCounts()
    {
        var admin = (await factory.NewOrgAsync("Mkt Add Idempotent")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var leads = await admin.NewLeadIdsAsync(3);

        var first = await admin.AddMembersAsync(campaign, "lead", [leads[0], leads[1], leads[1]]);
        (first.Int("addedCount"), first.Int("alreadyMemberCount"), first.GetProperty("skipped").GetArrayLength()).ShouldBe((2, 0, 0));

        var second = await admin.AddMembersAsync(campaign, "lead", leads);
        (second.Int("addedCount"), second.Int("alreadyMemberCount"), second.GetProperty("skipped").GetArrayLength()).ShouldBe((1, 2, 0));

        var repeat = await admin.AddMembersAsync(campaign, "lead", leads);
        (repeat.Int("addedCount"), repeat.Int("alreadyMemberCount")).ShouldBe((0, 3));

        var members = await admin.MembersAsync(campaign);
        members.Count.ShouldBe(3);
        members.ShouldAllBe(m => m.Str("status") == "added" && m.Str("memberType") == "lead");
        (await admin.GetJsonAsync($"{CampaignsPath}/{campaign}")).Int("memberCount").ShouldBe(3);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM marketing.campaign_members WHERE campaign_id = @id", ("id", campaign))).ShouldBe(3);
    }

    [Fact]
    public async Task BulkAdd_SkipsUnknownAndOtherTenantRecords_WithoutFailing()
    {
        var a = await factory.NewOrgAsync("Mkt Skip A");
        var b = await factory.NewOrgAsync("Mkt Skip B");
        var campaign = await a.Admin.NewCampaignIdAsync();
        var mine = (await a.Admin.CreateLeadAsync("Benim")).Id();
        var foreign = (await b.Admin.CreateLeadAsync("Yabancı")).Id();
        var deleted = (await a.Admin.CreateLeadAsync("Silinmiş")).Id();
        await a.Admin.DeleteJsonAsync($"{Base}/leads/{deleted}");
        var random = Guid.NewGuid();

        var result = await a.Admin.AddMembersAsync(campaign, "lead", [mine, foreign, random, deleted]);

        (result.Int("addedCount"), result.Int("alreadyMemberCount")).ShouldBe((1, 0));
        var skipped = result.GetProperty("skipped").EnumerateArray().Select(s => (s.Str("memberId"), s.Str("reason"))).ToList();
        skipped.ShouldBe(
        [
            (foreign.ToString(), "not_found"),
            (random.ToString(), "not_found"),
            (deleted.ToString(), "not_found"),
        ]);
        (await a.Admin.MembersAsync(campaign)).Select(m => m.Str("memberId")).ShouldBe([mine.ToString()]);

        // Tür uyuşmazlığı: lead kimliği kişi olarak eklenemez.
        var asContact = await a.Admin.AddMembersAsync(campaign, "contact", [mine]);
        (asContact.Int("addedCount"), asContact.GetProperty("skipped")[0].Str("reason")).ShouldBe((0, "not_found"));
    }

    [Fact]
    public async Task BulkAdd_SkipsConvertedLeads_ButStillAddsTheOthers()
    {
        var admin = (await factory.NewOrgAsync("Mkt Skip Converted")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var open = (await admin.CreateLeadAsync("Açık")).Id();
        var converted = (await admin.CreateLeadAsync("Dönüşmüş")).Id();
        await admin.PostJsonAsync($"{Base}/leads/{converted}/convert", new { createDeal = false }, HttpStatusCode.OK);

        var result = await admin.AddMembersAsync(campaign, "lead", [open, converted]);

        (result.Int("addedCount"), result.Int("alreadyMemberCount")).ShouldBe((1, 0));
        var skipped = result.GetProperty("skipped").EnumerateArray().Single();
        (skipped.Str("memberId"), skipped.Str("reason")).ShouldBe((converted.ToString(), "lead_converted"));
        (await admin.MembersAsync(campaign)).Select(m => m.Str("memberId")).ShouldBe([open.ToString()]);

        // Dönüşen lead'in kişisi kampanyaya eklenebilir (kişiler dönüşmez).
        var contactId = (await admin.GetJsonAsync($"{Base}/leads/{converted}")).GetProperty("convertedContactId").GetGuid();
        (await admin.AddMembersAsync(campaign, "contact", [contactId])).Int("addedCount").ShouldBe(1);
    }

    [Fact]
    public async Task BulkAdd_AcceptsContacts_AsAnIndependentMemberType()
    {
        var admin = (await factory.NewOrgAsync("Mkt Add Contacts")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var contact = (await admin.CreateContactAsync("Demir", new { FirstName = "Ayşe" })).Id();

        (await admin.AddMembersAsync(campaign, "contact", [contact])).Int("addedCount").ShouldBe(1);
        (await admin.AddMembersAsync(campaign, "contact", [contact])).Int("alreadyMemberCount").ShouldBe(1);

        var member = (await admin.MembersAsync(campaign)).Single();
        (member.Str("memberType"), member.Str("memberName"), member.GetProperty("memberMissing").GetBoolean()).ShouldBe(("contact", "Ayşe Demir", false));
    }

    [Fact]
    public async Task BulkAdd_EnforcesTheBatchLimits_500AcceptedAnd501Rejected()
    {
        var admin = (await factory.NewOrgAsync("Mkt Add Limits")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var url = $"{CampaignsPath}/{campaign}/members";

        await (await admin.PostAsJsonAsync(url, new { memberType = "lead", memberIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        await (await admin.PostAsJsonAsync(url, new { memberType = "lead", memberIds = Array.Empty<Guid>() }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        await (await admin.PostAsJsonAsync(url, new { memberType = "lead" }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        await (await admin.PostAsJsonAsync(url, new { memberType = "lead", memberIds = new[] { Guid.NewGuid(), Guid.Empty } }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        await (await admin.PostAsJsonAsync(url, new { memberIds = new[] { Guid.NewGuid() } }, Ct)).ShouldBeValidationErrorAsync("memberType");
        (await admin.PostAsJsonAsync(url, new { memberType = "account", memberIds = new[] { Guid.NewGuid() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Tek üye = tek elemanlı dizi.
        var single = (await admin.CreateLeadAsync("Tek")).Id();
        (await admin.AddMembersAsync(campaign, "lead", [single])).Int("addedCount").ShouldBe(1);

        // 500 gerçek lead tek çağrıda, tek transaction'da eklenir.
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 500), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, _) =>
            ids.Add((await admin.CreateLeadAsync($"Toplu{i:D3}")).Id()));
        var result = await admin.AddMembersAsync(campaign, "lead", ids);
        (result.Int("addedCount"), result.Int("alreadyMemberCount"), result.GetProperty("skipped").GetArrayLength()).ShouldBe((500, 0, 0));
        (await admin.GetJsonAsync($"{CampaignsPath}/{campaign}")).Int("memberCount").ShouldBe(501);

        // Tam 500 bilinmeyen kimlik de geçerli bir istektir (404 değil, hepsi atlanır).
        var unknown = await admin.AddMembersAsync(campaign, "lead", Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()));
        (unknown.Int("addedCount"), unknown.GetProperty("skipped").GetArrayLength()).ShouldBe((0, 500));
    }

    [Fact]
    public async Task ClosedCampaign_RejectsNewMembers_ButStatusUpdateAndRemovalStillWork()
    {
        var admin = (await factory.NewOrgAsync("Mkt Closed")).Admin;
        var campaign = await admin.NewCampaignIdAsync(extra: new { status = "active" });
        var leads = await admin.NewLeadIdsAsync(3);
        await admin.AddMembersAsync(campaign, "lead", leads.Take(2));
        var rows = await admin.MembersAsync(campaign);

        foreach (var closing in new[] { "completed", "cancelled" })
        {
            await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = closing }, HttpStatusCode.NoContent);

            await (await admin.PostAsJsonAsync($"{CampaignsPath}/{campaign}/members", new { memberType = "lead", memberIds = new[] { leads[2] } }, Ct))
                .ShouldBeProblemAsync(HttpStatusCode.Conflict, "campaign.closed");
            (await admin.MembersAsync(campaign)).Count.ShouldBe(2);

            var update = await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { rows[0].Id() }, status = closing == "completed" ? "responded" : "sent" }, HttpStatusCode.OK);
            update.Int("updatedCount").ShouldBe(1);

            if (closing == "completed")
            {
                await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = "active" }, HttpStatusCode.NoContent);
                await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = "completed" }, HttpStatusCode.NoContent);
                await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = "active" }, HttpStatusCode.NoContent);
            }
        }

        var removed = await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/remove", new { memberIds = new[] { rows[1].Id() } }, HttpStatusCode.OK);
        removed.Int("removedCount").ShouldBe(1);

        // Yeniden açılınca (cancelled → planned) ekleme yeniden mümkündür.
        await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = "planned" }, HttpStatusCode.NoContent);
        (await admin.AddMembersAsync(campaign, "lead", [leads[2]])).Int("addedCount").ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentAdds_OfTheSameMembers_NeverFail_NorCreateDuplicates()
    {
        var admin = (await factory.NewOrgAsync("Mkt Concurrent")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var leads = await admin.NewLeadIdsAsync(5);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admin.AddMembersAsync(campaign, "lead", leads)));

        results.Sum(r => r.Int("addedCount")).ShouldBe(5, "her üye tam bir istekte eklenmiş sayılır");
        results.ShouldAllBe(r => r.Int("addedCount") + r.Int("alreadyMemberCount") == 5);
        (await admin.MembersAsync(campaign)).Count.ShouldBe(5);
        (await factory.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT member_id FROM marketing.campaign_members WHERE campaign_id = @id GROUP BY member_id HAVING count(*) > 1) d",
            ("id", campaign))).ShouldBe(0);

        // Denetim: her üyelik için tek "created" kaydı (çakışan istekler iz bırakmaz).
        var memberIds = (await admin.MembersAsync(campaign)).Select(m => m.Id()).ToList();
        foreach (var id in memberIds)
        {
            (await admin.GetJsonAsync($"{Base}/audit?entityType=CampaignMember&entityId={id}")).GetProperty("total").GetInt64().ShouldBe(1);
        }
    }

    [Fact]
    public async Task ConcurrentAdds_Stress_OverlappingBatchesInMixedOrder_AlwaysSucceedWithExactCounts()
    {
        var admin = (await factory.NewOrgAsync("Mkt Concurrent Stress")).Admin;
        var leads = await admin.NewLeadIdsAsync(6);

        for (var round = 0; round < 12; round++)
        {
            var campaign = await admin.NewCampaignIdAsync($"Stres {round}");
            var url = $"{CampaignsPath}/{campaign}/members";

            // 12 eşzamanlı istek: aynı 6 lead, yarısı ters sırada, biri kısmi küme; her yanıtın durumu + gövdesi hata mesajında görünür.
            var requests = Enumerable.Range(0, 12).Select(async i =>
            {
                var ids = i % 2 == 0 ? leads : Enumerable.Reverse(leads).ToList();
                if (i == 5)
                {
                    ids = leads.Take(3).ToList();
                }

                var response = await admin.PostAsJsonAsync(url, new { memberType = "lead", memberIds = ids }, Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, $"round {round} request {i}: {(int)response.StatusCode} {body}");
                var json = JsonDocument.Parse(body).RootElement;
                (json.GetProperty("addedCount").GetInt32() + json.GetProperty("alreadyMemberCount").GetInt32()).ShouldBe(ids.Count, $"round {round} request {i}: {body}");
                json.GetProperty("skipped").GetArrayLength().ShouldBe(0, body);
                return json.GetProperty("addedCount").GetInt32();
            }).ToList();

            var added = await Task.WhenAll(requests);
            added.Sum().ShouldBe(6, $"round {round}: eklenen toplamı tam olarak üye sayısı olmalı");
            (await admin.MembersAsync(campaign)).Count.ShouldBe(6);
        }
    }

    [Fact]
    public async Task MemberList_FiltersSortsPages_AndResolvesNamesInBatch()
    {
        var org = await factory.NewOrgAsync("Mkt Member List");
        var admin = org.Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var ali = (await admin.CreateLeadAsync("Yılmaz", extra: new { FirstName = "Ali" })).Id();
        var veli = (await admin.CreateLeadAsync("Kaya", extra: new { FirstName = "Veli" })).Id();
        var gone = (await admin.CreateLeadAsync("Silinecek")).Id();
        var contact = (await admin.CreateContactAsync("Demir", new { FirstName = "Ayşe" })).Id();
        await admin.AddMembersAsync(campaign, "lead", [ali, veli, gone]);
        await admin.AddMembersAsync(campaign, "contact", [contact]);
        var rows = await admin.MembersAsync(campaign);
        var aliRow = rows.Single(r => r.Str("memberId") == ali.ToString()).Id();
        await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { aliRow }, status = "responded" }, HttpStatusCode.OK);
        await admin.DeleteJsonAsync($"{Base}/leads/{gone}");

        var all = await admin.MembersAsync(campaign, "?sort=memberType,status");
        all.Count.ShouldBe(4);
        var byId = all.ToDictionary(m => m.Str("memberId"));
        byId[ali.ToString()].Str("memberName").ShouldBe("Ali Yılmaz");
        byId[contact.ToString()].Str("memberName").ShouldBe("Ayşe Demir");
        byId[gone.ToString()].Has("memberName").ShouldBeFalse();
        byId[gone.ToString()].GetProperty("memberMissing").GetBoolean().ShouldBeTrue();
        byId[ali.ToString()].GetProperty("memberMissing").GetBoolean().ShouldBeFalse();
        var row = byId[ali.ToString()];
        (row.GetProperty("addedByUserId").GetGuid(), row.Str("addedByName")).ShouldBe((org.AdminUserId, org.AdminName));
        (row.Has("addedAt"), row.Has("statusChangedAt")).ShouldBe((true, true));
        row.GetProperty("statusChangedAt").GetDateTime().ShouldBeGreaterThan(row.GetProperty("addedAt").GetDateTime());

        // Sıralama: memberType (lead önce), sonra durum sırası (added < sent < responded < converted < unsubscribed).
        all.Select(m => m.Str("memberType")).ShouldBe(["lead", "lead", "lead", "contact"]);
        all.Take(3).Select(m => m.Str("status")).ShouldBe(["added", "added", "responded"]);

        (await admin.MembersAsync(campaign, "?memberType=contact")).Single().Str("memberId").ShouldBe(contact.ToString());
        (await admin.MembersAsync(campaign, "?status=responded")).Single().Str("memberId").ShouldBe(ali.ToString());
        (await admin.MembersAsync(campaign, "?status=responded,added&memberType=lead")).Count.ShouldBe(3);
        (await admin.MembersAsync(campaign, "?status=sent")).ShouldBeEmpty();
        await (await admin.GetAsync($"{CampaignsPath}/{campaign}/members?status=bogus", Ct)).ShouldBeValidationErrorAsync("status");

        var page1 = await admin.GetJsonAsync($"{CampaignsPath}/{campaign}/members?pageSize=3&page=1&sort=addedAt");
        var page2 = await admin.GetJsonAsync($"{CampaignsPath}/{campaign}/members?pageSize=3&page=2&sort=addedAt");
        page1.GetProperty("totalCount").GetInt64().ShouldBe(4);
        page1.GetProperty("items").GetArrayLength().ShouldBe(3);
        page2.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await admin.MembersAsync(campaign, "?sort=-addedAt")).First().Str("memberId").ShouldBe(contact.ToString());
        (await admin.MembersAsync(campaign, "?sort=statusChangedAt")).Last().Str("memberId").ShouldBe(ali.ToString());
    }

    [Fact]
    public async Task BulkStatus_UpdatesRows_IgnoresForeignIds_AndRejectsConverted()
    {
        var admin = (await factory.NewOrgAsync("Mkt Bulk Status")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var other = await admin.NewCampaignIdAsync("Diğer");
        var leads = await admin.NewLeadIdsAsync(3);
        await admin.AddMembersAsync(campaign, "lead", leads);
        await admin.AddMembersAsync(other, "lead", leads.Take(1));
        var rows = (await admin.MembersAsync(campaign)).Select(r => r.Id()).ToList();
        var otherRow = (await admin.MembersAsync(other)).Single().Id();
        var url = $"{CampaignsPath}/{campaign}/members/status";

        var sent = await admin.PostJsonAsync(url, new { memberIds = rows.Append(otherRow).Append(Guid.NewGuid()), status = "sent" }, HttpStatusCode.OK);
        (sent.Int("updatedCount"), sent.Int("skippedCount")).ShouldBe((3, 0));
        (await admin.MembersAsync(campaign)).ShouldAllBe(m => m.Str("status") == "sent");
        (await admin.MembersAsync(other)).Single().Str("status").ShouldBe("added", "başka kampanyanın satırına dokunulmaz");

        var again = await admin.PostJsonAsync(url, new { memberIds = rows, status = "sent" }, HttpStatusCode.OK);
        (again.Int("updatedCount"), again.Int("skippedCount")).ShouldBe((0, 0));

        foreach (var status in new[] { "responded", "unsubscribed", "added" })
        {
            (await admin.PostJsonAsync(url, new { memberIds = rows.Take(2), status }, HttpStatusCode.OK)).Int("updatedCount").ShouldBe(2);
        }

        await (await admin.PostAsJsonAsync(url, new { memberIds = rows, status = "converted" }, Ct)).ShouldBeValidationErrorAsync("status");
        await (await admin.PostAsJsonAsync(url, new { memberIds = rows }, Ct)).ShouldBeValidationErrorAsync("status");
        await (await admin.PostAsJsonAsync(url, new { status = "sent" }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        await (await admin.PostAsJsonAsync(url, new { memberIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()), status = "sent" }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        (await admin.PostAsJsonAsync(url, new { memberIds = rows, status = "bogus" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkRemove_IgnoresUnknownIds_IsIdempotent_AndAuditsTheDeletion()
    {
        var admin = (await factory.NewOrgAsync("Mkt Bulk Remove")).Admin;
        var campaign = await admin.NewCampaignIdAsync();
        var other = await admin.NewCampaignIdAsync("Diğer");
        var leads = await admin.NewLeadIdsAsync(3);
        await admin.AddMembersAsync(campaign, "lead", leads);
        await admin.AddMembersAsync(other, "lead", leads.Take(1));
        var rows = (await admin.MembersAsync(campaign)).Select(r => r.Id()).ToList();
        var otherRow = (await admin.MembersAsync(other)).Single().Id();
        var url = $"{CampaignsPath}/{campaign}/members/remove";

        var removed = await admin.PostJsonAsync(url, new { memberIds = rows.Take(2).Append(otherRow).Append(Guid.NewGuid()) }, HttpStatusCode.OK);
        removed.Int("removedCount").ShouldBe(2);
        (await admin.MembersAsync(campaign)).Select(m => m.Id()).ShouldBe([rows[2]]);
        (await admin.MembersAsync(other)).Count.ShouldBe(1, "başka kampanyanın satırı silinmez");
        (await admin.PostJsonAsync(url, new { memberIds = rows.Take(2) }, HttpStatusCode.OK)).Int("removedCount").ShouldBe(0);

        // Fiziksel silme: satır gider; denetim kaydı "deleted" bırakır (kullanıcı = silen).
        (await factory.ScalarAsync<long>("SELECT count(*) FROM marketing.campaign_members WHERE id = @id", ("id", rows[0]))).ShouldBe(0);
        var audit = await admin.GetJsonAsync($"{Base}/audit?entityType=CampaignMember&entityId={rows[0]}");
        audit.GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ShouldBe(["deleted", "created"]);

        // Çıkarılan lead yeniden eklenebilir.
        (await admin.AddMembersAsync(campaign, "lead", leads)).Int("addedCount").ShouldBe(2);
        await (await admin.PostAsJsonAsync(url, new { memberIds = Array.Empty<Guid>() }, Ct)).ShouldBeValidationErrorAsync("memberIds");
    }

    [Fact]
    public async Task ByMember_ListsTheRecordsCampaigns_NewestFirst_AndValidatesTheRecord()
    {
        var admin = (await factory.NewOrgAsync("Mkt By Member")).Admin;
        var lead = (await admin.CreateLeadAsync("Kaya")).Id();
        var contact = (await admin.CreateContactAsync("Demir")).Id();
        var first = await admin.NewCampaignIdAsync("Birinci", "email");
        var second = await admin.NewCampaignIdAsync("İkinci", "event", new { status = "active" });
        var untouched = await admin.NewCampaignIdAsync("Dokunulmamış");
        await admin.AddMembersAsync(first, "lead", [lead]);
        await admin.AddMembersAsync(second, "lead", [lead]);
        await admin.AddMembersAsync(second, "contact", [contact]);
        var secondRow = (await admin.MembersAsync(second, "?memberType=lead")).Single().Id();
        await admin.PostJsonAsync($"{CampaignsPath}/{second}/members/status", new { memberIds = new[] { secondRow }, status = "sent" }, HttpStatusCode.OK);

        var rows = (await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={lead}")).EnumerateArray().ToList();
        rows.Select(r => r.Str("campaignName")).ShouldBe(["İkinci", "Birinci"]);
        var top = rows[0];
        (top.Str("campaignId"), top.Str("campaignType"), top.Str("campaignStatus"), top.Str("membershipId"), top.Str("memberStatus"))
            .ShouldBe((second.ToString(), "event", "active", secondRow.ToString(), "sent"));
        top.Has("addedAt").ShouldBeTrue();
        rows.Select(r => r.Str("campaignId")).ShouldNotContain(untouched.ToString());

        (await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=contact&memberId={contact}")).EnumerateArray().Single().Str("campaignName").ShouldBe("İkinci");
        (await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={(await admin.CreateLeadAsync("Boş")).Id()}")).GetArrayLength().ShouldBe(0);

        // Kayıt yoksa (veya tür uyuşmazsa) not_found; eksik parametre validation.
        await (await admin.GetAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.GetAsync($"{CampaignsPath}/by-member?memberType=contact&memberId={lead}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.GetAsync($"{CampaignsPath}/by-member?memberId={lead}", Ct)).ShouldBeValidationErrorAsync("memberType");
        await (await admin.GetAsync($"{CampaignsPath}/by-member?memberType=lead", Ct)).ShouldBeValidationErrorAsync("memberId");

        // Kayıt silinse de üyelik listelenir olmaz (kayıt doğrulaması); ama kampanyada memberMissing olarak kalır.
        await admin.DeleteJsonAsync($"{Base}/leads/{lead}");
        await (await admin.GetAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={lead}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.MembersAsync(first)).Single().GetProperty("memberMissing").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ByMember_ReturnsAtMostTwoHundredRows()
    {
        var admin = (await factory.NewOrgAsync("Mkt By Member Cap")).Admin;
        var lead = (await admin.CreateLeadAsync("Çok Kampanyalı")).Id();
        await Parallel.ForEachAsync(Enumerable.Range(0, 205), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, _) =>
            await admin.AddMembersAsync(await admin.NewCampaignIdAsync($"Kampanya {i}"), "lead", [lead]));

        (await admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={lead}")).GetArrayLength().ShouldBe(200);
    }

    [Fact]
    public async Task ListMembers_OfAnUnknownCampaign_IsNotFound()
    {
        var admin = (await factory.NewOrgAsync("Mkt Members 404")).Admin;

        await (await admin.GetAsync($"{CampaignsPath}/{Guid.NewGuid()}/members", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.GetAsync($"{CampaignsPath}/{Guid.NewGuid()}/metrics", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }
}
