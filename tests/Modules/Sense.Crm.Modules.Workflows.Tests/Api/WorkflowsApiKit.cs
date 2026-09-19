using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Modules.Workflows.Infrastructure;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Tests.Shared.Fixtures;
using Sense.Crm.Tests.Shared.Workflows;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Workflows.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu (sahte workflow motoruyla) paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminName);

/// <summary>Bir organizasyon üyesi: oturum açmış istemci + kullanıcı kimliği + rol.</summary>
internal sealed record Member(HttpClient Client, Guid UserId, string Name);

/// <summary>Entegrasyon testlerinde tekrar eden HTTP/DB yardımcıları.</summary>
internal static class WorkflowsApiKit
{
    public const string RulesPath = $"{Base}/workflows/rules";
    public const string ExecutionsPath = $"{Base}/workflows/executions";
    public const string ApprovalsPath = $"{Base}/approvals";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static FakeWorkflowEngine Engine(this CrmApiFactory factory) => factory.Services.GetRequiredService<FakeWorkflowEngine>();

    public static async Task<Org> NewOrgAsync(this CrmApiFactory factory, string name, string locale = "tr")
    {
        var client = factory.CreateClient();
        var auth = await client.SignUpAsync(name, UniqueEmail("admin"), locale);
        client.WithToken(auth.AccessToken);
        var me = await client.GetJsonAsync($"{Base}/me");
        return new Org(
            client,
            me.GetProperty("organization").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("displayName").GetString()!);
    }

    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<JsonElement> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<JsonElement> PostJsonAsync(this HttpClient client, string url, object? body, HttpStatusCode expected = HttpStatusCode.Created) =>
        client.SendJsonAsync(HttpMethod.Post, url, body, expected);

