using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Npgsql;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Marketing.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminName);

/// <summary>Entegrasyon testlerinde tekrar eden HTTP/DB yardımcıları.</summary>
internal static class MarketingApiKit
{
    public const string CampaignsPath = $"{Base}/campaigns";

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

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static int Int(this JsonElement element, string property) => element.GetProperty(property).GetInt32();

    public static decimal Dec(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static bool Has(this JsonElement element, string property) => element.TryGetProperty(property, out _);

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
        response.StatusCode.ShouldBe(expected, $"{method} {url} -> {(int)response.StatusCode} {response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<JsonElement> PostJsonAsync(this HttpClient client, string url, object? body, HttpStatusCode expected = HttpStatusCode.Created) =>
        client.SendJsonAsync(HttpMethod.Post, url, body, expected);

    public static Task<JsonElement> PutJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Put, url, body, expected);

    public static Task<JsonElement> DeleteJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Delete, url, null, expected);

    /// <summary>Gövdeyi anonim nesne yerine sözlükten kurar (kısmi alanlar; null'lar dahil edilmez).</summary>
    public static Dictionary<string, object?> Body(object? fields)
    {
        var body = new Dictionary<string, object?>();
        foreach (var property in fields?.GetType().GetProperties() ?? [])
        {
            // Anonim tür özellik adları camelCase yazılır; sözlük anahtarı olarak olduğu gibi kullanılır.
            body[property.Name] = property.GetValue(fields);
        }

        return body;
    }

    public static Task<JsonElement> CreateCampaignAsync(this HttpClient client, string name, string type = "email", object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["type"] = type };
        foreach (var (key, value) in Body(extra))
        {
            body[key] = value;
        }

        return client.PostJsonAsync(CampaignsPath, body);
    }

    public static async Task<Guid> NewCampaignIdAsync(this HttpClient client, string name = "Kampanya", string type = "email", object? extra = null) =>
        (await client.CreateCampaignAsync(name, type, extra)).Id();

    public static async Task<JsonElement> CreateLeadAsync(this HttpClient client, string lastName, string company = "Şirket", object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["lastName"] = lastName, ["company"] = company };
        foreach (var (key, value) in Body(extra))
        {
            body[key] = value;
        }

        return await client.PostJsonAsync($"{Base}/leads", body);
    }

    public static async Task<List<Guid>> NewLeadIdsAsync(this HttpClient client, int count, string prefix = "Lead")
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add((await client.CreateLeadAsync($"{prefix}{i:D3}")).Id());
        }

        return ids;
    }

    public static async Task<JsonElement> CreateContactAsync(this HttpClient client, string lastName, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["lastName"] = lastName };
        foreach (var (key, value) in Body(extra))
        {
            body[key] = value;
        }

        return await client.PostJsonAsync($"{Base}/contacts", body);
    }

    public static Task<JsonElement> AddMembersAsync(this HttpClient client, Guid campaignId, string memberType, IEnumerable<Guid> memberIds, HttpStatusCode expected = HttpStatusCode.OK) =>
        client.PostJsonAsync($"{CampaignsPath}/{campaignId}/members", new { memberType, memberIds }, expected);

    public static async Task<List<JsonElement>> MembersAsync(this HttpClient client, Guid campaignId, string query = "")
    {
        var page = await client.GetJsonAsync($"{CampaignsPath}/{campaignId}/members{query}");
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    public static async Task<JsonElement> StatusOfAsync(this HttpClient client, Guid campaignId, Guid memberId) =>
        (await client.MembersAsync(campaignId, "?pageSize=100")).Single(m => m.Str("memberId") == memberId.ToString());

    public static Task<JsonElement> MetricsAsync(this HttpClient client, Guid campaignId) => client.GetJsonAsync($"{CampaignsPath}/{campaignId}/metrics");

    public static async Task<List<Guid>> ListIdsAsync(this HttpClient client, string query = "")
    {
        var page = await client.GetJsonAsync($"{CampaignsPath}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ToList();
    }

    public static async Task<List<string>> ListNamesAsync(this HttpClient client, string query = "")
    {
        var page = await client.GetJsonAsync($"{CampaignsPath}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Str("name")).ToList();
    }

    /// <summary>Verilen izinleri taşıyan yeni bir özel rol + o role sahip yeni üye açar; üye olarak oturum açmış istemciyi döner.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> AddMemberAsync(this CrmApiFactory factory, Org org, string displayName, params string[] permissions)
    {
        var role = await org.Admin.PostJsonAsync($"{Base}/organization/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..8], permissions });
        var email = UniqueEmail("member");
        var member = await org.Admin.PostJsonAsync($"{Base}/organization/members", new { email, displayName, roleId = role.Id() });
        var temporary = member.GetProperty("temporaryPassword").GetString()!;
        var client = factory.CreateClient();
        await client.ActivateAsync(email, temporary);
        return (client, member.GetProperty("userId").GetGuid());
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

    public static async Task<T?> ScalarAsync<T>(this CrmApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default : (T)result;
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
