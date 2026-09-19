using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Service.Domain;
using Sense.Crm.Modules.Service.Domain.Cases;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;
using Case = Sense.Crm.Modules.Service.Domain.Cases.Case;

namespace Sense.Crm.Modules.Service.Tests.Api;

/// <summary>Numaralama <c>C-{yıl}-{sıra:D4}</c>: ardışık, eşzamanlı güvenli ve boşluksuz, kiracı saat diliminde yıl, geri almada yanmaz.</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseNumberingApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Numbers_AreSequential_PerTenant_AndTenantsAreIndependent()
    {
        var a = await factory.NewOrgAsync("Numbering A");
        var b = await factory.NewOrgAsync("Numbering B");
        var year = DateTime.UtcNow.Year;

        var first = (await a.Admin.CreateCaseAsync("A1")).Str("number");
        var numbersA = new List<string> { first };
        for (var i = 2; i <= 3; i++)
        {
            numbersA.Add((await a.Admin.CreateCaseAsync($"A{i}")).Str("number"));
        }

        var firstB = (await b.Admin.CreateCaseAsync("B1")).Str("number");

        numbersA.ShouldBe([$"C-{year}-0001", $"C-{year}-0002", $"C-{year}-0003"]);
        firstB.ShouldBe($"C-{year}-0001", "iki kiracının sayaçları bağımsız");
        (await b.Admin.CreateCaseAsync("B2")).Str("number").ShouldBe($"C-{year}-0002");
        (await a.Admin.CreateCaseAsync("A4")).Str("number").ShouldBe($"C-{year}-0004");
    }

    [Fact]
    public async Task ConcurrentCreates_YieldUniqueGaplessNumbers()
    {
        var admin = (await factory.NewOrgAsync("Numbering Concurrent")).Admin;

        var responses = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => admin.PostAsJsonAsync(CasesPath, new { subject = $"Eşzamanlı {i}" }, Ct)));

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        }

        var numbers = (await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).Str("number")))).ToList();
        numbers.Distinct().Count().ShouldBe(20);
        numbers.Select(n => int.Parse(n.Split('-')[2], System.Globalization.CultureInfo.InvariantCulture)).Order().ShouldBe(Enumerable.Range(1, 20));
        (await admin.ListNumbersAsync("?pageSize=100")).Count.ShouldBe(20);
    }

    [Fact]
    public async Task TheYearBoundary_FollowsTheTenantTimeZone_NotUtc()
    {
        using var host = new ClockedHost(factory);

        // 31 Aralık 23:30 UTC = Europe/Istanbul'da 1 Ocak 02:30 → yeni yıl; UTC hâlâ 2026.
        var org = await host.NewOrgAsync("Numbering Year", new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero));
        (await org.Admin.CreateCaseAsync("Yeni yıl")).Str("number").ShouldBe("C-2027-0001");
        (await org.Admin.CreateCaseAsync("Yeni yıl 2")).Str("number").ShouldBe("C-2027-0002");

        // 31 Aralık 20:30 UTC = yerel 23:30 → hâlâ 2026; sayaç yıla göre ayrıdır.
        host.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 31, 20, 30, 0, TimeSpan.Zero));
        (await org.Admin.CreateCaseAsync("Eski yıl")).Str("number").ShouldBe("C-2026-0001");

        host.Clock.SetUtcNow(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        (await org.Admin.CreateCaseAsync("Yeni yıl 3")).Str("number").ShouldBe("C-2027-0003");
    }

    [Fact]
    public async Task ThePaddingGrowsPastNineThousandNineHundredNinetyNine()
    {
        var org = await factory.NewOrgAsync("Numbering Big");
        (await org.Admin.CreateCaseAsync("İlk")).Str("number").ShouldEndWith("-0001");
        await factory.ExecuteAsync("UPDATE service.case_counters SET last_value = 9998 WHERE tenant_id = @t", ("t", org.TenantId));

        (await org.Admin.CreateCaseAsync("9999.")).Str("number").ShouldEndWith("-9999");
        (await org.Admin.CreateCaseAsync("10000.")).Str("number").ShouldEndWith("-10000");
        (await org.Admin.CreateCaseAsync("10001.")).Str("number").ShouldEndWith("-10001");
    }

    [Fact]
    public async Task ARolledBackCreate_DoesNotBurnANumber_AndTheCounterRollsBackToo()
    {
        // Sayaç adımı çalıştıktan sonra INSERT aşaması patlar → transaction geri alınır → numara tüketilmemiş olmalı.
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddScoped<ICaseRepository, ExplodingCaseRepository>();
        }));
        var client = failing.CreateClient();
        var auth = await client.SignUpAsync("Numbering Rollback", UniqueEmail("admin"));
        client.WithToken(auth.AccessToken);
        var tenantId = (await client.GetJsonAsync($"{Base}/me")).GetProperty("organization").Id();

        (await client.CreateCaseAsync("Sağlam bir")).Str("number").ShouldEndWith("-0001");

        var boom = await client.PostAsJsonAsync(CasesPath, new { subject = ExplodingCaseRepository.Trigger }, Ct);
        boom.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        (await factory.ScalarAsync<int>("SELECT last_value FROM service.case_counters WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(1, "sayaç geri alındı");
        (await client.CreateCaseAsync("Sağlam iki")).Str("number").ShouldEndWith("-0002");
        (await factory.ScalarAsync<int>("SELECT last_value FROM service.case_counters WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(2);
        (await client.ListNumbersAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task InvalidRequests_DoNotConsumeNumbers()
    {
        var org = await factory.NewOrgAsync("Numbering Validation");
        var admin = org.Admin;

        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", accountId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.account_not_found");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", assignedUserId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "" }, Ct)).ShouldBeValidationErrorAsync("subject");

        (await admin.CreateCaseAsync("İlk")).Str("number").ShouldEndWith("-0001");
    }

    /// <summary>Gerçek depoyu sarar; belirli bir konuda <c>Add</c> aşamasında patlar (sayaç adımından sonra).</summary>
    private sealed class ExplodingCaseRepository(ServiceDbContext db) : ICaseRepository
    {
        public const string Trigger = "Patlayacak";

        private readonly CaseRepository _inner = new(db);

        public Task<Case?> GetByIdAsync(Guid id, CancellationToken ct) => _inner.GetByIdAsync(id, ct);

        public void Add(Case entity)
        {
            if (entity.Subject == Trigger)
            {
                throw new InvalidOperationException("boom");
            }

            _inner.Add(entity);
        }

        public void Remove(Case entity) => _inner.Remove(entity);

        public void AddEvent(CaseEvent caseEvent) => _inner.AddEvent(caseEvent);

        public void AddComment(CaseComment comment) => _inner.AddComment(comment);

        public Task<bool> TrySaveAsync(CancellationToken ct) => _inner.TrySaveAsync(ct);
    }
}
