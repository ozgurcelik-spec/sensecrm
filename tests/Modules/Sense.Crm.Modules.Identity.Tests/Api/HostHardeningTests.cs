using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Identity.Tests.Api;

/// <summary>Host düzeyi üretim sertleştirmeleri (M5): kimlik uçlarında no-store, API dokümantasyonu, ters vekil başlıkları.</summary>
[Collection(ApiCollection.Name)]
public sealed class HostHardeningTests(CrmApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuthResponses_AreNotCacheable_ButConfigKeepsItsShortPublicCache()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail("nostore");
        var signup = await client.PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "NoStore Org", displayName = "N", email, password = DefaultPassword, locale = "tr" }, Ct);
        var login = await client.PostAsJsonAsync($"{Base}/auth/login", new { email, password = "wrong-password" }, Ct);
        var config = await client.GetAsync($"{Base}/auth/config", Ct);

        foreach (var response in new[] { signup, login })
        {
            response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        }

        config.Headers.CacheControl?.NoStore.ShouldBeFalse();
        config.Headers.CacheControl?.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ApiDocs_AreClosedInProduction_UnlessExplicitlyEnabled()
    {
        using var production = Production(docsEnabled: false);
        using var withDocs = Production(docsEnabled: true);

        (await production.CreateClient().GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await production.CreateClient().GetAsync("/scalar", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await withDocs.CreateClient().GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.CreateClient().GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // Testing/Development
    }

    [Fact]
    public void ForwardedHeaders_AreOffByDefault()
    {
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.None);
    }

    [Fact]
    public void ForwardedHeaders_TrustOnlyConfiguredProxiesAndNetworks()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ForwardedHeaders:Enabled", "true");
            b.UseSetting("ForwardedHeaders:KnownProxies:0", "10.20.30.40");
            b.UseSetting("ForwardedHeaders:KnownNetworks:0", "172.29.0.0/16");
        });

        var options = configured.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.ForwardLimit.ShouldBe(1);
        options.KnownProxies.Select(p => p.ToString()).ShouldBe(["10.20.30.40"]);
        options.KnownIPNetworks.Select(n => n.ToString()).ShouldBe(["172.29.0.0/16"]);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Production(bool docsEnabled) =>
        factory.WithWebHostBuilder(b =>
        {
            using var rsa = RSA.Create(2048);
            b.UseEnvironment("Production");
            b.UseSetting("Auth:SigningKeyPem", rsa.ExportPkcs8PrivateKeyPem());
            b.UseSetting("Docs:Enabled", docsEnabled ? "true" : "false");
            b.UseSetting("Integrations:Webhooks:Enabled", "false"); // production default: the fixture's direct-egress webhook test setup is not a valid production config
            b.UseSetting("Files:Storage:Endpoint", "http://localhost:9000"); // M8C: Production requires an s3 endpoint (never contacted at startup).
        });
}
