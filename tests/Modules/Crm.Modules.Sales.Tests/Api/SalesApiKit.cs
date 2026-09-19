using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Shared.Infrastructure.Persistence;
using Crm.Shared.Infrastructure.Persistence.Outbox;
using Crm.Tests.Shared.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Sales.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminName);

/// <summary>Entegrasyon testlerinde tekrar eden HTTP/DB yardımcıları.</summary>
internal static class SalesApiKit
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    public static Task<JsonElement> PostJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.Created) =>
        client.SendJsonAsync(HttpMethod.Post, url, body, expected);

    public static Task<JsonElement> PutJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Put, url, body, expected);

    public static Task<JsonElement> DeleteJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Delete, url, null, expected);

    public static async Task<JsonElement> CreateAccountAsync(this HttpClient client, string name, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        foreach (var property in extra?.GetType().GetProperties() ?? [])
        {
            body[property.Name] = property.GetValue(extra);
        }

        return await client.PostJsonAsync($"{Base}/accounts", body);
    }

    public static Task<JsonElement> CreateLeadAsync(this HttpClient client, string lastName, string company, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["lastName"] = lastName, ["company"] = company };
        foreach (var property in extra?.GetType().GetProperties() ?? [])
        {
            body[property.Name] = property.GetValue(extra);
        }

        return client.PostJsonAsync($"{Base}/leads", body);
    }

    public static async Task<JsonElement> DefaultPipelineAsync(this HttpClient client) =>
        (await client.GetJsonAsync($"{Base}/pipelines")).EnumerateArray().Single(p => p.GetProperty("isDefault").GetBoolean());

    public static Guid StageId(this JsonElement pipeline, string stageName) =>
        pipeline.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("name").GetString() == stageName).Id();

    /// <summary>Verilen izinleri taşıyan yeni bir özel rol + o role sahip yeni üye açar; üye olarak oturum açmış istemciyi döner.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> AddMemberAsync(this CrmApiFactory factory, Org org, string displayName, params string[] permissions)
    {
        var role = await org.Admin.PostJsonAsync($"{Base}/organization/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..8], permissions });
        var member = await ApiTestClient.AddMemberAsync(factory, org.Admin, displayName, role.Id());
        return (member.Client, member.UserId);
    }

    public static async Task<List<OutboxMessage>> OutboxMessagesAsync<TContext>(this CrmApiFactory factory, string type, string payloadContains)
        where TContext : ModuleDbContext
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var messages = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == type).ToListAsync(Ct);
        return messages.Where(m => m.Payload.Contains(payloadContains, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Worker'ın yaptığı işi taklit eder: modülün outbox'ını boşalana kadar işler.</summary>
    public static async Task DrainOutboxAsync<TContext>(this CrmApiFactory factory)
        where TContext : ModuleDbContext
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OutboxProcessor<TContext>>().ProcessAsync(Ct) == 0)
            {
                return;
            }
        }
    }

    public static async Task ShouldBeValidationErrorAsync(this HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe("validation", body);
        json.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
    }
}
