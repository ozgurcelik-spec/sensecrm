using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Infrastructure.Entitlements;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>Limit/plan testlerinin ortak yardımcıları (A öneki: aynı projedeki diğer test dosyalarının yardımcılarıyla çakışmaz).</summary>
internal static class ALimitsKit
{
    public const string UsageCacheSetting = "Platform:Usage:CacheSeconds";

    /// <summary>Plan kodu deseni <c>^[a-z][a-z0-9_]{1,31}$</c>: önek + rastgele sonek (planlar <c>platform.plans</c>'ta kalıcıdır, çakışma olmasın).</summary>
    public static string NewPlanCode(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Kayıt sayımı önbelleksiz host (yumuşak limit testleri belirleyici olsun).</summary>
    public static WebApplicationFactory<Program> WithUncachedRecordCounts(this CrmApiFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting(UsageCacheSetting, "0"));

    public static string LimitsJson(int? maxUsers = null, string maxRecordsJson = "{}") =>
        $$"""{"maxUsers":{{(maxUsers is null ? "null" : maxUsers.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}},"maxRecords":{{maxRecordsJson}}}""";

    /// <summary>Yeni plan ekler ve kodunu döner (modüller ham JSON).</summary>
    public static async Task<string> PlanAsync(this CrmApiFactory factory, string prefix, int? maxUsers = null, string maxRecordsJson = "{}", string? modulesJson = null)
    {
        var code = NewPlanCode(prefix);
        await factory.EnsurePlanAsync(code, LimitsJson(maxUsers, maxRecordsJson), modulesJson ?? AllModulesOn);
        return code;
    }

    public static string OnlyModuleJson(string module) =>
        "{" + string.Join(',', new[] { "workflows", "commerce", "service", "marketing" }.Select(m => $"\"{m}\":{(m == module ? "true" : "false")}")) + "}";

    /// <summary>
    /// Yeni organizasyon açar, kayıt olayını boşaltır (tembel satır gerçek olayla düzelir; sonraki plan atamasını geri almasın) ve platform yöneticisiyle planı atar.
    /// </summary>
    public static async Task<(TestOrg Org, HttpClient Platform)> OrgOnPlanAsync(this WebApplicationFactory<Program> host, string name, string planCode, object? overrides = null)
    {
        var org = await host.NewOrgAsync(name);
        await host.DrainOutboxesAsync();
        var platform = await host.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(org.TenantId, planCode, overrides: overrides);
        return (org, platform);
    }

    public static async Task<Guid> StandardRoleIdAsync(this HttpClient admin) =>
        (await admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Standard").GuidProp("id");

    public static Task<HttpResponseMessage> PostMemberAsync(this HttpClient admin, Guid roleId, string? email = null) =>
        admin.PostAsJsonAsync($"{Base}/organization/members", new { email = email ?? UniqueEmail("m"), displayName = "Uye", roleId }, Ct);

    /// <summary>Yeni hesaplı (etkin) üye ekler ve kullanıcı kimliğini döner.</summary>
    public static async Task<Guid> AddActiveMemberAsync(this HttpClient admin, Guid roleId)
    {
        var response = await admin.PostMemberAsync(roleId);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GuidProp("userId");
    }

    /// <summary>ProblemDetails yanıtını doğrular ve gövdeyi döner.</summary>
    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        json.Str("code").ShouldBe(code, body);
        return json;
    }

    public static async Task ShouldBeLimitExceededAsync(this HttpResponseMessage response, string limit, string? module, long max, long used)
    {
        var problem = await response.ProblemAsync(HttpStatusCode.PaymentRequired, "plan.limit_exceeded");
        var args = problem.GetProperty("args");
        args.Str("limit").ShouldBe(limit);
        args.GetProperty("max").GetInt64().ShouldBe(max);
        args.GetProperty("used").GetInt64().ShouldBe(used);
        if (module is null)
        {
            args.TryGetProperty("module", out _).ShouldBeFalse();
        }
        else
        {
            args.Str("module").ShouldBe(module);
        }
    }

    public static async Task ShouldBeModuleDisabledAsync(this HttpResponseMessage response, string module)
    {
        var problem = await response.ProblemAsync(HttpStatusCode.Forbidden, "plan.module_disabled");
        problem.GetProperty("args").Str("module").ShouldBe(module);
    }

    public static Task<HttpResponseMessage> PostAccountAsync(this HttpClient client) =>
        client.PostAsJsonAsync($"{Base}/accounts", new { name = "Firma " + Guid.NewGuid().ToString("N")[..6] }, Ct);

    public static Task<HttpResponseMessage> PostContactAsync(this HttpClient client) =>
        client.PostAsJsonAsync($"{Base}/contacts", new { firstName = "Test", lastName = "Kisi" + Guid.NewGuid().ToString("N")[..4] }, Ct);

    public static Task<HttpResponseMessage> PostLeadAsync(this HttpClient client) =>
        client.PostAsJsonAsync($"{Base}/leads", new { lastName = "Aday" + Guid.NewGuid().ToString("N")[..4], company = "Sirket" }, Ct);

    public static Task<HttpResponseMessage> PostDealAsync(this HttpClient client, Guid accountId) =>
        client.PostAsJsonAsync($"{Base}/deals", new { name = "Firsat " + Guid.NewGuid().ToString("N")[..4], accountId }, Ct);

    public static async Task<Guid> CreatedIdAsync(this Task<HttpResponseMessage> pending)
    {
        var response = await pending;
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GuidProp("id");
    }

    public static async Task<HttpResponseMessage> DeleteAsync(this HttpClient client, string path, Guid id) => await client.DeleteAsync($"{Base}/{path}/{id}", Ct);

    /// <summary>Kapı modülü başına gerçek uçlar: okuma (liste) ve yazma (oluşturma) + gövde.</summary>
    public static (string ListUrl, string CreateUrl, object Body) GatedEndpoint(string module, Guid roleId) => module switch
    {
        "workflows" => ($"{Base}/workflows/rules", $"{Base}/workflows/rules", new Dictionary<string, object?>
        {
            ["name"] = "Kural " + Guid.NewGuid().ToString("N")[..4],
            ["kind"] = "leadAssignment",
            ["params"] = new Dictionary<string, object?> { ["assigneeRoleId"] = roleId },
        }),
        "commerce" => ($"{Base}/products", $"{Base}/products", new { name = "Urun " + Guid.NewGuid().ToString("N")[..4], unitPrice = 100m, taxRate = 20m }),
        "service" => ($"{Base}/cases", $"{Base}/cases", new { subject = "Talep " + Guid.NewGuid().ToString("N")[..4] }),
        "marketing" => ($"{Base}/campaigns", $"{Base}/campaigns", new { name = "Kampanya " + Guid.NewGuid().ToString("N")[..4], type = "email" }),
        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    public static readonly string[] GatedModuleNames = ["workflows", "commerce", "service", "marketing"];
}

/// <summary><c>IUsageMeter</c> çağrılarını sayan sarmalayıcı: "sınırsız plan = sıfır sayım sorgusu" kuralını doğrular.</summary>
internal sealed class ACallCounter
{
    private int _total;
    private int _userCounts;
    private int _recordCounts;

    public int Total => Volatile.Read(ref _total);

    public int UserCounts => Volatile.Read(ref _userCounts);

    public int RecordCounts => Volatile.Read(ref _recordCounts);

    public void Users()
    {
        Interlocked.Increment(ref _total);
        Interlocked.Increment(ref _userCounts);
    }

    public void Records()
    {
        Interlocked.Increment(ref _total);
        Interlocked.Increment(ref _recordCounts);
    }

    public void Other() => Interlocked.Increment(ref _total);
}

internal sealed class ACountingUsageMeter(IUsageMeter inner, ACallCounter counter) : IUsageMeter
{
    public Task<UsageCollection> CollectAsync(Guid tenantId, CancellationToken ct)
    {
        counter.Other();
        return inner.CollectAsync(tenantId, ct);
    }

    public Task<(int Active, int Pending)> CountUsersAsync(CancellationToken ct)
    {
        counter.Users();
        return inner.CountUsersAsync(ct);
    }

    public Task<RecordCounts> GetRecordCountsAsync(CancellationToken ct)
    {
        counter.Records();
        return inner.GetRecordCountsAsync(ct);
    }
}

/// <summary>Plan limitleri: sert kullanıcı limiti, yumuşak kayıt limiti, sıfır/sınırsız, plan düşürme, modül kapıları, istisna.</summary>
[Collection(ApiCollection.Name)]
public sealed class LimitsApiTests(CrmApiFactory factory)
{
    // ---- Kullanıcı limiti (sert) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task MaxUsers_CountsActiveMembersAndPendingInvitations_AndTheFourthAddIsRejectedWithUsedAndMax()
    {
        var plan = await factory.PlanAsync("users3", maxUsers: 3);
        var (org, _) = await factory.OrgOnPlanAsync("Kullanici Limit", plan);
        var role = await org.Admin.StandardRoleIdAsync();
        var invitee = await factory.NewOrgAsync("Davetli Org"); // mevcut hesap: ekleme "bekleyen davet" olur

        await org.Admin.AddActiveMemberAsync(role); // 2 etkin (yönetici + üye)
        var invitation = await org.Admin.PostMemberAsync(role, invitee.AdminEmail); // + 1 bekleyen
        (await invitation.Content.ReadAsStringAsync(Ct)).ShouldContain("pending");
        invitation.StatusCode.ShouldBe(HttpStatusCode.Created);

        var fourth = await org.Admin.PostMemberAsync(role);

        await fourth.ShouldBeLimitExceededAsync("users", module: null, max: 3, used: 3);
        var subscription = await org.Admin.GetJsonAsync($"{Base}/subscription");
        subscription.GetProperty("usage").GetProperty("users").GetInt32().ShouldBe(2);
        subscription.GetProperty("usage").GetProperty("pendingUsers").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task MaxUsers_DeactivatingAMemberFreesASlot()
    {
        var plan = await factory.PlanAsync("users2", maxUsers: 2);
        var (org, _) = await factory.OrgOnPlanAsync("Pasiflestirme Limit", plan);
        var role = await org.Admin.StandardRoleIdAsync();
        var member = await org.Admin.AddActiveMemberAsync(role); // yönetici + üye = 2/2
        (await org.Admin.PostMemberAsync(role)).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{member}", new { isActive = false }, HttpStatusCode.NoContent);

        (await org.Admin.PostMemberAsync(role)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await org.Admin.PostMemberAsync(role)).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task MaxUsers_ReactivatingAnInactiveMemberAtTheLimitIsRejected_AndSucceedsOnceASlotIsFree()
    {
        var plan = await factory.PlanAsync("react2", maxUsers: 2);
        var (org, _) = await factory.OrgOnPlanAsync("Yeniden Etkinlestirme", plan);
        var role = await org.Admin.StandardRoleIdAsync();
        var first = await org.Admin.AddActiveMemberAsync(role);
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{first}", new { isActive = false }, HttpStatusCode.NoContent);
        var second = await org.Admin.AddActiveMemberAsync(role); // yönetici + ikinci üye = 2/2

        var blocked = await org.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{Base}/organization/members/{first}") { Content = JsonContent.Create(new { isActive = true }) }, Ct);

        await blocked.ShouldBeLimitExceededAsync("users", module: null, max: 2, used: 2);

        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{second}", new { isActive = false }, HttpStatusCode.NoContent);
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{first}", new { isActive = true }, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task MaxUsers_AcceptingAnInvitationConsumesNoExtraSlot()
    {
        var plan = await factory.PlanAsync("accept3", maxUsers: 3);
        var (org, _) = await factory.OrgOnPlanAsync("Davet Kabul Limit", plan);
        var role = await org.Admin.StandardRoleIdAsync();
        var invitee = await factory.NewOrgAsync("Kabul Eden Org");
        (await org.Admin.PostMemberAsync(role, invitee.AdminEmail)).StatusCode.ShouldBe(HttpStatusCode.Created); // bekleyen davet: 2/3
        await org.Admin.AddActiveMemberAsync(role); // 3/3

        var invitations = await invitee.Admin.GetJsonAsync($"{Base}/me/invitations");
        var invitationId = invitations.EnumerateArray().Single(i => i.GuidProp("organizationId") == org.TenantId).GuidProp("id");
        (await invitee.Admin.PostAsync($"{Base}/me/invitations/{invitationId}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var usage = (await org.Admin.GetJsonAsync($"{Base}/subscription")).GetProperty("usage");
        usage.GetProperty("users").GetInt32().ShouldBe(3, "davet kabulü bekleyen yeri etkin yere çevirir");
        usage.GetProperty("pendingUsers").GetInt32().ShouldBe(0);
        await (await org.Admin.PostMemberAsync(role)).ShouldBeLimitExceededAsync("users", module: null, max: 3, used: 3);
    }

    [Fact]
    public async Task MaxUsers_EightConcurrentAddsWithOneFreeSlot_YieldExactlyOneSuccess_AndTheRestAre402()
    {
        var plan = await factory.PlanAsync("race3", maxUsers: 3);
        var (org, _) = await factory.OrgOnPlanAsync("Eszamanlilik Limit", plan);
        var role = await org.Admin.StandardRoleIdAsync();
        await org.Admin.AddActiveMemberAsync(role); // 2/3: bir boş yer

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => org.Admin.PostMemberAsync(role)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.PaymentRequired).ShouldBe(7, string.Join(",", responses.Select(r => (int)r.StatusCode)));
        (await org.Admin.GetJsonAsync($"{Base}/subscription")).GetProperty("usage").GetProperty("users").GetInt32().ShouldBe(3);
    }

    // ---- Kayıt limiti (yumuşak) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task MaxRecordsSales_TheNthRecordPasses_TheNextIsRejected_AcrossAccountsContactsLeadsAndDeals()
    {
        var plan = await factory.PlanAsync("sales4", maxRecordsJson: """{"sales":4}""");
        using var host = factory.WithUncachedRecordCounts();
        var (org, _) = await host.OrgOnPlanAsync("Kayit Limit", plan);
        var client = org.Admin;

        var account = await client.PostAccountAsync().CreatedIdAsync(); // 1
        await client.PostContactAsync().CreatedIdAsync(); // 2
        await client.PostLeadAsync().CreatedIdAsync(); // 3
        await host.DrainOutboxesAsync(); // varsayılan huni (fırsat için) kayıt olayıyla tohumlanır
        await client.PostDealAsync(account).CreatedIdAsync(); // 4 (N. kayıt geçer)

        await (await client.PostAccountAsync()).ShouldBeLimitExceededAsync("records", "sales", max: 4, used: 4);
        await (await client.PostContactAsync()).ShouldBeLimitExceededAsync("records", "sales", max: 4, used: 4);
        await (await client.PostLeadAsync()).ShouldBeLimitExceededAsync("records", "sales", max: 4, used: 4);
        await (await client.PostDealAsync(account)).ShouldBeLimitExceededAsync("records", "sales", max: 4, used: 4);
        (await client.GetJsonAsync($"{Base}/subscription")).GetProperty("usage").GetProperty("records").GetProperty("sales").GetInt64().ShouldBe(4);
    }

    [Fact]
    public async Task MaxRecordsSales_SoftDeletedRecordsDoNotCount_SoDeletingFreesASlot()
    {
        var plan = await factory.PlanAsync("sales2", maxRecordsJson: """{"sales":2}""");
        using var host = factory.WithUncachedRecordCounts();
        var (org, _) = await host.OrgOnPlanAsync("Silme Limit", plan);
        var client = org.Admin;
        var lead = await client.PostLeadAsync().CreatedIdAsync();
        var contact = await client.PostContactAsync().CreatedIdAsync();
        (await client.PostAccountAsync()).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        (await client.DeleteAsync("leads", lead)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAccountAsync()).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAccountAsync()).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await client.DeleteAsync("contacts", contact)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostLeadAsync()).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task MaxRecords_ZeroLimit_RejectsNewRecordsOfThatModule_ButReadsAndOtherModulesWork()
    {
        var plan = await factory.PlanAsync("sales0", maxRecordsJson: """{"sales":0}""");
        using var host = factory.WithUncachedRecordCounts();
        var (org, _) = await host.OrgOnPlanAsync("Sifir Limit", plan);

        await (await org.Admin.PostAccountAsync()).ShouldBeLimitExceededAsync("records", "sales", max: 0, used: 0);

        (await org.Admin.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.GetAsync($"{Base}/subscription", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.PostAsJsonAsync($"{Base}/campaigns", new { name = "Sinirsiz modul", type = "email" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UnlimitedPlan_RunsNoCountingQueriesAtAll_WhileFiniteLimitsCountOnlyWhatTheyNeed()
    {
        var counter = new ACallCounter();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(counter);
            services.Replace(ServiceDescriptor.Scoped<IUsageMeter>(sp => new ACountingUsageMeter(ActivatorUtilities.CreateInstance<UsageMeter>(sp), counter)));
        }));
        var org = await host.NewOrgAsync("Sinirsiz Sayim");
        var role = await org.Admin.StandardRoleIdAsync();

        // internal plan: kullanıcı ekleme + her modülden kayıt açma hiçbir sayım yapmaz.
        await org.Admin.AddActiveMemberAsync(role);
        await org.Admin.PostAccountAsync().CreatedIdAsync();
        await org.Admin.PostContactAsync().CreatedIdAsync();
        await org.Admin.PostLeadAsync().CreatedIdAsync();
        foreach (var module in ALimitsKit.GatedModuleNames)
        {
            var (_, create, body) = ALimitsKit.GatedEndpoint(module, role);
            (await org.Admin.PostAsJsonAsync(create, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        counter.Total.ShouldBe(0, "sınırsız (null) limitte sayım sorgusu hiç çalışmamalı");

        // Kontrol 1: yalnız kullanıcı limiti sonlu → kayıt oluşturma hâlâ sıfır kayıt sayımı, üye ekleme kullanıcı sayımı yapar.
        await host.DrainOutboxesAsync();
        var platform = await host.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(org.TenantId, await factory.PlanAsync("ctlusers", maxUsers: 50));
        await org.Admin.PostAccountAsync().CreatedIdAsync();
        (counter.UserCounts, counter.RecordCounts).ShouldBe((0, 0));
        await org.Admin.AddActiveMemberAsync(role);
        counter.UserCounts.ShouldBeGreaterThan(0);
        counter.RecordCounts.ShouldBe(0);

        // Kontrol 2: sonlu sales limiti → hesap oluşturma kayıt sayımı yapar (sarmalayıcının bağlı olduğunu kanıtlar).
        await platform.PutSubscriptionAsync(org.TenantId, await factory.PlanAsync("ctlrecs", maxRecordsJson: """{"sales":500}"""));
        await org.Admin.PostAccountAsync().CreatedIdAsync();
        counter.RecordCounts.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Downgrade_BelowCurrentUsage_KeepsAllData_ReportsOverLimit_AndBlocksNewConsumption()
    {
        var big = await factory.PlanAsync("big", maxRecordsJson: "{}");
        var small = await factory.PlanAsync("small", maxUsers: 2, maxRecordsJson: """{"sales":2}""");
        using var host = factory.WithUncachedRecordCounts();
        var (org, platform) = await host.OrgOnPlanAsync("Plan Dusurme", big);
        var role = await org.Admin.StandardRoleIdAsync();
        await org.Admin.AddActiveMemberAsync(role);
        await org.Admin.AddActiveMemberAsync(role); // 3 kullanıcı
        for (var i = 0; i < 3; i++)
        {
            await org.Admin.PostAccountAsync().CreatedIdAsync(); // 3 kayıt
        }

        var result = await platform.PutSubscriptionAsync(org.TenantId, small);

        var over = result.GetProperty("overLimit").EnumerateArray().ToList();
        over.Count.ShouldBe(2);
        var users = over.Single(o => o.Str("limit") == "users");
        (users.GetProperty("max").GetInt32(), users.GetProperty("used").GetInt32()).ShouldBe((2, 3));
        var records = over.Single(o => o.Str("limit") == "records");
        (records.Str("module"), records.GetProperty("max").GetInt32(), records.GetProperty("used").GetInt32()).ShouldBe(("sales", 2, 3));

        (await org.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(3, "veri silinmez");
        (await org.Admin.GetJsonAsync($"{Base}/organization/members")).GetArrayLength().ShouldBe(3);
        await (await org.Admin.PostAccountAsync()).ShouldBeLimitExceededAsync("records", "sales", max: 2, used: 3);
        await (await org.Admin.PostMemberAsync(role)).ShouldBeLimitExceededAsync("users", module: null, max: 2, used: 3);
        (await org.Admin.GetJsonAsync($"{Base}/subscription")).GetProperty("overLimit").GetArrayLength().ShouldBe(2);

        await platform.PutSubscriptionAsync(org.TenantId, big); // plan yükseltilince tüketim yeniden açılır
        (await org.Admin.PostAccountAsync()).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CommerceConvertQuote_CountsAgainstTheCommerceRecordLimit()
    {
        var tight = await factory.PlanAsync("comm1", maxRecordsJson: """{"commerce":1}""");
        var roomy = await factory.PlanAsync("comm2", maxRecordsJson: """{"commerce":2}""");
        using var host = factory.WithUncachedRecordCounts();
        var (org, platform) = await host.OrgOnPlanAsync("Ticaret Limit", tight);
        var client = org.Admin;
        var account = await client.PostAccountAsync().CreatedIdAsync();
        var line = new { description = "Lisans", quantity = 1m, unitPrice = 100m, discountPercent = 0m, taxRate = 20m };
        var quote = await client.PostAsJsonAsync($"{Base}/quotes", new { subject = "Teklif", accountId = account, lines = new[] { line } }, Ct).CreatedIdAsync(); // commerce 1/1
        (await client.PostAsync($"{Base}/quotes/{quote}/send", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync($"{Base}/quotes/{quote}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var blocked = await client.PostAsJsonAsync($"{Base}/quotes/{quote}/convert", new { }, Ct);

        await blocked.ShouldBeLimitExceededAsync("records", "commerce", max: 1, used: 1);

        await platform.PutSubscriptionAsync(org.TenantId, roomy);
        (await client.PostAsJsonAsync($"{Base}/quotes/{quote}/convert", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var another = await client.PostAsJsonAsync($"{Base}/quotes", new { subject = "Ikinci", accountId = account, lines = new[] { line } }, Ct);
        await another.ShouldBeLimitExceededAsync("records", "commerce", max: 2, used: 2);
    }

    // ---- Modül kapıları ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("workflows")]
    [InlineData("commerce")]
    [InlineData("service")]
    [InlineData("marketing")]
    public async Task GatedModule_IsBlockedForReadsAndWritesWhenThePlanFlagIsOff_AndWorksWhenOn(string module)
    {
        var off = await factory.PlanAsync("modoff", modulesJson: AllModulesOff);
        var only = await factory.PlanAsync("modon", modulesJson: ALimitsKit.OnlyModuleJson(module));
        var (org, platform) = await factory.OrgOnPlanAsync("Modul Kapisi " + module, off);
        var role = await org.Admin.StandardRoleIdAsync();
        var (listUrl, createUrl, body) = ALimitsKit.GatedEndpoint(module, role);

        await (await org.Admin.GetAsync(listUrl, Ct)).ShouldBeModuleDisabledAsync(module);
        await (await org.Admin.PostAsJsonAsync(createUrl, body, Ct)).ShouldBeModuleDisabledAsync(module);

        await platform.PutSubscriptionAsync(org.TenantId, only);

        (await org.Admin.GetAsync(listUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.PostAsJsonAsync(createUrl, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        foreach (var other in ALimitsKit.GatedModuleNames.Where(m => m != module))
        {
            await (await org.Admin.GetAsync(ALimitsKit.GatedEndpoint(other, role).ListUrl, Ct)).ShouldBeModuleDisabledAsync(other);
        }
    }

    [Fact]
    public async Task ModuleOverride_BeatsThePlan_InBothDirections()
    {
        var off = await factory.PlanAsync("ovoff", modulesJson: AllModulesOff);
        var on = await factory.PlanAsync("ovon", modulesJson: AllModulesOn);
        var (org, platform) = await factory.OrgOnPlanAsync("Modul Istisna", off, overrides: new { modules = new { marketing = true } });
        var role = await org.Admin.StandardRoleIdAsync();

        (await org.Admin.GetAsync($"{Base}/campaigns", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await org.Admin.GetAsync($"{Base}/products", Ct)).ShouldBeModuleDisabledAsync("commerce");

        await platform.PutSubscriptionAsync(org.TenantId, on, overrides: new { modules = new { marketing = false } });

        await (await org.Admin.GetAsync($"{Base}/campaigns", Ct)).ShouldBeModuleDisabledAsync("marketing");
        (await org.Admin.GetAsync($"{Base}/products", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.GetAsync(ALimitsKit.GatedEndpoint("workflows", role).ListUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LimitOverrides_BeatThePlan_UserLimitNullMeansUnlimited_AndRecordLimitsApply()
    {
        var oneUser = await factory.PlanAsync("ovuser1", maxUsers: 1);
        var unlimited = await factory.PlanAsync("ovunlim");
        using var host = factory.WithUncachedRecordCounts();
        var (org, platform) = await host.OrgOnPlanAsync("Limit Istisna", oneUser);
        var role = await org.Admin.StandardRoleIdAsync();
        (await org.Admin.PostMemberAsync(role)).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        await platform.PutSubscriptionAsync(org.TenantId, oneUser, overrides: new { maxUsers = (int?)null });
        await org.Admin.AddActiveMemberAsync(role);
        await org.Admin.AddActiveMemberAsync(role);

        await platform.PutSubscriptionAsync(org.TenantId, unlimited, overrides: new { maxUsers = 3, maxRecords = new { sales = 1 } });
        await (await org.Admin.PostMemberAsync(role)).ShouldBeLimitExceededAsync("users", module: null, max: 3, used: 3);
        await org.Admin.PostAccountAsync().CreatedIdAsync();
        await (await org.Admin.PostAccountAsync()).ShouldBeLimitExceededAsync("records", "sales", max: 1, used: 1);
    }
}