    public static Task<JsonElement> PutJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Put, url, body, expected);

    public static Task<JsonElement> DeleteJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Delete, url, null, expected);

    public static async Task<HttpResponseMessage> PostRawAsync(this HttpClient client, string url, object? body = null) =>
        await client.PostAsJsonAsync(url, body ?? new { }, Ct);

    /// <summary>Özel rol açar (yalnız verilen izinlerle) ve kimliğini döner.</summary>
    public static async Task<Guid> CreateRoleAsync(this Org org, string name, params string[] permissions) =>
        (await org.Admin.PostJsonAsync($"{Base}/organization/roles", new { name, permissions })).Id();

    /// <summary>Verilen role yeni bir aktif üye ekler; üye olarak oturum açmış istemciyi döner.</summary>
    public static async Task<Member> AddMemberAsync(this CrmApiFactory factory, Org org, Guid roleId, string displayName)
    {
        var member = await ApiTestClient.AddMemberAsync(factory, org.Admin, displayName, roleId);
        return new Member(member.Client, member.UserId, displayName);
    }

    /// <summary>Verilen izinlerle yeni bir rol + o rolde bir üye (tek adım).</summary>
    public static async Task<Member> AddMemberWithPermissionsAsync(this CrmApiFactory factory, Org org, string displayName, params string[] permissions) =>
        await factory.AddMemberAsync(org, await org.CreateRoleAsync("Role " + Guid.NewGuid().ToString("N")[..8], permissions), displayName);

    public static async Task DeactivateMemberAsync(this Org org, Guid userId) =>
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{userId}", new { isActive = false }, HttpStatusCode.NoContent);

    public static object LeadRule(Guid roleId, string name = "Lead atama", string[]? sources = null, int? followUpHours = null, bool? isEnabled = null) => new Dictionary<string, object?>
    {
        ["name"] = name,
        ["kind"] = "leadAssignment",
        ["isEnabled"] = isEnabled,
        ["params"] = new Dictionary<string, object?> { ["assigneeRoleId"] = roleId, ["sources"] = sources, ["followUpHours"] = followUpHours },
    };

    public static object DealRule(Guid roleId, decimal minAmount, string name = "Buyuk firsat onayi", bool? isEnabled = null) => new Dictionary<string, object?>
    {
        ["name"] = name,
        ["kind"] = "dealApproval",
        ["isEnabled"] = isEnabled,
        ["params"] = new Dictionary<string, object?> { ["minAmount"] = minAmount, ["approverRoleId"] = roleId },
    };

    public static Task<JsonElement> CreateRuleAsync(this Org org, object rule) => org.Admin.PostJsonAsync(RulesPath, rule);

    public static Task<JsonElement> CreateLeadAsync(this HttpClient client, string firstName, string lastName, string company = "Acme", string source = "web") =>
        client.PostJsonAsync($"{Base}/leads", new { firstName, lastName, company, source });

    public static async Task<Guid> WonStageIdAsync(this HttpClient client) =>
        (await client.GetJsonAsync($"{Base}/pipelines")).EnumerateArray().Single(p => p.GetProperty("isDefault").GetBoolean())
            .GetProperty("stages").EnumerateArray().Single(s => s.Str("kind") == "won").Id();

    public static async Task<Guid> FirstOpenStageIdAsync(this HttpClient client) =>
        (await client.GetJsonAsync($"{Base}/pipelines")).EnumerateArray().Single(p => p.GetProperty("isDefault").GetBoolean())
            .GetProperty("stages").EnumerateArray().First(s => s.Str("kind") == "open").Id();

    public static async Task<Guid> LostStageIdAsync(this HttpClient client) =>
        (await client.GetJsonAsync($"{Base}/pipelines")).EnumerateArray().Single(p => p.GetProperty("isDefault").GetBoolean())
            .GetProperty("stages").EnumerateArray().Single(s => s.Str("kind") == "lost").Id();

    /// <summary>Fırsat açar (varsayılan huni, ilk açık aşama) ve gövdesini döner.</summary>
    public static async Task<JsonElement> CreateDealAsync(this HttpClient client, string name, decimal? amount)
    {
        var account = await client.PostJsonAsync($"{Base}/accounts", new { name = "Firma " + Guid.NewGuid().ToString("N")[..6] });
        return await client.PostJsonAsync($"{Base}/deals", new { name, accountId = account.Id(), amount });
    }

    public static async Task WinDealAsync(this HttpClient client, Guid dealId) =>
        await client.PostJsonAsync($"{Base}/deals/{dealId}/stage", new { stageId = await client.WonStageIdAsync() }, HttpStatusCode.NoContent);

    /// <summary>Worker'ın yaptığı işi taklit eder: modülün outbox'ını boşalana kadar işler (domain event → integration event → tüketiciler).</summary>
    public static async Task DrainOutboxAsync<TContext>(this CrmApiFactory factory)
        where TContext : ModuleDbContext
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OutboxProcessor<TContext>>().ProcessAsync(Ct) == 0)
            {
                break;
            }
        }

        // Olaylar commit edildi: sahte motorun bekleyen görevleri (Worker taklidi) şimdi çalışır.
        await factory.Engine().DrainAsync();
    }

    /// <summary>Sales outbox'ını boşaltır: lead/fırsat olayları Workflows tüketicilerine ulaşır, sahte motor yürütmeyi çalıştırır.</summary>
    public static Task DrainSalesAsync(this CrmApiFactory factory) => factory.DrainOutboxAsync<SalesDbContext>();

    /// <summary>Worker'daki durum senkronunun bir turu: çalışan yürütmelerin durumu motordan yansıtılır.</summary>
    public static async Task<int> SyncExecutionsAsync(this CrmApiFactory factory)
    {
        await factory.Engine().DrainAsync();
        return await factory.Services.GetRequiredService<ExecutionSyncRunner>().RunOnceAsync(Ct);
    }

    public static async Task<List<OutboxMessage>> OutboxMessagesAsync<TContext>(this CrmApiFactory factory, string type, string payloadContains)
        where TContext : ModuleDbContext
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var messages = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == type).ToListAsync(Ct);
        return messages.Where(m => m.Payload.Contains(payloadContains, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static async Task ExecuteAsync(this CrmApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<List<JsonElement>> ExecutionsAsync(this HttpClient client, string query = "")
    {
        var page = await client.GetJsonAsync($"{ExecutionsPath}{query}");
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    public static async Task<List<JsonElement>> ApprovalsAsync(this HttpClient client, string query = "?mine=true")
    {
        var page = await client.GetJsonAsync($"{ApprovalsPath}{query}");
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    public static async Task<List<JsonElement>> ActivitiesForAsync(this HttpClient client, string relatedType, Guid relatedId)
    {
        var page = await client.GetJsonAsync($"{Base}/activities?relatedType={relatedType}&relatedId={relatedId}");
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    public static async Task ShouldBeValidationErrorAsync(this HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe("validation", body);
        json.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
    }

    /// <summary>Kesinti simülasyonunu bayrak açıkken çalıştırır ve mutlaka geri alır (paylaşılan motor diğer testleri etkilemesin).</summary>
    public static async Task WithEngineOutageAsync(this CrmApiFactory factory, bool start, bool complete, Func<Task> action)
    {
        var engine = factory.Engine();
        (engine.FailStart, engine.FailComplete) = (start, complete);
        try
        {
            await action();
        }
        finally
        {
            (engine.FailStart, engine.FailComplete) = (false, false);
        }
    }
}
