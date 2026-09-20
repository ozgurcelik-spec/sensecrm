using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Shared.Infrastructure.Observability;
using Shouldly;
using Xunit;

namespace Sense.Crm.Shared.Infrastructure.Tests.Observability;

/// <summary>
/// Metrik uç noktası (C-OPS1, K20): ayrı dinleyici, isteğe bağlı bearer belirteç, özel enstrümanların artışı ve <b>etiket adı kapısı</b>
/// (kiracı/kullanıcı/kayıt kimliği, e-posta, IP gibi sınırsız ya da kişisel etiketler asla dışa aktarılmaz).
/// </summary>
public sealed partial class MetricsEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Endpoint_ServesPrometheusText_AndCustomCountersIncrement()
    {
        await using var host = await StartAsync();
        using var client = host.Client();

        CrmMetrics.LoginOutcome("success");
        var before = await ValueOfAsync(client, "crm_auth_logins_total", "outcome=\"invalid_credentials\"");
        CrmMetrics.LoginOutcome("invalid_credentials");
        CrmMetrics.LoginOutcome("invalid_credentials");
        var after = await ValueOfAsync(client, "crm_auth_logins_total", "outcome=\"invalid_credentials\"");

        (after - before).ShouldBe(2d);

        var response = await client.GetAsync(host.Url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("# TYPE crm_auth_logins_total counter");
        body.ShouldContain("target_info", Case.Sensitive, "service.name kaynak özniteliği target_info'da olmalı");
        body.ShouldContain("dotnet_", Case.Insensitive, "çalışma zamanı metrikleri (System.Runtime) yayınlanmalı");

        // Dinleyicinin kendi kazıma trafiği uygulamanın RED/Kestrel metriklerine karışmaz (yalnız ana hattın istekleri sayılır).
        var again = await (await client.GetAsync(host.Url, Ct)).Content.ReadAsStringAsync(Ct);
        again.ShouldNotContain("http_server_request_duration_seconds");
        again.ShouldNotContain("kestrel_active_connections");
    }

    [Fact]
    public async Task ExportedLabelNames_NeverContainTenantOrUserIdentifiers()
    {
        await using var host = await StartAsync();
        using var client = host.Client();
        ExerciseEveryInstrument();

        var body = await (await client.GetAsync(host.Url, Ct)).Content.ReadAsStringAsync(Ct);
        var labelNames = LabelNames(body);

        labelNames.ShouldNotBeEmpty();
        foreach (var forbidden in CrmMetrics.ForbiddenLabelNames)
        {
            labelNames.ShouldNotContain(forbidden, $"'{forbidden}' etiketi dışa aktarılamaz (kardinalite/KVKK)");
        }

        // Özel (crm_*) metriklerin etiketleri yalnız sabit kümeden gelir.
        var allowed = new HashSet<string>(["module", "status", "reason", "outcome", "task_type", "plan", "step", "background_job", "le", "quantile"], StringComparer.Ordinal);
        var crmLabels = LabelNames(string.Join('\n', body.Split('\n').Where(l => l.StartsWith("crm_", StringComparison.Ordinal))));
        crmLabels.ShouldNotBeEmpty();
        crmLabels.Where(l => !allowed.Contains(l)).ShouldBeEmpty("özel metrikler yalnız module/status/reason/outcome/task_type/plan/step/job etiketlerini kullanabilir");

        // Tüm özel metrik adları yayınlanmış olmalı (yeni enstrüman eklenip egzersiz listesine girmezse bu kapı kırılır).
        foreach (var name in new[]
        {
            "crm_auth_logins_total", "crm_auth_lockouts_total", "crm_auth_refresh_reuse_detected_total", "crm_auth_refresh_rejected_total",
            "crm_entitlement_rejections_total", "crm_outbox_messages_total", "crm_outbox_dispatch_lag_seconds", "crm_outbox_poll_failures_total",
            "crm_conductor_polls_total", "crm_conductor_tasks_total", "crm_conductor_task_duration_seconds", "crm_workflow_executions_total",
            "crm_tenant_deletion_runs_total", "crm_tenant_deletion_steps_total", "crm_tenant_deletion_rows_deleted_total", "crm_usage_snapshot_tenants_total",
            "crm_background_failures_total", "crm_outbox_pending", "crm_outbox_dead", "crm_outbox_oldest_pending_age_seconds",
            "crm_tenant_deletion_requests", "crm_tenant_deletion_oldest_due_age_seconds", "crm_workflow_executions_running",
        })
        {
            body.ShouldContain(name, Case.Sensitive, $"{name} yayınlanmıyor");
        }
    }

    [Fact]
    public async Task BearerToken_WhenConfigured_IsRequired()
    {
        const string token = "test-scrape-token-0123456789";
        await using var host = await StartAsync(("BearerToken", token));
        using var client = host.Client();

        (await client.GetAsync(host.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, host.Url);
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        (await client.SendAsync(wrong, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var basic = new HttpRequestMessage(HttpMethod.Get, host.Url);
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        (await client.SendAsync(basic, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var ok = new HttpRequestMessage(HttpMethod.Get, host.Url);
        ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(ok, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BearerTokenFile_IsReadFromDockerSecretPath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"crm-metrics-token-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(file, "file-token-abc\n", Ct);
        try
        {
            await using var host = await StartAsync(("BearerTokenFile", file));
            using var client = host.Client();

            (await client.GetAsync(host.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using var ok = new HttpRequestMessage(HttpMethod.Get, host.Url);
            ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "file-token-abc");
            (await client.SendAsync(ok, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Listener_ServesOnlyTheMetricsPath_WithGetAndHead()
    {
        await using var host = await StartAsync();
        using var client = host.Client();
        var origin = new Uri(host.Url).GetLeftPart(UriPartial.Authority);

        foreach (var path in new[] { "/", "/health", "/api/v1/auth/login", "/metrics/extra", "/swagger", "/debug/pprof" })
        {
            (await client.GetAsync(origin + path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }

        (await client.PostAsync(host.Url, new StringContent("x"), Ct)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, host.Url), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Listener_IgnoresTheApplicationsAllowedHosts_SoPrometheusCanScrapeByIpOrServiceName()
    {
        // Regresyon (gerçek yığında yakalandı): compose api'ye AllowedHosts=<genel ad>;localhost verir; ortam değişkeni iç dinleyiciye de sızar ve
        // Host: 10.x.x.x:9464 ile gelen kazımayı 400 ile reddederdi.
        Environment.SetEnvironmentVariable("AllowedHosts", "crm.example.local;localhost");
        try
        {
            await using var host = await StartAsync();
            using var client = host.Client();
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Url);
            request.Headers.Host = "10.213.77.6:9464";

            (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AllowedHosts", null);
        }
    }

    [Fact]
    public async Task Disabled_IsTheDefault_AndOpensNoPort()
    {
        var port = FreePort();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:Metrics:Port"] = port.ToString(CultureInfo.InvariantCulture) }).Build();
        using var host = Host.CreateDefaultBuilder().ConfigureServices(s => s.AddCrmObservability(config, "test")).Build();
        await host.StartAsync(Ct);

        // Kimse dinlemiyorsa aynı porta bağlanılabilir.
        var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task DashboardsAndAlertRules_ReferenceOnlyCrmMetricsTheApplicationExports()
    {
        await using var host = await StartAsync();
        using var client = host.Client();
        ExerciseEveryInstrument();
        var body = await (await client.GetAsync(host.Url, Ct)).Content.ReadAsStringAsync(Ct);
        var exported = new HashSet<string>(
            body.Split('\n').Where(l => l.StartsWith("# TYPE ", StringComparison.Ordinal)).Select(l => l.Split(' ')[2]),
            StringComparer.Ordinal);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sense.Crm.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull();
        var files = Directory.GetFiles(Path.Combine(root.FullName, "infra", "observability", "grafana", "dashboards"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(root.FullName, "infra", "observability", "prometheus", "rules"), "*.yml"))
            .ToList();
        files.Count.ShouldBeGreaterThanOrEqualTo(6);

        var missing = new List<string>();
        foreach (var file in files)
        {
            foreach (Match m in CrmMetricName().Matches(File.ReadAllText(file)))
            {
                var name = m.Value;
                if (name == "crm_event_handlers_skipped_total")
                {
                    continue; // InProcessEventBus'ın (Sense.Crm.Entitlements Meter'ı) sayacı; burada özel enstrüman olarak egzersiz edilemez.
                }

                var family = HistogramSuffix().Replace(name, string.Empty);
                if (!exported.Contains(name) && !exported.Contains(family))
                {
                    missing.Add($"{Path.GetFileName(file)}: {name}");
                }
            }
        }

        missing.Distinct().ShouldBeEmpty("panolar/alarmlar uygulamanın yayınlamadığı crm_* metriklerine başvuruyor");
    }

    [GeneratedRegex("crm_[a-z0-9_]+")]
    private static partial Regex CrmMetricName();

    [GeneratedRegex("_(bucket|sum|count)$")]
    private static partial Regex HistogramSuffix();

    [Fact]
    public void Prime_CreatesZeroValuedSeries_SoTheFirstRareSecurityEventIsVisibleToIncrease()
    {
        // Prometheus increase()/rate() ilk artışı göremez (önceki örnek yok): nadir güvenlik olaylarının sayaçları 0 ile önceden oluşturulur.
        var zero = new List<(string Instrument, string Tags)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CrmMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (value == 0)
            {
                lock (zero)
                {
                    zero.Add((instrument.Name, string.Join(',', tags.ToArray().Select(t => $"{t.Key}={t.Value}"))));
                }
            }
        });
        listener.Start();

        CrmMetrics.Prime();
        CrmMetrics.PrimeBackground();
        CrmMetrics.PrimeModule("sales");
        CrmMetrics.PrimeTaskType("crm_assign_lead_owner");
        CrmMetrics.PrimeBackgroundJob("TenantErasureService");

        lock (zero)
        {
            zero.ShouldContain(("crm.auth.refresh_reuse_detected", string.Empty));
            zero.ShouldContain(("crm.auth.lockouts", string.Empty));
            zero.Count(z => z.Instrument == "crm.auth.logins").ShouldBe(7);
            zero.ShouldContain(("crm.auth.logins", "outcome=invalid_credentials"));
            zero.ShouldContain(("crm.outbox.messages", "module=sales,outcome=dead"));
            zero.ShouldContain(("crm.workflow.executions", "status=failed"));
            zero.ShouldContain(("crm.conductor.tasks", "task_type=crm_assign_lead_owner,outcome=failed"));
            zero.ShouldContain(("crm.background.failures", "background_job=TenantErasureService"));
        }
    }

    [Fact]
    public void ForbiddenLabelNames_CoverTheObviousIdentifiers()
    {
        CrmMetrics.ForbiddenLabelNames.ShouldContain("tenant_id");
        CrmMetrics.ForbiddenLabelNames.ShouldContain("user_id");
        CrmMetrics.ForbiddenLabelNames.ShouldContain("email");
        CrmMetrics.ForbiddenLabelNames.ShouldContain("ip");
    }

    /// <summary>Her kaydedici metodu bir kez çağırır (sabit değer kümeleriyle) ki tüm özel metrikler çıktıda görünsün.</summary>
    internal static void ExerciseEveryInstrument()
    {
        CrmMetrics.LoginOutcome("success");
        CrmMetrics.LockoutStarted();
        CrmMetrics.RefreshReuseDetected();
        CrmMetrics.RefreshRejectedFor("reuse");
        CrmMetrics.EntitlementRejected("module_disabled", "commerce", "starter");
        CrmMetrics.OutboxHandled("sales", "dispatched", 3);
        CrmMetrics.OutboxDispatchLag("sales", TimeSpan.FromMilliseconds(120));
        CrmMetrics.OutboxPollFailed("sales");
        CrmMetrics.ConductorPoll("empty");
        CrmMetrics.ConductorTaskHandled("crm_assign_lead_owner", "completed", TimeSpan.FromMilliseconds(15));
        CrmMetrics.WorkflowExecutionFinished("completed");
        CrmMetrics.DeletionRunFinished("completed");
        CrmMetrics.DeletionStepFinished("module:sales", "completed", 12);
        CrmMetrics.UsageSnapshotTenants("written", 2);
        CrmMetrics.BackgroundFailed("TenantErasureService");
        CrmMetrics.Gauges.SetOutbox([("sales", 4, 1, 12.5)]);
        CrmMetrics.Gauges.SetDeletion([("scheduled", 1), ("running", 0), ("failed", 0)], 30);
        CrmMetrics.Gauges.SetWorkflowsRunning(2);
    }

    private static async Task<double> ValueOfAsync(HttpClient client, string metric, string labelFragment)
    {
        var body = await (await client.GetAsync(client.BaseAddress, Ct)).Content.ReadAsStringAsync(Ct);
        var line = body.Split('\n').FirstOrDefault(l => l.StartsWith(metric + "{", StringComparison.Ordinal) && l.Contains(labelFragment, StringComparison.Ordinal));
        return line is null ? 0d : double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
    }

    private static HashSet<string> LabelNames(string body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in body.Split('\n').Where(l => l.Length > 0 && l[0] != '#'))
        {
            var open = line.IndexOf('{', StringComparison.Ordinal);
            var close = line.LastIndexOf('}');
            if (open < 0 || close < open)
            {
                continue;
            }

            foreach (Match m in LabelName().Matches(line[(open + 1)..close]))
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    [GeneratedRegex("([A-Za-z_][A-Za-z0-9_]*)=\"")]
    private static partial Regex LabelName();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<MetricsHost> StartAsync(params (string Key, string Value)[] extra)
    {
        var port = FreePort();
        var settings = new Dictionary<string, string?>
        {
            ["Observability:Metrics:Enabled"] = "true",
            ["Observability:Metrics:Port"] = port.ToString(CultureInfo.InvariantCulture),
            ["Observability:Metrics:ScrapeCacheMilliseconds"] = "0",
        };
        foreach (var (key, value) in extra)
        {
            settings[$"Observability:Metrics:{key}"] = value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var host = Host.CreateDefaultBuilder().ConfigureServices(s => s.AddCrmObservability(config, "crm-test")).Build();
        await host.StartAsync(Ct);
        return new MetricsHost(host, $"http://127.0.0.1:{port}/metrics");
    }

    private sealed class MetricsHost(IHost host, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(10), BaseAddress = new Uri(Url) };

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
