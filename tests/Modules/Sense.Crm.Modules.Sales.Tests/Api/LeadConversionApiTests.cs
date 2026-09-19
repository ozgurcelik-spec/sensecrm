using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Sales.Domain;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Sales.Tests.Api;

/// <summary>Lead dönüştürme: firma + kişi + fırsat tek transaction'da, LeadConverted outbox'ta, yeniden dönüştürme ve geri alma kuralları.</summary>
[Collection(ApiCollection.Name)]
public sealed class LeadConversionApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Convert_CreatesAccountContactAndDeal_InTheFirstOpenStage()
    {
        var org = await factory.NewOrgAsync("Convert Org");
        var admin = org.Admin;
        var lead = await admin.CreateLeadAsync("Kılıç", "Yeni Şirket A.Ş.", new { FirstName = "Deniz", Email = "deniz@yeni.com", Phone = "0555 000 00 00" });

        var result = await admin.PostJsonAsync(
            $"{Base}/leads/{lead.Id()}/convert",
            new { createDeal = true, dealName = "Yeni Şirket - Lisans", amount = 12500.5m, closingDate = "2026-12-31" },
            HttpStatusCode.OK);
        var (accountId, contactId, dealId) = (result.GetProperty("accountId").GetGuid(), result.GetProperty("contactId").GetGuid(), result.GetProperty("dealId").GetGuid());

        var account = await admin.GetJsonAsync($"{Base}/accounts/{accountId}");
        account.GetProperty("name").GetString().ShouldBe("Yeni Şirket A.Ş.");
        account.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        (account.GetProperty("contactCount").GetInt32(), account.GetProperty("dealCount").GetInt32()).ShouldBe((1, 1));

        var contact = await admin.GetJsonAsync($"{Base}/contacts/{contactId}");
        (contact.GetProperty("fullName").GetString(), contact.GetProperty("email").GetString(), contact.GetProperty("accountId").GetGuid())
            .ShouldBe(("Deniz Kılıç", "deniz@yeni.com", accountId));

        var deal = await admin.GetJsonAsync($"{Base}/deals/{dealId}");
        (deal.GetProperty("name").GetString(), deal.GetProperty("amount").GetDecimal(), deal.GetProperty("currency").GetString(), deal.GetProperty("closingDate").GetString())
            .ShouldBe(("Yeni Şirket - Lisans", 12500.5m, "TRY", "2026-12-31"));
        (deal.GetProperty("stageName").GetString(), deal.GetProperty("stageKind").GetString(), deal.GetProperty("probability").GetInt32()).ShouldBe(("Nitelendirme", "open", 10));
        (deal.GetProperty("accountId").GetGuid(), deal.GetProperty("contactId").GetGuid(), deal.GetProperty("contactName").GetString()).ShouldBe((accountId, contactId, "Deniz Kılıç"));

        var converted = await admin.GetJsonAsync($"{Base}/leads/{lead.Id()}");
        converted.GetProperty("status").GetString().ShouldBe("converted");
        (converted.GetProperty("convertedAccountId").GetGuid(), converted.GetProperty("convertedContactId").GetGuid(), converted.GetProperty("convertedDealId").GetGuid())
            .ShouldBe((accountId, contactId, dealId));
        converted.GetProperty("convertedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        // Dönüşmüş lead salt-okur; tekrar dönüştürülemez; ikinci deneme yeni kayıt üretmez.
        await (await admin.PutAsJsonAsync($"{Base}/leads/{lead.Id()}", new { lastName = "Kılıç", company = "X" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "lead.already_converted");
        await (await admin.PostAsJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { createDeal = false }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "lead.already_converted");
        (await admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{Base}/contacts")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{Base}/deals")).GetProperty("totalCount").GetInt32().ShouldBe(1);

        // LeadConverted integration event'i aynı transaction'da outbox'a yazıldı ve Worker'ın yaptığı işle tüketilir.
        var pending = await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadConverted", lead.Id().ToString());
        var message = pending.ShouldHaveSingleItem();
        message.TenantId.ShouldBe(org.TenantId);
        message.Payload.ShouldContain(accountId.ToString());
        message.Payload.ShouldContain(dealId.ToString());
        await factory.DrainOutboxAsync<SalesDbContext>();
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadConverted", lead.Id().ToString())).Single().ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Convert_WithExistingAccount_AndWithoutDeal()
    {
        var admin = (await factory.NewOrgAsync("Convert Existing")).Admin;
        var existing = await admin.CreateAccountAsync("Mevcut Firma");
        var lead = await admin.CreateLeadAsync("Aydın", "Başka Ad Ltd");

        var result = await admin.PostJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { accountId = existing.Id(), createDeal = false }, HttpStatusCode.OK);

        result.GetProperty("accountId").GetGuid().ShouldBe(existing.Id());
        result.TryGetProperty("dealId", out _).ShouldBeFalse();
        (await admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(1, "yeni firma açılmaz");
        (await admin.GetJsonAsync($"{Base}/accounts/{existing.Id()}")).GetProperty("contactCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{Base}/deals")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await admin.GetJsonAsync($"{Base}/leads/{lead.Id()}")).TryGetProperty("convertedDealId", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Convert_Validation_AndMissingReferences_LeaveNothingBehind()
    {
        var org = await factory.NewOrgAsync("Convert Rollback");
        var admin = org.Admin;
        var other = await factory.NewOrgAsync("Convert Rollback Other");
        var foreignAccount = await other.Admin.CreateAccountAsync("Yabancı Firma");
        var lead = await admin.CreateLeadAsync("Test", "Geri Alınacak A.Ş.");

        await (await admin.PostAsJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { createDeal = true }, Ct)).ShouldBeValidationErrorAsync("dealName");
        await (await admin.PostAsJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { createDeal = true, dealName = "D", pipelineId = Guid.NewGuid() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { createDeal = false, accountId = foreignAccount.Id() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{Base}/leads/{Guid.NewGuid()}/convert", new { createDeal = false }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        (await admin.GetJsonAsync($"{Base}/leads/{lead.Id()}")).GetProperty("status").GetString().ShouldBe("new");
        (await admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await admin.GetJsonAsync($"{Base}/contacts")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadConverted", lead.Id().ToString())).ShouldBeEmpty();
        (await other.Admin.GetJsonAsync($"{Base}/accounts/{foreignAccount.Id()}")).GetProperty("contactCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Convert_IsAtomic_WhenAWriteFailsMidway()
    {
        // Çıktı garantisi: firma + kişi ekledikten sonra fırsat yazımı patlarsa hiçbiri kalıcı olmaz (tek transaction).
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddScoped<IDealRepository, ExplodingDealRepository>();
        }));
        var client = failing.CreateClient();
        var auth = await client.SignUpAsync("Convert Atomic", UniqueEmail("admin"));
        client.WithToken(auth.AccessToken);
        var lead = await client.CreateLeadAsync("Atomik", "Atomik A.Ş.");

        var response = await client.PostAsJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { createDeal = true, dealName = "Patlayacak" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await client.GetJsonAsync($"{Base}/leads/{lead.Id()}")).GetProperty("status").GetString().ShouldBe("new");
        (await client.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await client.GetJsonAsync($"{Base}/contacts")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadConverted", lead.Id().ToString())).ShouldBeEmpty();
    }

    [Fact]
    public async Task Convert_WithDeal_RequiresDealsWritePermission_Additionally()
    {
        var org = await factory.NewOrgAsync("Convert Perms");
        var (member, _) = await factory.AddMemberAsync(org, "Lead Sorumlusu", "crm.leads.read", "crm.leads.write", "crm.accounts.write", "crm.contacts.write");
        var withDeal = await member.CreateLeadAsync("Bir", "Bir A.Ş.");
        var withoutDeal = await member.CreateLeadAsync("Iki", "Iki A.Ş.");

        await (await member.PostAsJsonAsync($"{Base}/leads/{withDeal.Id()}/convert", new { createDeal = true, dealName = "Fırsat" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await org.Admin.GetJsonAsync($"{Base}/leads/{withDeal.Id()}")).GetProperty("status").GetString().ShouldBe("new");

        await member.PostJsonAsync($"{Base}/leads/{withoutDeal.Id()}/convert", new { createDeal = false }, HttpStatusCode.OK);

        var (noLeadWrite, _) = await factory.AddMemberAsync(org, "Yetkisiz", "crm.leads.read", "crm.accounts.write", "crm.contacts.write");
        await (await noLeadWrite.PostAsJsonAsync($"{Base}/leads/{withDeal.Id()}/convert", new { createDeal = false }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    private sealed class ExplodingDealRepository : IDealRepository
    {
        public Task<Sense.Crm.Modules.Sales.Domain.Deals.Deal?> GetByIdAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("boom");

        public void Add(Sense.Crm.Modules.Sales.Domain.Deals.Deal deal) => throw new InvalidOperationException("boom");

        public void Remove(Sense.Crm.Modules.Sales.Domain.Deals.Deal deal) => throw new InvalidOperationException("boom");
    }
}
