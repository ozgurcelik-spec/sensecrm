using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Sales.Tests.Api;

/// <summary>L7 — firma web sitesi sunucuda da doğrulanır: boş veya mutlak http/https adresi (arayüz bağlantıyı doğrudan href yapar).</summary>
[Collection(ApiCollection.Name)]
public sealed class AccountWebsiteApiTests(CrmApiFactory factory)
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("example.com")]
    [InlineData("//example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("http://")]
    [InlineData("not a url")]
    public async Task InvalidWebsites_AreRejectedOnCreateAndUpdate(string website)
    {
        var org = await factory.NewOrgAsync("Website Org");
        var created = await org.Admin.PostJsonAsync($"{Base}/accounts", new { name = "Geçerli Firma" });

        await (await org.Admin.PostAsJsonAsync($"{Base}/accounts", new { name = "Kötü Site", website }, Ct)).ShouldBeValidationErrorAsync("website");
        await (await org.Admin.PutAsJsonAsync($"{Base}/accounts/{created.Id()}", new { name = "Geçerli Firma", website }, Ct)).ShouldBeValidationErrorAsync("website");
    }

    [Theory]
    [InlineData("https://acme.example")]
    [InlineData("http://acme.example/yol?x=1")]
    [InlineData("HTTPS://ACME.EXAMPLE")]
    public async Task HttpAndHttpsWebsites_AreAccepted(string website)
    {
        var org = await factory.NewOrgAsync("Website Ok Org");

        var created = await org.Admin.PostJsonAsync($"{Base}/accounts", new { name = "Site Firması", website });

        created.GetProperty("website").GetString().ShouldBe(website);
    }

    [Fact]
    public async Task LegacyBareHostWebsites_StillReadFine_ButMustBeFixedOnWrite()
    {
        var org = await factory.NewOrgAsync("Legacy Website Org");
        var created = await org.Admin.PostJsonAsync($"{Base}/accounts", new { name = "Eski Firma" });
        await using (var connection = new Npgsql.NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE sales.accounts SET website = 'acme.example' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", created.Id());
            await command.ExecuteNonQueryAsync(Ct);
        }

        // Okuma hata vermez (veri dönüştürülmez).
        (await org.Admin.GetJsonAsync($"{Base}/accounts/{created.Id()}")).GetProperty("website").GetString().ShouldBe("acme.example");
        (await org.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);

        // Yazma (PUT) geçerli adres ister.
        await (await org.Admin.PutAsJsonAsync($"{Base}/accounts/{created.Id()}", new { name = "Eski Firma", website = "acme.example" }, Ct)).ShouldBeValidationErrorAsync("website");
        await org.Admin.PutJsonAsync($"{Base}/accounts/{created.Id()}", new { name = "Eski Firma", website = "https://acme.example" });
    }
}
