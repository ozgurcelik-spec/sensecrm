using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminName);

/// <summary>Entegrasyon testlerinde tekrar eden HTTP/DB yardımcıları.</summary>
internal static class CommerceApiKit
{
    public const string ProductsPath = $"{Base}/products";
    public const string QuotesPath = $"{Base}/quotes";
    public const string OrdersPath = $"{Base}/orders";
    public const string InvoicesPath = $"{Base}/invoices";
    public const string PurchaseOrdersPath = $"{Base}/purchase-orders";
    public const string VendorsPath = $"{Base}/vendors";
    public const string PriceBooksPath = $"{Base}/pricebooks";

    public static readonly string[] AllCommercePermissions =
    [
        "crm.products.read", "crm.products.write", "crm.quotes.read", "crm.quotes.write", "crm.orders.read", "crm.orders.write",
        "crm.invoices.read", "crm.invoices.write", "crm.pricebooks.read", "crm.pricebooks.write", "crm.vendors.read", "crm.vendors.write",
        "crm.purchaseorders.read", "crm.purchaseorders.write",
    ];

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Org> NewOrgAsync(this CrmApiFactory factory, string name, string locale = "tr") => await NewOrgAsync((WebApplicationFactoryLike)factory.CreateClient, name, locale);

    /// <summary>Herhangi bir host (ör. <c>WithWebHostBuilder</c> ile türetilmiş) için istemci üreticisi.</summary>
    public delegate HttpClient WebApplicationFactoryLike();

