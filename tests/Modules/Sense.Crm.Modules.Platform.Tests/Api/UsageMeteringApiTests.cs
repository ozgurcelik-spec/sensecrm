using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Usage;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// Kullanım ölçümü (M7): her modülün <c>IUsageReporter</c>'ı bilinen veri kümesinde elle hesaplanmış değerler döner; günlük anlık görüntü işi (idempotent, askıdaki dahil,
/// silme bekleyen/silinmiş hariç, hata yalıtımı, kiracı kapsamı, saklama); kullanım serisi aralık kuralları; canlı yenileme; finans CSV dışa aktarma.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UsageMeteringApiTests(CrmApiFactory factory)
{
    // ---- IUsageReporter doğruluğu --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reporters_ReturnHandComputedValues_ExcludingSoftDeletedRows_AndOtherTenants()
    {
        var a = await factory.NewOrgAsync(Token("rpa") + " metrics A");
        var b = await factory.NewOrgAsync(Token("rpb") + " metrics B");
        var standard = await RoleIdAsync(a.Admin, "Standard");
        var admin = await RoleIdAsync(a.Admin, "Administrator");

        // ---- A: Sales (4 hesap - 1 silinen, 2 kişi, 5 lead - 1 silinen, 1 fırsat) ----
        var accountIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            accountIds.Add((await CreateAsync(a.Admin, "accounts", new { name = $"Firma {i}" })).GuidProp("id"));
        }

        await CreateAsync(a.Admin, "contacts", new { firstName = "Ali", lastName = "Bir", accountId = accountIds[0] });
        await CreateAsync(a.Admin, "contacts", new { firstName = "Veli", lastName = "Iki" });
        var leadIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            leadIds.Add((await CreateAsync(a.Admin, "leads", new { lastName = $"Aday {i}", company = "Sirket" })).GuidProp("id"));
        }

        await CreateAsync(a.Admin, "deals", new { name = "Buyuk firsat", accountId = accountIds[1], amount = 1000m });
        await RemoveAsync(a.Admin, $"accounts/{accountIds[3]}");
        await RemoveAsync(a.Admin, $"leads/{leadIds[4]}");

        // ---- A: Activities (3 - 1 silinen) ----
        var activityIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            activityIds.Add((await CreateAsync(a.Admin, "activities", new { type = "task", subject = $"Gorev {i}" })).GuidProp("id"));
        }

        await RemoveAsync(a.Admin, $"activities/{activityIds[2]}");

        // ---- A: Workflows (2 kural - 1 silinen) ----
        await CreateAsync(a.Admin, "workflows/rules", new { name = "Lead atama", kind = "leadAssignment", @params = new { assigneeRoleId = admin } });
        var dealRule = await CreateAsync(a.Admin, "workflows/rules", new { name = "Firsat onayi", kind = "dealApproval", @params = new { minAmount = 500m, approverRoleId = admin } });
        await RemoveAsync(a.Admin, $"workflows/rules/{dealRule.GuidProp("id")}");

        // ---- A: Commerce (3 urun - 1 silinen, 1 teklif, 2 siparis) ----
        var productIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            productIds.Add((await CreateAsync(a.Admin, "products", new { name = $"Urun {i}", unitPrice = 100m, taxRate = 20m })).GuidProp("id"));
        }

        await RemoveAsync(a.Admin, $"products/{productIds[2]}");
        var line = new { description = "Lisans", quantity = 2m, unitPrice = 100m, discountPercent = 0m, taxRate = 20m };
        await CreateAsync(a.Admin, "quotes", new { subject = "Teklif", accountId = accountIds[0], lines = new[] { line } });
        await CreateAsync(a.Admin, "orders", new { subject = "Siparis 1", accountId = accountIds[0], lines = new[] { line } });
        await CreateAsync(a.Admin, "orders", new { subject = "Siparis 2", accountId = accountIds[1], lines = new[] { line } });

        // M9C: 2 tedarikci (1 silinen), 1 satin alma emri, 1 fiyat listesi (+ 1 girdi: sayilmaz), 2 fatura (1 silinen; kalemler ve tahsilat sayilmaz).
        var vendorOne = await CreateAsync(a.Admin, "vendors", new { name = "Tedarikci 1" });
        var vendorTwo = await CreateAsync(a.Admin, "vendors", new { name = "Tedarikci 2" });
        await RemoveAsync(a.Admin, $"vendors/{vendorTwo.GuidProp("id")}");
        await CreateAsync(a.Admin, "purchase-orders", new { subject = "PO", vendorId = vendorOne.GuidProp("id"), lines = new[] { line } });
        var book = await CreateAsync(a.Admin, "pricebooks", new { name = "Liste", pricingModel = "perProduct" });
        await a.Admin.SendJsonAsync(HttpMethod.Put, $"{Base}/pricebooks/{book.GuidProp("id")}/entries/{productIds[0]}", new { unitPrice = 90m }, HttpStatusCode.NoContent);
        var invoiceOne = await CreateAsync(a.Admin, "invoices", new { subject = "Fatura 1", accountId = accountIds[0], lines = new[] { line } });
        var invoiceTwo = await CreateAsync(a.Admin, "invoices", new { subject = "Fatura 2", accountId = accountIds[0], lines = new[] { line } });
        await a.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/invoices/{invoiceOne.GuidProp("id")}/send", null, HttpStatusCode.NoContent);
        await a.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/invoices/{invoiceOne.GuidProp("id")}/payments", new { amount = 10m }, HttpStatusCode.Created);
        await RemoveAsync(a.Admin, $"invoices/{invoiceTwo.GuidProp("id")}");

        // ---- A: Service (4 talep - 1 silinen) ----
        var caseIds = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            caseIds.Add((await CreateAsync(a.Admin, "cases", new { subject = $"Talep {i}" })).GuidProp("id"));
        }

        await RemoveAsync(a.Admin, $"cases/{caseIds[3]}");

        // ---- A: Marketing (3 kampanya - 1 silinen) ----
        var campaignIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            campaignIds.Add((await CreateAsync(a.Admin, "campaigns", new { name = $"Kampanya {i}", type = "email" })).GuidProp("id"));
        }

        await RemoveAsync(a.Admin, $"campaigns/{campaignIds[2]}");

        // ---- A: kullanıcılar: yönetici + etkin üye = 2 etkin; pasifleştirilen sayılmaz; başka hesaba davet = 1 bekleyen ----
        await factory.AddMemberAsync(a.Admin, "Etkin Uye", standard);
        var inactive = await factory.AddMemberAsync(a.Admin, "Pasif Uye", standard);
        await a.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{inactive.UserId}", new { isActive = false }, HttpStatusCode.NoContent);
        var invited = await a.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/members", new { email = b.AdminEmail, displayName = "Davetli", roleId = standard }, HttpStatusCode.Created);
        invited.Str("status").ShouldBe("pending");

        // ---- B: başka kiracı (A'nın hiçbir sayacına girmemeli) ----
        for (var i = 0; i < 7; i++)
        {
            await CreateAsync(b.Admin, "accounts", new { name = $"B Firma {i}" });
        }

        for (var i = 0; i < 6; i++)
        {
            await CreateAsync(b.Admin, "leads", new { lastName = $"B Aday {i}", company = "B" });
        }

        for (var i = 0; i < 4; i++)
        {
            await CreateAsync(b.Admin, "cases", new { subject = $"B Talep {i}" });
        }

        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(b.Admin, "activities", new { type = "task", subject = $"B Gorev {i}" });
        }

        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(b.Admin, "campaigns", new { name = $"B Kampanya {i}", type = "email" });
        }

        var reported = await ReportAsync(a.TenantId);

        foreach (var module in new[] { "identity", "sales", "activities", "workflows", "commerce", "service", "marketing" })
        {
            reported.ShouldContainKey(module);
        }

        reported["identity"].ShouldBe(new Dictionary<string, long> { ["identity.users_active"] = 2, ["identity.users_pending"] = 1, ["identity.profile_completed"] = 0 }, ignoreOrder: true);
        reported["sales"].ShouldBe(new Dictionary<string, long> { ["sales.accounts"] = 3, ["sales.contacts"] = 2, ["sales.leads"] = 4, ["sales.deals"] = 1, ["sales.records"] = 10 }, ignoreOrder: true);
        reported["activities"].ShouldBe(new Dictionary<string, long> { ["activities.activities"] = 2, ["activities.records"] = 2 }, ignoreOrder: true);
        reported["workflows"].ShouldBe(new Dictionary<string, long> { ["workflows.workflow_rules"] = 1, ["workflows.records"] = 1 }, ignoreOrder: true);
        reported["commerce"].ShouldBe(
            new Dictionary<string, long>
            {
                ["commerce.products"] = 2,
                ["commerce.quotes"] = 1,
                ["commerce.orders"] = 2,
                ["commerce.invoices"] = 1,
                ["commerce.purchase_orders"] = 1,
                ["commerce.vendors"] = 1,
                ["commerce.price_books"] = 1,
                ["commerce.records"] = 9,
            },
            ignoreOrder: true);
        reported["service"].ShouldBe(new Dictionary<string, long> { ["service.cases"] = 3, ["service.records"] = 3 }, ignoreOrder: true);
        reported["marketing"].ShouldBe(new Dictionary<string, long> { ["marketing.campaigns"] = 2, ["marketing.records"] = 2 }, ignoreOrder: true);

        // Silme yumuşaktır (satırlar durur) ama sayaç yaşayanları sayar.
        (await factory.ScalarAsync<long>("SELECT count(*) FROM sales.accounts WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(4);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM sales.leads WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(5);

        // Profil bilgisi kaydedilince 0 → 1.
        var organization = await a.Admin.GetJsonAsync($"{Base}/organization");
        await a.Admin.SendJsonAsync(
            HttpMethod.Put, $"{Base}/organization", new { name = organization.Str("name"), defaultLocale = organization.Str("defaultLocale"), timeZone = organization.Str("timeZone") }, HttpStatusCode.NoContent);
        (await ReportAsync(a.TenantId))["identity"]["identity.profile_completed"].ShouldBe(1);

        // B: yalnız kendi verisi; A'nın davetli üyeliği B'nin sayaçlarında yok.
        var reportedB = await ReportAsync(b.TenantId);
        reportedB["sales"].ShouldBe(new Dictionary<string, long> { ["sales.accounts"] = 7, ["sales.contacts"] = 0, ["sales.leads"] = 6, ["sales.deals"] = 0, ["sales.records"] = 13 }, ignoreOrder: true);
        reportedB["service"]["service.cases"].ShouldBe(4);
        reportedB["activities"]["activities.records"].ShouldBe(5);
        reportedB["marketing"]["marketing.records"].ShouldBe(3);
        reportedB["commerce"]["commerce.records"].ShouldBe(0);
        reportedB["workflows"]["workflows.records"].ShouldBe(0);
        reportedB["identity"]["identity.users_active"].ShouldBe(1);
        reportedB["identity"]["identity.users_pending"].ShouldBe(0);
    }

    [Fact]
    public async Task EveryModuleRecordsTotal_IsTheSumOfItsSubCounts()
    {
        var org = await factory.NewOrgAsync(Token("sum") + " sums");
        var account = (await CreateAsync(org.Admin, "accounts", new { name = "Toplam Firma" })).GuidProp("id");
        await CreateAsync(org.Admin, "contacts", new { lastName = "Kisi" });
        await CreateAsync(org.Admin, "leads", new { lastName = "Aday", company = "Sirket" });
        await CreateAsync(org.Admin, "products", new { name = "Urun", unitPrice = 10m, taxRate = 20m });
        var line = new { description = "Kalem", quantity = 1m, unitPrice = 10m, discountPercent = 0m, taxRate = 20m };
        await CreateAsync(org.Admin, "quotes", new { subject = "T", accountId = account, lines = new[] { line } });
        await CreateAsync(org.Admin, "orders", new { subject = "S", accountId = account, lines = new[] { line } });

        var reported = await ReportAsync(org.TenantId);

        // "files" (M8C) reports storage metrics (files, storage_bytes), not a record count: no records limit exists for it.
        foreach (var (module, metrics) in reported.Where(r => r.Key is not ("identity" or "files")))
        {
            var records = metrics[$"{module}.records"];
            var parts = metrics.Where(m => m.Key != $"{module}.records").Sum(m => m.Value);
            records.ShouldBe(parts, $"{module}.records = alt toplamların toplamı");
        }

        reported["sales"]["sales.records"].ShouldBe(3);
        reported["commerce"]["commerce.records"].ShouldBe(3);
    }

    // ---- UsageSnapshotJob ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SnapshotJob_IsIdempotent_TwoRunsOnTheSameDayLeaveOneRowPerTenant()
    {
        var org = await factory.SyncedOrgAsync(Token("idm") + " job idempotent");
        await CreateAsync(org.Admin, "accounts", new { name = "Bir" });
        await CreateAsync(org.Admin, "accounts", new { name = "Iki" });
        await using var host = new ConsoleClockHost(factory);
        var job = host.Services.GetRequiredService<UsageSnapshotJob>();
        var today = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime);

        var first = await job.RunOnceAsync(Ct);

        first.Written.ShouldBeGreaterThanOrEqualTo(1);
        (await SnapshotRowsAsync(org.TenantId, today)).ShouldBe(1);
        var metrics = await SnapshotMetricsAsync(org.TenantId, today);
        metrics["sales.accounts"].ShouldBe(2);
        metrics["sales.records"].ShouldBe(2);
        (await factory.ScalarAsync<int>("SELECT users_active FROM platform.usage_snapshots WHERE tenant_id = @t AND day = @d", ("t", org.TenantId), ("d", today))).ShouldBe(1);

        await CreateAsync(org.Admin, "accounts", new { name = "Uc" });
        await job.RunOnceAsync(Ct);

        (await SnapshotRowsAsync(org.TenantId, today)).ShouldBe(1, "aynı gün ikinci koşu ikinci satır üretmez");
        (await SnapshotMetricsAsync(org.TenantId, today))["sales.accounts"].ShouldBe(2, "bugünün anlık görüntüsü olan kiracı yeniden sayılmaz");
    }

    [Fact]
    public async Task SnapshotJob_IncludesSuspendedTenants_AndExcludesPendingDeletionAndDeleted()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("inc");
        var active = await factory.SyncedOrgAsync($"{t} active");
        var suspended = await factory.SyncedOrgAsync($"{t} suspended");
        var trialExpired = await factory.SyncedOrgAsync($"{t} expired");
        var pending = await factory.SyncedOrgAsync($"{t} pending");
        var deleted = await factory.SyncedOrgAsync($"{t} deleted");
        await platform.SuspendAsync(suspended.TenantId, "test", "blocked");
        await factory.SetAccountAsync(factory, trialExpired.TenantId, "trial_ends_on = current_date - 3, trial_ends_at = now() - interval '2 days'");
        (await platform.RequestDeletionRawAsync(pending.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await factory.SetAccountAsync(factory, deleted.TenantId, "status = 'deleted'");
        await using var host = new ConsoleClockHost(factory);
        var today = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime);

        await host.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);

        (await SnapshotRowsAsync(active.TenantId, today)).ShouldBe(1);
        (await SnapshotRowsAsync(suspended.TenantId, today)).ShouldBe(1, "askıdaki kiracı da ölçülür (tam engel olsa bile)");
        (await SnapshotRowsAsync(trialExpired.TenantId, today)).ShouldBe(1, "deneme bitmiş (saklanan durum active) ölçülür");
        (await SnapshotRowsAsync(pending.TenantId, today)).ShouldBe(0, "silme bekleyen ölçülmez");
        (await SnapshotRowsAsync(deleted.TenantId, today)).ShouldBe(0, "silinmiş ölçülmez");
    }

    [Fact]
    public async Task SnapshotJob_AReporterFailureForOneTenant_DoesNotBlockTheOthers_AndIsRetriedOnTheNextRound()
    {
        var t = Token("chs");
        var broken = await factory.SyncedOrgAsync($"{t} broken");
        var healthy1 = await factory.SyncedOrgAsync($"{t} healthy1");
        var healthy2 = await factory.SyncedOrgAsync($"{t} healthy2");
        var switchBox = new ReporterSwitch { FailFor = broken.TenantId };
        await using var host = new ConsoleClockHost(factory, services: s => s.AddScoped<IUsageReporter>(sp => new ChaosReporter(sp.GetRequiredService<ITenantContext>(), switchBox)));
        var job = host.Services.GetRequiredService<UsageSnapshotJob>();
        var today = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime);

        var first = await job.RunOnceAsync(Ct);

        first.Failed.ShouldBe(1);
        (await SnapshotRowsAsync(broken.TenantId, today)).ShouldBe(0, "hatalı kiracıya satır yazılmaz");
        (await SnapshotRowsAsync(healthy1.TenantId, today)).ShouldBe(1);
        (await SnapshotRowsAsync(healthy2.TenantId, today)).ShouldBe(1);

        // Arıza giderilince sonraki turda aynı kiracı yeniden denenir; sağlıklılar yeniden sayılmaz.
        switchBox.FailFor = null;
        var second = await job.RunOnceAsync(Ct);

        second.Failed.ShouldBe(0);
        second.Written.ShouldBeGreaterThanOrEqualTo(1);
        (await SnapshotRowsAsync(broken.TenantId, today)).ShouldBe(1);
    }

    [Fact]
    public async Task SnapshotJob_CountsAreScopedPerTenant_ThereIsNoCrossTenantLeakage()
    {
        var x = await factory.SyncedOrgAsync(Token("lkx") + " tenant X");
        var y = await factory.SyncedOrgAsync(Token("lky") + " tenant Y");
        for (var i = 0; i < 2; i++)
        {
            await CreateAsync(x.Admin, "accounts", new { name = $"X{i}" });
        }

        await CreateAsync(x.Admin, "leads", new { lastName = "X Aday", company = "X" });
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(y.Admin, "accounts", new { name = $"Y{i}" });
        }

        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(y.Admin, "cases", new { subject = $"Y Talep {i}" });
        }

        await using var host = new ConsoleClockHost(factory);
        var today = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime);

        await host.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);

        var metricsX = await SnapshotMetricsAsync(x.TenantId, today);
        var metricsY = await SnapshotMetricsAsync(y.TenantId, today);
        metricsX["sales.accounts"].ShouldBe(2);
        metricsX["sales.leads"].ShouldBe(1);
        metricsX["sales.records"].ShouldBe(3);
        metricsX["service.cases"].ShouldBe(0);
        metricsY["sales.accounts"].ShouldBe(5);
        metricsY["sales.leads"].ShouldBe(0);
        metricsY["sales.records"].ShouldBe(5);
        metricsY["service.cases"].ShouldBe(3);
        metricsX.Keys.ShouldNotContain("identity.users_active", "kullanıcı sayıları kolonlarda, metrics'te değil");
    }

    [Fact]
    public async Task SnapshotJob_PurgesSnapshotsOlderThanTheConfiguredRetention_AndKeepsTheRest()
    {
        var org = await factory.SyncedOrgAsync(Token("ret") + " retention");
        await using var host = new ConsoleClockHost(factory, b => b.UseSetting("Platform:Usage:RetentionDays", "10"));
        var today = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime);
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(-11), 1, 0, "{}");
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(-10), 1, 0, "{}");
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(-1), 1, 0, "{}");

        // Varsayılan (400 gün) 11 günlük satırı korur.
        await factory.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);
        (await SnapshotRowsAsync(org.TenantId, today.AddDays(-11))).ShouldBe(1);

        var run = await host.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);

        run.SnapshotsPurged.ShouldBeGreaterThanOrEqualTo(1);
        (await SnapshotRowsAsync(org.TenantId, today.AddDays(-11))).ShouldBe(0, "saklama süresinden eski");
        (await SnapshotRowsAsync(org.TenantId, today.AddDays(-10))).ShouldBe(1, "sınırdaki gün kalır");
        (await SnapshotRowsAsync(org.TenantId, today.AddDays(-1))).ShouldBe(1);
        (await SnapshotRowsAsync(org.TenantId, today)).ShouldBe(1, "bugünün anlık görüntüsü işle birlikte yazılır");
    }

    // ---- GET /platform/organizations/{id}/usage ------------------------------------------------------------------------------

    [Fact]
    public async Task UsageSeries_DefaultsToTheLast90Days_InAscendingOrder_WithMetrics()
    {
        await using var host = new ConsoleClockHost(factory);
        var platform = await host.Host.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("ser") + " series");
        var today = FixToday(host);
        await factory.InsertSnapshotAsync(org.TenantId, today, 4, 1, """{"sales.accounts": 9, "sales.records": 9}""");
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(-30), 3, 0, "{}");
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(-89), 2, 0, "{}");
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(-90), 1, 0, "{}");
        await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(1), 8, 0, "{}");

        var series = await platform.GetJsonAsync($"{OrgUrl(org.TenantId)}/usage");

        var items = series.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.Str("day")).ShouldBe([today.AddDays(-89).Day(), today.AddDays(-30).Day(), today.Day()], "son 90 gün (bugün dahil), gün artan");
        items[^1].GetProperty("usersActive").GetInt32().ShouldBe(4);
        items[^1].GetProperty("usersPending").GetInt32().ShouldBe(1);
        items[^1].GetProperty("metrics").GetProperty("sales.accounts").GetInt64().ShouldBe(9);
        items[0].GetProperty("metrics").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task UsageSeries_ExplicitRange_IsInclusiveOnBothEnds_AndAnOpenEndDefaultsRelativeToTheOtherEnd()
    {
        await using var host = new ConsoleClockHost(factory);
        var platform = await host.Host.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("rng") + " range");
        var today = FixToday(host);
        foreach (var offset in new[] { -100, -95, -20, -10, -1, 0 })
        {
            await factory.InsertSnapshotAsync(org.TenantId, today.AddDays(offset), 1, 0, "{}");
        }

        async Task<List<string>> Days(string query) =>
            (await platform.GetJsonAsync($"{OrgUrl(org.TenantId)}/usage?{query}")).GetProperty("items").EnumerateArray().Select(i => i.Str("day")).ToList();

        (await Days($"from={today.AddDays(-20).Day()}&to={today.AddDays(-10).Day()}")).ShouldBe([today.AddDays(-20).Day(), today.AddDays(-10).Day()]);
        (await Days($"from={today.AddDays(-19).Day()}&to={today.AddDays(-11).Day()}")).ShouldBeEmpty();
        (await Days($"from={today.AddDays(-10).Day()}")).ShouldBe([today.AddDays(-10).Day(), today.AddDays(-1).Day(), today.Day()], "to yok = bugün");
        (await Days($"to={today.AddDays(-95).Day()}")).ShouldBe([today.AddDays(-100).Day(), today.AddDays(-95).Day()], "from yok = to - 89 gün");
    }

    [Fact]
    public async Task UsageSeries_RejectsRangesLongerThan400Days_ReversedRanges_AndUnknownTenants()
    {
        await using var host = new ConsoleClockHost(factory);
        var platform = await host.Host.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("bad") + " bad range");
        var today = FixToday(host);
        var from = today.AddDays(-399);

        (await platform.GetAsync($"{OrgUrl(org.TenantId)}/usage?from={from.Day()}&to={today.Day()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "tam 400 gün (uçlar dahil) serbest");

        var tooLong = await platform.GetAsync($"{OrgUrl(org.TenantId)}/usage?from={from.AddDays(-1).Day()}&to={today.Day()}", Ct);
        (await tooLong.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("from", out _).ShouldBeTrue();
        var openEnded = await platform.GetAsync($"{OrgUrl(org.TenantId)}/usage?from={today.AddDays(-500).Day()}", Ct);
        await openEnded.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation");
        var reversed = await platform.GetAsync($"{OrgUrl(org.TenantId)}/usage?from={today.Day()}&to={today.AddDays(-1).Day()}", Ct);
        await reversed.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation");
        await (await platform.GetAsync($"{OrgUrl(Guid.NewGuid())}/usage", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await platform.GetAsync($"{OrgUrl(org.TenantId)}/usage?from=not-a-date", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- POST /platform/organizations/{id}/usage/refresh --------------------------------------------------------------------

    [Fact]
    public async Task UsageRefresh_CountsLive_UpsertsTodaysSnapshot_AndRecordsAnAuditRow()
    {
        await using var host = new ConsoleClockHost(factory);
        var platform = await host.Host.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("ref") + " refresh");
        var today = FixToday(host);
        for (var i = 0; i < 2; i++)
        {
            await CreateAsync(org.Admin, "accounts", new { name = $"F{i}" });
        }

        var first = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/usage/refresh", null, HttpStatusCode.OK);

        var item = first.GetProperty("items").EnumerateArray().Single();
        item.Str("day").ShouldBe(today.Day());
        item.GetProperty("usersActive").GetInt32().ShouldBe(1);
        item.GetProperty("usersPending").GetInt32().ShouldBe(0);
        item.GetProperty("metrics").GetProperty("sales.accounts").GetInt64().ShouldBe(2);
        item.GetProperty("metrics").TryGetProperty("identity.users_active", out _).ShouldBeFalse();
        (await SnapshotRowsAsync(org.TenantId, today)).ShouldBe(1);

        await CreateAsync(org.Admin, "accounts", new { name = "F2" });
        var second = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/usage/refresh", null, HttpStatusCode.OK);

        second.GetProperty("items")[0].GetProperty("metrics").GetProperty("sales.accounts").GetInt64().ShouldBe(3);
        (await SnapshotRowsAsync(org.TenantId, today)).ShouldBe(1, "yenileme aynı günün satırını günceller");
        (await SnapshotMetricsAsync(org.TenantId, today))["sales.accounts"].ShouldBe(3);
        var series = await platform.GetJsonAsync($"{OrgUrl(org.TenantId)}/usage");
        series.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await factory.AuditCountAsync(org.TenantId, "usage.refreshed")).ShouldBe(2);
    }

    [Fact]
    public async Task UsageRefresh_WorksForSuspendedAndTrialExpiredTenants_ButIs409ForPendingDeletionAndDeleted()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("rfs");
        var suspended = await factory.SyncedOrgAsync($"{t} suspended");
        var expired = await factory.SyncedOrgAsync($"{t} expired");
        var pending = await factory.SyncedOrgAsync($"{t} pending");
        var deleted = await factory.SyncedOrgAsync($"{t} deleted");
        await platform.SuspendAsync(suspended.TenantId, "test", "blocked");
        await factory.SetAccountAsync(factory, expired.TenantId, "trial_ends_on = current_date - 3, trial_ends_at = now() - interval '2 days'");
        (await platform.RequestDeletionRawAsync(pending.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await factory.SetAccountAsync(factory, deleted.TenantId, "status = 'deleted'");

        (await platform.RefreshUsageRawAsync(suspended.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.RefreshUsageRawAsync(expired.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await platform.RefreshUsageRawAsync(pending.TenantId)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "platform.invalid_transition");
        await (await platform.RefreshUsageRawAsync(deleted.TenantId)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "platform.invalid_transition");
        await (await platform.RefreshUsageRawAsync(Guid.NewGuid())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.usage_snapshots WHERE tenant_id = @t", ("t", pending.TenantId))).ShouldBe(0);
    }

    // ---- GET /platform/usage/export (CSV) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_ReturnsAnAttachment_WithBom_TheAlphabeticalMetricUnion_AndEmptyCellsForMissingMetrics()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("csv");
        var later = await factory.SyncedOrgAsync($"{t} B firma");
        var earlier = await factory.SyncedOrgAsync($"{t} A firma");
        await factory.SetAccountAsync(factory, earlier.TenantId, "name = @n", ("n", $"A-{t}"));
        await factory.SetAccountAsync(factory, later.TenantId, "name = @n", ("n", $"B-{t}"));
        await factory.InsertSnapshotAsync(later.TenantId, new DateOnly(2018, 3, 5), 3, 1, """{"sales.accounts": 5, "sales.records": 5, "zeta.things": 1}""");
        await factory.InsertSnapshotAsync(later.TenantId, new DateOnly(2018, 3, 6), 4, 0, """{"sales.accounts": 6, "sales.records": 6, "zeta.things": 2}""");
        await factory.InsertSnapshotAsync(earlier.TenantId, new DateOnly(2018, 3, 5), 1, 0, """{"alpha.k": 2, "sales.records": 3}""");
        await factory.SqlAsync("DELETE FROM platform.usage_snapshots WHERE day BETWEEN '2018-03-01' AND '2018-03-31' AND tenant_id NOT IN (@a, @b)", ("a", earlier.TenantId), ("b", later.TenantId));

        var response = await platform.GetAsync($"{PlatformBase}/usage/export?from=2018-03-01&to=2018-03-31", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/csv; charset=utf-8");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName!.Trim('"').ShouldBe("usage-2018-03-01-2018-03-31.csv");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ToArray().ShouldBe([0xEF, 0xBB, 0xBF], "UTF-8 BOM");
        bytes.Skip(3).Take(3).ToArray().ShouldNotBe([0xEF, 0xBB, 0xBF], "tek BOM");

        var rows = ParseCsv(new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
        rows[0].ShouldBe(["day", "tenantId", "slug", "name", "planCode", "status", "usersActive", "usersPending", "alpha.k", "sales.accounts", "sales.records", "zeta.things"]);
        rows.Count.ShouldBe(4, "başlık + 3 anlık görüntü");
        // gün artan, sonra kiracı adı
        rows[1][0].ShouldBe("2018-03-05");
        rows[1][3].ShouldBe($"A-{t}");
        rows[2][0].ShouldBe("2018-03-05");
        rows[2][3].ShouldBe($"B-{t}");
        rows[3][0].ShouldBe("2018-03-06");
        rows[1].Skip(8).ShouldBe(["2", string.Empty, "3", string.Empty], "eksik metrikler boş hücre");
        rows[2].Skip(8).ShouldBe([string.Empty, "5", "5", "1"]);
        rows[3].Skip(8).ShouldBe([string.Empty, "6", "6", "2"]);
        rows[2][1].ShouldBe(later.TenantId.ToString("D"));
        rows[2][4].ShouldBe("internal");
        rows[2][5].ShouldBe("active", "durum = dışa aktarma anındaki etkin durum");
        rows[2][6].ShouldBe("3");
        rows[2][7].ShouldBe("1");
    }

    [Fact]
    public async Task Export_ShowsTheCurrentEffectiveStatus_NotTheStatusOfTheSnapshotDay()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("est");
        var org = await factory.SyncedOrgAsync($"{t} status");
        await factory.SetAccountAsync(factory, org.TenantId, "name = @n", ("n", $"S-{t}"));
        await factory.InsertSnapshotAsync(org.TenantId, new DateOnly(2018, 9, 10), 1, 0, "{}");
        await factory.SqlAsync("DELETE FROM platform.usage_snapshots WHERE day BETWEEN '2018-09-01' AND '2018-09-30' AND tenant_id <> @t", ("t", org.TenantId));
        await platform.SuspendAsync(org.TenantId, "test");

        var rows = ParseCsv(await ExportTextAsync(platform, "2018-09-01", "2018-09-30"));

        rows.Count.ShouldBe(2);
        rows[1][5].ShouldBe("suspended");
    }

    [Fact]
    public async Task Export_QuotesPerRfc4180_AndPrefixesFormulaLikeCellsAgainstCsvInjection()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("inj");
        var names = new[]
        {
            "=cmd|' /C calc'!A0", "+1", "-1", "@x", "\tTabbed", "\rCarriage", "=1,2", "Acme, \"Quoted\" Ltd", "Line1\nLine2", $"Plain {t}",
        };
        var ids = new List<(Guid Id, string Name)>();
        for (var i = 0; i < names.Length; i++)
        {
            var org = await factory.SyncedOrgAsync($"{t} injection {i}");
            await factory.SetAccountAsync(factory, org.TenantId, "name = @n", ("n", names[i]));
            await factory.InsertSnapshotAsync(org.TenantId, new DateOnly(2018, 4, 10), 1, 0, "{}");
            ids.Add((org.TenantId, names[i]));
        }

        // Slug sütunu da aynı kurala tabidir.
        await factory.SetAccountAsync(factory, ids[0].Id, "slug = @s", ("s", "@slug-" + t));
        await factory.SqlAsync("DELETE FROM platform.usage_snapshots WHERE day BETWEEN '2018-04-01' AND '2018-04-30' AND NOT (tenant_id = ANY(@ids))", ("ids", ids.Select(i => i.Id).ToArray()));

        var text = await ExportTextAsync(platform, "2018-04-01", "2018-04-30");
        var rows = ParseCsv(text);

        rows.Count.ShouldBe(1 + names.Length);
        foreach (var (id, name) in ids)
        {
            var row = rows.Single(r => r.Count > 3 && r[1] == id.ToString("D"));
            var expected = name.Length > 0 && "=+-@\t\r".Contains(name[0]) ? "'" + name : name;
            row[3].ShouldBe(expected, $"ad hücresi: {name.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}");
            row.Count.ShouldBe(rows[0].Count, "her satır başlıkla aynı sütun sayısında");
        }

        rows.Single(r => r[1] == ids[0].Id.ToString("D"))[2].ShouldBe("'@slug-" + t);

        // Ham metin: tırnaklı hücreler RFC 4180 (çift tırnak kaçışı), satır ayırıcı CRLF.
        text.ShouldContain("\"Acme, \"\"Quoted\"\" Ltd\"");
        text.ShouldContain("\"'=1,2\"");
        text.ShouldContain("\"Line1\nLine2\"");
        text.ShouldContain("\r\n");
    }

    [Theory]
    [InlineData("2014-06-15", "2014-05-01", "2014-05-31")]
    [InlineData("2015-01-10", "2014-12-01", "2014-12-31")]
    [InlineData("2016-03-01", "2016-02-01", "2016-02-29")]
    public async Task Export_WithoutARange_DefaultsToThePreviousCalendarMonth(string clockDay, string expectedFrom, string expectedTo)
    {
        await using var host = new ConsoleClockHost(factory);
        var platform = await host.Host.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("prv") + " previous month");
        var from = DateOnly.Parse(expectedFrom);
        var to = DateOnly.Parse(expectedTo);
        foreach (var day in new[] { from.AddDays(-1), from, to, to.AddDays(1) })
        {
            await factory.InsertSnapshotAsync(org.TenantId, day, 1, 0, "{}");
        }

        await factory.SqlAsync("DELETE FROM platform.usage_snapshots WHERE day BETWEEN @a AND @b AND tenant_id <> @t", ("a", from.AddDays(-1)), ("b", to.AddDays(1)), ("t", org.TenantId));
        host.Clock.SetUtcNow(DateTimeOffset.Parse(clockDay + "T12:00:00Z"));

        var response = await platform.GetAsync($"{PlatformBase}/usage/export", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"usage-{expectedFrom}-{expectedTo}.csv");
        var rows = ParseCsv(Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct)).TrimStart('\uFEFF'));
        rows.Skip(1).Select(r => r[0]).ShouldBe([expectedFrom, expectedTo], "yalnız önceki ayın günleri");
    }

    [Fact]
    public async Task Export_WithOnlyToOrOnlyFrom_FillsTheOtherEndRelativeToTheGivenOneOrToday()
    {
        await using var host = new ConsoleClockHost(factory);
        var platform = await host.Host.PlatformAdminAsync();
        host.Clock.SetUtcNow(DateTimeOffset.Parse("2013-09-20T12:00:00Z"));

        var onlyTo = await platform.GetAsync($"{PlatformBase}/usage/export?to=2013-08-31", Ct);
        onlyTo.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("usage-2013-08-02-2013-08-31.csv");

        var onlyFrom = await platform.GetAsync($"{PlatformBase}/usage/export?from=2013-09-10", Ct);
        onlyFrom.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("usage-2013-09-10-2013-09-20.csv");
    }

    [Fact]
    public async Task Export_RejectsRangesLongerThan400Days_AndReversedRanges_ButAcceptsExactly400()
    {
        var platform = await factory.PlatformAdminAsync();
        var from = new DateOnly(2012, 1, 1);

        (await platform.GetAsync($"{PlatformBase}/usage/export?from={from.Day()}&to={from.AddDays(399).Day()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tooLong = await platform.GetAsync($"{PlatformBase}/usage/export?from={from.Day()}&to={from.AddDays(400).Day()}", Ct);
        (await tooLong.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("from", out _).ShouldBeTrue();
        var reversed = await platform.GetAsync($"{PlatformBase}/usage/export?from=2012-02-01&to=2012-01-01", Ct);
        await reversed.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation");
        (await platform.GetAsync($"{PlatformBase}/usage/export?from=2000-01-01", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "açık uçlu eski başlangıç da 400 günü aşar");
    }

    [Fact]
    public async Task Export_IsForPlatformAdminsOnly()
    {
        var org = await factory.NewOrgAsync(Token("exp") + " tenant admin");

        await (await org.Admin.GetAsync($"{PlatformBase}/usage/export", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await factory.CreateClient().GetAsync($"{PlatformBase}/usage/export", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task Export_WritesAUsageExportedAuditRow_WithTheRangeAndTheRowCount()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        var org = await factory.SyncedOrgAsync(Token("adx") + " audit export");
        await factory.InsertSnapshotAsync(org.TenantId, new DateOnly(2010, 5, 1), 1, 0, "{}");
        await factory.InsertSnapshotAsync(org.TenantId, new DateOnly(2010, 5, 2), 1, 0, "{}");
        await factory.SqlAsync("DELETE FROM platform.usage_snapshots WHERE day BETWEEN '2010-05-01' AND '2010-05-31' AND tenant_id <> @t", ("t", org.TenantId));

        (await platform.GetAsync($"{PlatformBase}/usage/export?from=2010-05-01&to=2010-05-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var details = await factory.TextsAsync(
            "SELECT details::text FROM platform.platform_audit_entries WHERE action = 'usage.exported' AND details->>'from' = '2010-05-01' AND details->>'to' = '2010-05-31'");
        var audit = JsonDocument.Parse(details.Single()).RootElement;
        audit.GetProperty("rows").GetInt64().ShouldBe(2);
        (await factory.ScalarAsync<long>(
            "SELECT count(*) FROM platform.platform_audit_entries WHERE action = 'usage.exported' AND details->>'from' = '2010-05-01' AND target_tenant_id IS NULL AND actor_user_id = @u",
            ("u", me.UserId))).ShouldBe(1);
    }

    [Fact]
    public async Task Export_StreamsAFewThousandRows_InOrder_WithConsistentColumns()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("blk");
        await factory.SqlAsync(
            """
            INSERT INTO platform.tenant_accounts (tenant_id, name, slug, plan_code, status, source, is_system, onboarding_done, tenant_created_at, created_at)
            SELECT gen_random_uuid(), 'Bulk ' || @tok || ' ' || lpad(g::text, 2, '0'), 'bulk-' || @tok || '-' || g, 'internal', 'deleted', 'backfill', FALSE, '{}'::text[], now(), now()
            FROM generate_series(1, 10) g
            """,
            ("tok", t));
        await factory.SqlAsync(
            """
            INSERT INTO platform.usage_snapshots (tenant_id, day, users_active, users_pending, metrics, taken_at)
            SELECT a.tenant_id, d::date, 1, 0, jsonb_build_object('sales.records', extract(day from d)::int), now()
            FROM platform.tenant_accounts a
            CROSS JOIN generate_series(date '2011-01-01', date '2011-01-01' + 299, interval '1 day') d
            WHERE a.slug LIKE 'bulk-' || @tok || '-%'
            """,
            ("tok", t));
        await factory.SqlAsync("DELETE FROM platform.usage_snapshots WHERE day BETWEEN '2011-01-01' AND '2011-10-27' AND tenant_id NOT IN (SELECT tenant_id FROM platform.tenant_accounts WHERE slug LIKE 'bulk-' || @tok || '-%')", ("tok", t));

        var response = await platform.GetAsync($"{PlatformBase}/usage/export?from=2011-01-01&to=2011-10-27", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = ParseCsv(Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct)).TrimStart('\uFEFF'));
        rows.Count.ShouldBe(3001, "başlık + 10 kiracı x 300 gün");
        rows.ShouldAllBe(r => r.Count == 9);
        rows[0][^1].ShouldBe("sales.records");
        var body = rows.Skip(1).ToList();
        body.Select(r => (r[0], r[3])).ShouldBe(body.Select(r => (r[0], r[3])).OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList(), "gün artan, sonra kiracı adı");
        body.ShouldAllBe(r => r[5] == "deleted");
        var audit = await factory.TextsAsync("SELECT details::text FROM platform.platform_audit_entries WHERE action = 'usage.exported' AND details->>'from' = '2011-01-01'");
        JsonDocument.Parse(audit.Single()).RootElement.GetProperty("rows").GetInt64().ShouldBe(3000);
    }

    // ---- yardımcılar -----------------------------------------------------------------------------------------------------------

    private static async Task<JsonElement> CreateAsync(HttpClient client, string path, object body) =>
        await client.SendJsonAsync(HttpMethod.Post, $"{Base}/{path}", body, HttpStatusCode.Created);

    private static async Task RemoveAsync(HttpClient client, string path) =>
        await client.SendJsonAsync(HttpMethod.Delete, $"{Base}/{path}", null, HttpStatusCode.NoContent);

    private static async Task<Guid> RoleIdAsync(HttpClient admin, string roleName) =>
        (await admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == roleName).GuidProp("id");

    /// <summary>Sahte saati bugünün (UTC) öğlenine alır ve o günü döner: gün sınırı taşması olmadan deterministik aralık testi.</summary>
    private static DateOnly FixToday(ConsoleClockHost host)
    {
        var now = host.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        host.Clock.SetUtcNow(new DateTimeOffset(today.Year, today.Month, today.Day, 12, 0, 0, TimeSpan.Zero));
        return today;
    }

    private async Task<Dictionary<string, Dictionary<string, long>>> ReportAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(tenantId);
        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var reporter in scope.ServiceProvider.GetServices<IUsageReporter>())
        {
            result[reporter.Module] = (await reporter.ReportAsync(Ct)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
        }

        return result;
    }

    private Task<long> SnapshotRowsAsync(Guid tenantId, DateOnly day) =>
        factory.ScalarAsync<long>("SELECT count(*) FROM platform.usage_snapshots WHERE tenant_id = @t AND day = @d", ("t", tenantId), ("d", day));

    private async Task<Dictionary<string, long>> SnapshotMetricsAsync(Guid tenantId, DateOnly day)
    {
        var json = (await factory.TextsAsync("SELECT metrics::text FROM platform.usage_snapshots WHERE tenant_id = @t AND day = @d", ("t", tenantId), ("d", day))).Single();
        return JsonSerializer.Deserialize<Dictionary<string, long>>(json)!;
    }

    private static async Task<string> ExportTextAsync(HttpClient platform, string from, string to)
    {
        var response = await platform.GetAsync($"{PlatformBase}/usage/export?from={from}&to={to}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct)).TrimStart('\uFEFF');
    }

    /// <summary>Hata yalıtımı testi için: yalnız belirli kiracı sayılırken (bayrak açıkken) istisna atan yapay sayaç.</summary>
    private sealed class ReporterSwitch
    {
        public Guid? FailFor { get; set; }
    }

    private sealed class ChaosReporter(ITenantContext tenant, ReporterSwitch control) : IUsageReporter
    {
        public string Module => "chaos";

        public Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
        {
            if (control.FailFor is { } failing && tenant.IsResolved && tenant.TenantId == failing)
            {
                throw new InvalidOperationException("simulated reporter failure");
            }

            return Task.FromResult<IReadOnlyList<UsageMetric>>([]);
        }
    }
}
