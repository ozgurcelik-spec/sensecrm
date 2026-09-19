using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>
/// Numaralandırma: kiracı + tür + yıl bazında ardışık, eşzamanlılığa dayanıklı, boşluksuz (sayaç belge INSERT'iyle aynı transaction'da);
/// yıl kiracı saat diliminde hesaplanır.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NumberingApiTests(CrmApiFactory factory)
{
    private static int TenantYear() => TenantToday().Year;

    [Fact]
    public async Task TwentyParallelQuoteCreates_GetUniqueGaplessNumbers_AndOrderCounterIsIndependent()
    {
        var org = await factory.NewOrgAsync("Numara Paralel");
        var account = await org.Admin.CreateAccountAsync("Firma");
        var year = TenantYear();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var response = await org.Admin.PostAsJsonAsync(QuotesPath, new { subject = $"Paralel {i}", accountId = account.Id(), lines = new[] { Line() } }, Ct);
            return (Response: response, Body: await response.Content.ReadAsStringAsync(Ct));
        }));

        responses.ShouldAllBe(r => r.Response.StatusCode == HttpStatusCode.Created, string.Join(" | ", responses.Where(r => r.Response.StatusCode != HttpStatusCode.Created).Select(r => r.Body)));
        var numbers = responses.Select(r => System.Text.Json.JsonDocument.Parse(r.Body).RootElement.Str("number")).ToList();
        numbers.Distinct().Count().ShouldBe(20, "numaralar tekil");
        numbers.Order(StringComparer.Ordinal).ShouldBe(Enumerable.Range(1, 20).Select(n => $"Q-{year}-{n:D4}"), "0001-0020 kesintisiz");
        (await factory.CounterAsync(org.TenantId, "quote", year)).ShouldBe(20);
        (await factory.CounterAsync(org.TenantId, "order", year)).ShouldBe(0, "sipariş sayacı bağımsız");

        var order1 = await org.Admin.CreateOrderAsync(account.Id());
        var order2 = await org.Admin.CreateOrderAsync(account.Id());
        order1.Str("number").ShouldBe($"SO-{year}-0001");
        order2.Str("number").ShouldBe($"SO-{year}-0002");
        (await factory.CounterAsync(org.TenantId, "quote", year)).ShouldBe(20);
    }

    [Fact]
    public async Task TenLooseParallelOrderCreates_AreGapless()
    {
        var org = await factory.NewOrgAsync("Numara Sipariş Paralel");
        var account = await org.Admin.CreateAccountAsync("Firma");
        var year = TenantYear();

        var numbers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            (await org.Admin.CreateOrderAsync(account.Id(), new { subject = $"Sipariş {i}" })).Str("number")));

        numbers.Order(StringComparer.Ordinal).ShouldBe(Enumerable.Range(1, 10).Select(n => $"SO-{year}-{n:D4}"));
    }

    [Fact]
    public async Task EachTenant_HasItsOwnSequence()
    {
        var a = await factory.NewOrgAsync("Numara A");
        var b = await factory.NewOrgAsync("Numara B");
        var accountA = await a.Admin.CreateAccountAsync("A firma");
        var accountB = await b.Admin.CreateAccountAsync("B firma");
        var year = TenantYear();

        (await a.Admin.CreateQuoteAsync(accountA.Id())).Str("number").ShouldBe($"Q-{year}-0001");
        (await b.Admin.CreateQuoteAsync(accountB.Id())).Str("number").ShouldBe($"Q-{year}-0001");
        (await a.Admin.CreateQuoteAsync(accountA.Id())).Str("number").ShouldBe($"Q-{year}-0002");
        (await b.Admin.CreateOrderAsync(accountB.Id())).Str("number").ShouldBe($"SO-{year}-0001");
        (await factory.CounterAsync(a.TenantId, "quote", year)).ShouldBe(2);
        (await factory.CounterAsync(b.TenantId, "quote", year)).ShouldBe(1);
    }

    [Fact]
    public async Task DeletedNumbers_AreNeverReused()
    {
        var org = await factory.NewOrgAsync("Numara Silinen");
        var account = await org.Admin.CreateAccountAsync("Firma");
        var year = TenantYear();
        var first = await org.Admin.CreateQuoteAsync(account.Id());
        var second = await org.Admin.CreateQuoteAsync(account.Id());
        await org.Admin.DeleteJsonAsync($"{QuotesPath}/{second.Id()}");

        var third = await org.Admin.CreateQuoteAsync(account.Id());

        first.Str("number").ShouldBe($"Q-{year}-0001");
        third.Str("number").ShouldBe($"Q-{year}-0003", "silinmiş belgenin numarası yeniden kullanılmaz (boşluk yalnız silmede)");
    }

    [Fact]
    public async Task NumberUniqueIndex_IsTheBackstop_IncludingSoftDeletedRows()
    {
        var org = await factory.NewOrgAsync("Numara Yedek");
        var account = await org.Admin.CreateAccountAsync("Firma");
        var quote = await org.Admin.CreateQuoteAsync(account.Id());
        await org.Admin.DeleteJsonAsync($"{QuotesPath}/{quote.Id()}");

        var act = () => factory.ExecuteAsync(
            "INSERT INTO commerce.quotes (id, status, created_at, tenant_id, number, subject, account_id, owner_user_id, currency, subtotal, discount_total, tax_total, grand_total, is_deleted) " +
            "VALUES (@id, 'Draft', now(), @t, @n, 's', @a, @o, 'TRY', 0, 0, 0, 0, false)",
            ("id", Guid.NewGuid()), ("t", org.TenantId), ("n", quote.Str("number")), ("a", account.Id()), ("o", org.AdminUserId));

        var exception = await Should.ThrowAsync<Npgsql.PostgresException>(act);
        exception.SqlState.ShouldBe("23505");
        exception.ConstraintName.ShouldBe("ux_quotes_tenant_number");
    }

    [Fact]
    public async Task Year_IsComputedInTheTenantTimeZone_AndSequenceRestartsEachYear()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "Numara Yıl");
        var account = await org.Admin.CreateAccountAsync("Firma");

        // Europe/Istanbul (UTC+3): 2026-12-31T20:30Z hâlâ 2026; 21:30Z = 2027-01-01 yerel.
        clock.Override = new DateTimeOffset(2026, 12, 31, 20, 30, 0, TimeSpan.Zero);
        var lastOf2026 = await org.Admin.CreateQuoteAsync(account.Id());
        clock.Override = new DateTimeOffset(2026, 12, 31, 21, 30, 0, TimeSpan.Zero);
        var firstOf2027 = await org.Admin.CreateQuoteAsync(account.Id());
        var secondOf2027 = await org.Admin.CreateQuoteAsync(account.Id());
        var order2027 = await org.Admin.CreateOrderAsync(account.Id());
        clock.Override = new DateTimeOffset(2026, 12, 31, 20, 45, 0, TimeSpan.Zero);
        var secondOf2026 = await org.Admin.CreateQuoteAsync(account.Id());
        clock.Override = null;

        lastOf2026.Str("number").ShouldBe("Q-2026-0001");
        firstOf2027.Str("number").ShouldBe("Q-2027-0001", "yıl kiracı saat diliminde: Istanbul'da 2026-12-31T21:30Z = 2027-01-01");
        secondOf2027.Str("number").ShouldBe("Q-2027-0002");
        order2027.Str("number").ShouldBe("SO-2027-0001");
        order2027.Str("orderDate").ShouldBe("2027-01-01", "orderDate bugün (kiracı saati)");
        secondOf2026.Str("number").ShouldBe("Q-2026-0002");
        (await factory.CounterAsync(org.TenantId, "quote", 2026)).ShouldBe(2);
        (await factory.CounterAsync(org.TenantId, "quote", 2027)).ShouldBe(2);
    }

    [Fact]
    public async Task FailedSave_RollsTheCounterBack_SoTheNextNumberIsGapless()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "Numara Geri Alma");
        var account = await org.Admin.CreateAccountAsync("Firma");
        var year = TenantYear();
        (await org.Admin.CreateQuoteAsync(account.Id())).Str("number").ShouldBe($"Q-{year}-0001");

        Faults.Mode = Faults.FailOnQuoteInsert;
        try
        {
            var failed = await org.Admin.PostAsJsonAsync(QuotesPath, new { subject = "Patlayan", accountId = account.Id(), lines = new[] { Line() } }, Ct);
            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            Faults.Mode = null;
        }

        (await factory.CountAsync("quotes", org.TenantId)).ShouldBe(1, "başarısız teklif kalıcı olmadı");
        (await factory.CounterAsync(org.TenantId, "quote", year)).ShouldBe(1, "sayaç geri döndü");
        (await org.Admin.CreateQuoteAsync(account.Id())).Str("number").ShouldBe($"Q-{year}-0002", "boşluksuz");
    }
}
