using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Identity.Tests.Api;

/// <summary>
/// C-OPS1 (K20): Api ana bilgisayarı metrikleri <b>ayrı dinleyicide</b> sunar; ana istek hattı (nginx'in vekillediği port) <c>/metrics</c>'i sunmaz.
/// Gerçek bir giriş hatası hem özel sayacı (<c>crm_auth_logins_total</c>) hem de yol şablonlu RED metriğini (<c>http_route</c>) artırır;
/// e-posta/kimlik gibi değerler etiketlere sızmaz.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class MetricsEndpointApiTests(CrmApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Metrics_AreServedOnTheSeparateListener_NotOnTheMainPort_AndCountRealTraffic()
    {
        var port = FreePort();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Observability:Metrics:Enabled", "true");
            b.UseSetting("Observability:Metrics:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Observability:Metrics:ScrapeCacheMilliseconds", "0");
        });
        var app = host.CreateClient();
        var email = UniqueEmail("metrics");

        // Gerçek trafik: kayıt (başarılı), yanlış parolalı giriş (başarısız), var olmayan kullanıcı.
        (await app.PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "Metrics Org", displayName = "M", email, password = DefaultPassword, locale = "tr" }, Ct)).IsSuccessStatusCode.ShouldBeTrue();
        (await app.PostAsJsonAsync($"{Base}/auth/login", new { email, password = "wrong-password-1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.PostAsJsonAsync($"{Base}/auth/login", new { email = UniqueEmail("ghost"), password = "wrong-password-2" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.PostAsJsonAsync($"{Base}/auth/login", new { email, password = DefaultPassword }, Ct)).IsSuccessStatusCode.ShouldBeTrue();

        // Ana port: /metrics yok (ne yönlendirilir ne sunulur).
        (await app.GetAsync("/metrics", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var scraper = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await scraper.GetAsync("/metrics", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldContain("crm_auth_logins_total{outcome=\"invalid_credentials\"}");
        body.ShouldContain("crm_auth_logins_total{outcome=\"success\"}");

        // RED: yol ŞABLONU (http_route) etiketi var; ham yol/e-posta/kimlik yok.
        body.ShouldContain("http_server_request_duration_seconds_bucket");
        Regex.IsMatch(body, "http_route=\"[^\"]*auth/login\"").ShouldBeTrue("giriş yolu şablon olarak görünmeli");
        body.ShouldNotContain(email, Case.Insensitive, "e-posta metriklere sızmamalı");
        Regex.IsMatch(body, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase).ShouldBeFalse("GUID (kiracı/kullanıcı kimliği) hiçbir etiket değerinde olmamalı");

        var labelNames = new HashSet<string>(LabelName().Matches(body).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        foreach (var forbidden in CrmMetrics.ForbiddenLabelNames)
        {
            labelNames.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    public async Task OutboxDispatch_CarriesTheOriginatingCorrelationId_AndIsCountedPerModule()
    {
        var port = FreePort();
        var spy = new CorrelationSpy();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Observability:Metrics:Enabled", "true");
            b.UseSetting("Observability:Metrics:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Observability:Metrics:ScrapeCacheMilliseconds", "0");
            b.ConfigureServices(services => services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Identity.Contracts.OrganizationUpdated>>(_ => new SpyHandler(spy)));
        });
        var app = host.CreateClient();
        var correlationId = "ops1-corr-" + Guid.NewGuid().ToString("N")[..12];
        var auth = await app.SignUpAsync("Corr Org", UniqueEmail("corr"));
        app.WithToken(auth.AccessToken);

        // Kimliği doğrulanmış istek (outbox mesajı isteğin correlation id'sini taşır): kuruluş adını güncelle -> OrganizationUpdated.
        using var update = new HttpRequestMessage(HttpMethod.Put, $"{Base}/organization")
        {
            Content = JsonContent.Create(new { name = "Corr Org 2", defaultLocale = "tr", timeZone = "Europe/Istanbul" }),
        };
        update.Headers.Add("X-Correlation-Id", correlationId);
        var updated = await app.SendAsync(update, Ct);
        updated.IsSuccessStatusCode.ShouldBeTrue(await updated.Content.ReadAsStringAsync(Ct));

        // Worker'ın yaptığı işi burada çalıştır: outbox mesajını boşalt (mesajı üreten isteğin correlation id'si dispatch sırasında bağlamda olmalı).
        for (var i = 0; i < 20 && spy.Seen.IsEmpty; i++)
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Sense.Crm.Shared.Infrastructure.Persistence.Outbox.OutboxProcessor<Sense.Crm.Modules.Identity.Infrastructure.Persistence.IdentityDbContext>>().ProcessAsync(Ct);
        }

        spy.Seen.ShouldContain(correlationId);

        using var scraper = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var body = await scraper.GetStringAsync("/metrics", Ct);
        Regex.IsMatch(body, "crm_outbox_messages_total\\{module=\"identity\",outcome=\"dispatched\"\\} [1-9]").ShouldBeTrue("dağıtılan mesaj modül etiketiyle sayılmalı");
        body.ShouldContain("crm_outbox_dispatch_lag_seconds_count{module=\"identity\"}");
    }

    private sealed class CorrelationSpy
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = [];
    }

    private sealed class SpyHandler(CorrelationSpy spy) : Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Identity.Contracts.OrganizationUpdated>
    {
        public Task Handle(Sense.Crm.Modules.Identity.Contracts.OrganizationUpdated integrationEvent, CancellationToken cancellationToken)
        {
            if (new Sense.Crm.Shared.Infrastructure.Observability.CorrelationIdContext().CorrelationId is { } id)
            {
                spy.Seen.Add(id);
            }

            return Task.CompletedTask;
        }
    }

    [GeneratedRegex("[{,]([A-Za-z_][A-Za-z0-9_]*)=\"")]
    private static partial Regex LabelName();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