    public static async Task<Org> NewOrgAsync(WebApplicationFactoryLike createClient, string name, string locale = "tr")
    {
        var client = createClient();
        var auth = await client.SignUpAsync(name + " " + Guid.NewGuid().ToString("N")[..6], UniqueEmail("admin"), locale);
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

    public static decimal Dec(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

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

    /// <summary>Gövdesiz durum geçişi (send/accept/…); beklenen 204.</summary>
    public static Task<HttpResponseMessage> ActAsync(this HttpClient client, string url, object? body = null) =>
        client.PostAsync(url, body is null ? null : JsonContent.Create(body), Ct);

    public static async Task ShouldBeNoContentAsync(this Task<HttpResponseMessage> pending)
    {
        var response = await pending;
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    public static async Task ShouldBeValidationErrorAsync(this HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe("validation", body);
        json.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
    }

    public static async Task<JsonElement> ReadProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        json.GetProperty("code").GetString().ShouldBe(code, body);
        return json;
    }

    // ---- Alan verisi ---------------------------------------------------------------------------------------------------

    public static object Line(decimal quantity = 2m, decimal unitPrice = 100m, decimal discountPercent = 0m, decimal taxRate = 20m, string description = "CRM Pro lisansı", Guid? productId = null) =>
        new { productId, description, quantity, unitPrice, discountPercent, taxRate };

    public static Task<JsonElement> CreateAccountAsync(this HttpClient client, string name) => client.PostJsonAsync($"{Base}/accounts", new { name });

    public static Task<JsonElement> CreateProductAsync(this HttpClient client, string name, object? extra = null)
    {
        var body = Merge(new Dictionary<string, object?> { ["name"] = name, ["unitPrice"] = 100m, ["taxRate"] = 20m }, extra);
        return client.PostJsonAsync(ProductsPath, body);
    }

    /// <summary>Teklif oluşturur (varsayılan: tek kalem, KDV %20). <paramref name="extra"/> alanları gövdeyi geçersiz kılar.</summary>
    public static Task<JsonElement> CreateQuoteAsync(this HttpClient client, Guid accountId, object? extra = null, params object[] lines)
    {
        var body = Merge(
            new Dictionary<string, object?> { ["subject"] = "Yıllık lisans teklifi", ["accountId"] = accountId, ["lines"] = lines.Length == 0 ? new[] { Line() } : lines },
            extra);
        return client.PostJsonAsync(QuotesPath, body);
    }

    public static Task<JsonElement> CreateOrderAsync(this HttpClient client, Guid accountId, object? extra = null, params object[] lines)
    {
        var body = Merge(
            new Dictionary<string, object?> { ["subject"] = "Doğrudan sipariş", ["accountId"] = accountId, ["lines"] = lines.Length == 0 ? new[] { Line() } : lines },
            extra);
        return client.PostJsonAsync(OrdersPath, body);
    }

    /// <summary>Doğrudan fatura (varsayılan: tek kalem 2 × 100, KDV %20 → 240.00). <paramref name="extra"/> alanları gövdeyi geçersiz kılar.</summary>
    public static Task<JsonElement> CreateInvoiceAsync(this HttpClient client, Guid accountId, object? extra = null, params object[] lines)
    {
        var body = Merge(
            new Dictionary<string, object?> { ["subject"] = "Yıllık lisans faturası", ["accountId"] = accountId, ["lines"] = lines.Length == 0 ? new[] { Line() } : lines },
            extra);
        return client.PostJsonAsync(InvoicesPath, body);
    }

    /// <summary>Gönderilmiş (sent) doğrudan fatura.</summary>
    public static async Task<JsonElement> SentInvoiceAsync(this HttpClient client, Guid accountId, object? extra = null, params object[] lines)
    {
        var invoice = await client.CreateInvoiceAsync(accountId, extra, lines);
        await client.ActAsync($"{InvoicesPath}/{invoice.Id()}/send").ShouldBeNoContentAsync();
        return invoice;
    }

    public static Task<JsonElement> CreateVendorAsync(this HttpClient client, string name, object? extra = null) =>
        client.PostJsonAsync(VendorsPath, Merge(new Dictionary<string, object?> { ["name"] = name }, extra));

    public static Task<JsonElement> CreatePriceBookAsync(this HttpClient client, string name, string model = "perProduct", object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["pricingModel"] = model };
        if (model == "flat")
        {
            body["adjustmentPercent"] = -10m;
        }

        return client.PostJsonAsync(PriceBooksPath, Merge(body, extra));
    }

    public static Task<JsonElement> CreatePurchaseOrderAsync(this HttpClient client, Guid vendorId, object? extra = null, params object[] lines) =>
        client.PostJsonAsync(
            PurchaseOrdersPath,
            Merge(new Dictionary<string, object?> { ["subject"] = "Yedek parça siparişi", ["vendorId"] = vendorId, ["lines"] = lines.Length == 0 ? new[] { Line() } : lines }, extra));

    /// <summary>Onaylanmış (confirmed) doğrudan sipariş.</summary>
    public static async Task<JsonElement> ConfirmedOrderAsync(this HttpClient client, Guid accountId, object? extra = null, params object[] lines)
    {
        var order = await client.CreateOrderAsync(accountId, extra, lines);
        await client.ActAsync($"{OrdersPath}/{order.Id()}/confirm").ShouldBeNoContentAsync();
        return order;
    }

    /// <summary>Kalem: birim fiyatsız (sunucu çözer).</summary>
    public static object UnpricedLine(Guid? productId, decimal quantity = 1m, decimal discountPercent = 0m, decimal taxRate = 0m, string description = "Ürün kalemi") =>
        new { productId, description, quantity, discountPercent, taxRate };

    /// <summary>Tahsilat kaydı (201 beklenir).</summary>
    public static Task<JsonElement> PayAsync(this HttpClient client, Guid invoiceId, decimal amount, object? extra = null) =>
        client.PostJsonAsync($"{InvoicesPath}/{invoiceId}/payments", Merge(new Dictionary<string, object?> { ["amount"] = amount }, extra));

    /// <summary>Taslak teklifi gönderip kabul eder.</summary>
    public static async Task<Guid> AcceptedQuoteAsync(this HttpClient client, Guid accountId, object? extra = null, params object[] lines)
    {
        var quote = await client.CreateQuoteAsync(accountId, extra, lines);
        await client.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
        await client.ActAsync($"{QuotesPath}/{quote.Id()}/accept").ShouldBeNoContentAsync();
        return quote.Id();
    }

    public static Dictionary<string, object?> Merge(Dictionary<string, object?> body, object? extra)
    {
        if (extra is null)
        {
            return body;
        }

        if (extra is IDictionary<string, object?> dictionary)
        {
            foreach (var (key, value) in dictionary)
            {
                body[key] = value;
            }

            return body;
        }

        foreach (var property in extra.GetType().GetProperties())
        {
            body[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.GetValue(extra);
        }

        return body;
    }

    public static async Task<List<Guid>> ListIdsAsync(this HttpClient client, string path, string query = "")
    {
        var page = await client.GetJsonAsync($"{path}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ToList();
    }

    public static async Task<List<string>> ListNumbersAsync(this HttpClient client, string path, string query = "")
    {
        var page = await client.GetJsonAsync($"{path}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Str("number")).ToList();
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

    // ---- Veritabanı ----------------------------------------------------------------------------------------------------

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

    public static async Task<T> ScalarAsync<T>(this CrmApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(Ct);
        return scalar is null or DBNull ? default! : (T)Convert.ChangeType(scalar, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Kiracıya ait sayaç değeri (satır yoksa 0).</summary>
    public static async Task<long> CounterAsync(this CrmApiFactory factory, Guid tenantId, string kind, int year) =>
        await factory.ScalarAsync<long?>(
            "SELECT last_value FROM commerce.document_counters WHERE tenant_id = @t AND kind = @k AND year = @y",
            ("t", tenantId), ("k", kind), ("y", year)) ?? 0L;

    public static Task<long> CountAsync(this CrmApiFactory factory, string table, Guid tenantId) =>
        factory.ScalarAsync<long>($"SELECT count(*) FROM commerce.{table} WHERE tenant_id = @t", ("t", tenantId));

    public static Task<long> OutboxCountAsync(this CrmApiFactory factory, Guid tenantId, string typeFragment) =>
        factory.ScalarAsync<long>(
            "SELECT count(*) FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE @f",
            ("t", tenantId), ("f", $"%{typeFragment}%"));

    public static string Today(this TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string DateString(this DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Kiracının (varsayılan Europe/Istanbul) bugünü, test sırasında gün dönümü riskini önlemek için dakikada bir hesaplanır.</summary>
    public static DateOnly TenantToday(string zoneId = "Europe/Istanbul") =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zoneId)));
}
