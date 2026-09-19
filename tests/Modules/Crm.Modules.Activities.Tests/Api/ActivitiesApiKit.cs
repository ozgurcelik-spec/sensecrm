using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Npgsql;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Activities.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminName);

/// <summary>Entegrasyon testlerinde tekrar eden HTTP/DB yardımcıları.</summary>
internal static class ActivitiesApiKit
{
    public const string ActivitiesPath = $"{Base}/activities";

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

    /// <summary>Gövdeyi anonim nesne yerine sözlükten kurar (kısmi alanlar; null'lar dahil edilmez).</summary>
    public static Dictionary<string, object?> Body(object fields)
    {
        var body = new Dictionary<string, object?>();
        foreach (var property in fields.GetType().GetProperties())
        {
            body[property.Name] = property.GetValue(fields);
        }

        return body;
    }

    public static Task<JsonElement> CreateActivityAsync(this HttpClient client, string type, string subject, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["type"] = type, ["subject"] = subject };
        if (extra is not null)
        {
            foreach (var (key, value) in Body(extra))
            {
                body[key] = value;
            }
        }

        return client.PostJsonAsync(ActivitiesPath, body);
    }

    public static async Task<JsonElement> CreateAccountAsync(this HttpClient client, string name) =>
        await client.PostJsonAsync($"{Base}/accounts", new { name });

    public static async Task<List<Guid>> ListIdsAsync(this HttpClient client, string query = "")
    {
        var page = await client.GetJsonAsync($"{ActivitiesPath}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ToList();
    }

    /// <summary>Verilen izinleri taşıyan yeni bir özel rol + o role sahip yeni üye açar; üye olarak oturum açmış istemciyi döner.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> AddMemberAsync(this CrmApiFactory factory, Org org, string displayName, params string[] permissions)
    {
        var role = await org.Admin.PostJsonAsync($"{Base}/organization/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..8], permissions });
        var email = UniqueEmail("member");
        var member = await org.Admin.PostJsonAsync($"{Base}/organization/members", new { email, displayName, password = DefaultPassword, roleId = role.Id() });
        var client = factory.CreateClient();
        client.WithToken((await client.LoginAsync(email)).AccessToken);
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

    public static async Task ShouldBeValidationErrorAsync(this HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe("validation", body);
        json.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
    }

    public static string Iso(this DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
