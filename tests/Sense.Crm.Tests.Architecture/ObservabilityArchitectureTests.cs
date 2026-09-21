using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>
/// C-OPS1 / K20 kapıları: metrik uç noktası hiçbir zaman vekillenmez/yayınlanmaz, gözlem yığını yalnız iç ağda + Grafana loopback'te, imajlar sabitlenmiş, sır varsayılanı yok,
/// her alarmın runbook bağlantısı çalışır, panolar geçerli JSON ve kiracı/kullanıcı etiketi içermez, OpenTelemetry yalnız <c>Shared.Infrastructure</c>'da.
/// </summary>
public sealed partial class ObservabilityArchitectureTests
{
    private static readonly string Root = FindSolutionRoot();

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void MetricsPorts_AreNeverProxiedByNginx_NorPublishedByTheProductionStack()
    {
        var nginx = Read("web/nginx/default.conf.template");
        nginx.ShouldNotContain("metrics", Case.Insensitive, "nginx /metrics'i vekillememeli");
        nginx.ShouldNotContain("9464");
        nginx.ShouldNotContain("9465");

        var prod = Read("deploy/docker-compose.prod.yml");
        prod.ShouldNotContain("9464");
        prod.ShouldNotContain("9465");
        PublishedPorts(prod).Count.ShouldBe(1, "üretim yığınında yalnız web bir port yayınlar");
    }

    [Fact]
    public void ObservabilityOverlay_PublishesOnlyGrafanaOnLoopback_AndKeepsTheNetworksInternal()
    {
        var overlay = Read("deploy/docker-compose.observability.yml");

        var ports = PublishedPorts(overlay);
        ports.Count.ShouldBe(1, "yalnız Grafana bir port yayınlar");
        ports[0].ShouldStartWith("127.0.0.1:", Case.Sensitive, "Grafana yalnız loopback'e bağlanır (bind geçersiz kılma yok)");
        ports[0].ShouldEndWith(":3000");

        foreach (var forbidden in new[] { ":9464", ":9465", ":9090", ":9187", ":9100", ":8080:" })
        {
            ports.ShouldAllBe(p => !p.Contains(forbidden, StringComparison.Ordinal), $"{forbidden} yayınlanamaz");
        }

        // observability ağı iç (çıkışsız); Prometheus ve exporter'lar yalnız iç ağlara bağlıdır.
        Regex.IsMatch(overlay, @"(?ms)^networks:\s*.*?^  observability:\s*\n\s+internal: true").ShouldBeTrue("observability ağı internal olmalı");
        var prometheus = ServiceBlock(overlay, "prometheus");
        prometheus.ShouldContain("networks: [backend, observability]");
        ServiceBlock(overlay, "postgres-exporter").ShouldContain("networks: [backend, observability]");
        ServiceBlock(overlay, "grafana").ShouldContain("networks: [observability, observability-ui]");
        prometheus.ShouldNotContain("observability-ui");
        prometheus.ShouldNotContain("ports:");
    }

    [Fact]
    public void ObservabilityOverlay_HasPinnedImages_AndNoDefaultOrPlaintextSecrets()
    {
        var overlay = Read("deploy/docker-compose.observability.yml");

        var images = Regex.Matches(overlay, @"^\s+image:\s*(\S+)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        images.Count.ShouldBeGreaterThanOrEqualTo(5);
        foreach (var image in images)
        {
            image.ShouldMatch(@":v?[0-9]+[.][0-9]+[.0-9a-z\-]*$", $"{image} sürüm etiketiyle sabitlenmeli (latest yok)");
        }

        overlay.ShouldContain("GF_SECURITY_ADMIN_PASSWORD__FILE: /run/secrets/grafana_admin_password");
        Regex.IsMatch(overlay, @"GF_SECURITY_ADMIN_PASSWORD:\s").ShouldBeFalse("Grafana yönetici parolası düz metin/varsayılan olamaz");
        overlay.ShouldNotContain("GF_SECURITY_ADMIN_PASSWORD: admin");
        Regex.IsMatch(overlay, @"\$\{(GRAFANA_ADMIN_PASSWORD|POSTGRES_PASSWORD|PG_MONITOR_PASSWORD)(?!_FILE)[^}]*:-").ShouldBeFalse("parola için varsayılan değer olamaz");
        foreach (var flag in new[] { "GF_USERS_ALLOW_SIGN_UP: \"false\"", "GF_AUTH_ANONYMOUS_ENABLED: \"false\"", "GF_ANALYTICS_REPORTING_ENABLED: \"false\"", "GF_ANALYTICS_CHECK_FOR_UPDATES: \"false\"" })
        {
            overlay.ShouldContain(flag);
        }

        // Metrik uç noktası yalnız overlay'de açılır ve belirteç dosyadan okunur (ortam değişkeninde düz belirteç yok).
        overlay.ShouldContain("Observability__Metrics__Enabled");
        overlay.ShouldContain("Observability__Metrics__BearerTokenFile: /run/secrets/metrics_bearer_token");
        Regex.IsMatch(overlay, @"Observability__Metrics__BearerToken:").ShouldBeFalse();
    }

    [Fact]
    public void SecretGenerators_CreateEveryObservabilitySecretTheOverlayReferences()
    {
        var overlay = Read("deploy/docker-compose.observability.yml");
        var sh = Read("deploy/generate-secrets.sh");
        var ps1 = Read("deploy/generate-secrets.ps1");

        foreach (var file in Regex.Matches(overlay, @"\./secrets/([a-z\-]+)\}").Select(m => m.Groups[1].Value))
        {
            sh.ShouldContain(file, Case.Sensitive, $"{file} generate-secrets.sh tarafından üretilmeli");
            ps1.ShouldContain(file, Case.Sensitive, $"{file} generate-secrets.ps1 tarafından üretilmeli");
        }
    }

    [Fact]
    public void EveryAlert_HasSeverityAndAWorkingRunbookAnchor()
    {
        var runbook = Read("docs/operations/runbook.md");
        var alerts = AlertBlocks().ToList();
        alerts.Count.ShouldBeGreaterThanOrEqualTo(15, "alarm kuralları bulunamadı ya da eksik");
        alerts.Select(a => a.Name).ShouldBeUnique();

        foreach (var (name, body) in alerts)
        {
            body.ShouldMatch(@"severity:\s*(critical|warning|info)", $"{name}: severity yok");
            body.ShouldContain($"runbook_url: \"docs/operations/runbook.md#{name.ToLowerInvariant()}\"", Case.Sensitive, $"{name}: runbook_url anchor'ı alarm adından türemeli");
            runbook.ShouldContain($"#### {name}", Case.Sensitive, $"{name}: runbook'ta '#### {name}' başlığı yok");
            body.ShouldMatch(@"expr:", $"{name}: expr yok");
            body.ShouldMatch(@"summary:", $"{name}: summary yok");
        }
    }

    [Fact]
    public void Dashboards_AreValidProvisionedJson_WithoutTenantOrUserLabels()
    {
        var dir = Path.Combine(Root, "infra", "observability", "grafana", "dashboards");
        var files = Directory.GetFiles(dir, "*.json");
        files.Length.ShouldBeGreaterThanOrEqualTo(5, "API, arka plan, güvenlik, kiracı yaşam döngüsü, Postgres panoları");

        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var uid = root.GetProperty("uid").GetString()!;
            uids.Add(uid).ShouldBeTrue($"{name}: uid benzersiz olmalı");
            root.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
            root.GetProperty("panels").GetArrayLength().ShouldBeGreaterThan(3, $"{name}: pano boş");

            var text = File.ReadAllText(file);
            foreach (Match m in DatasourceUid().Matches(text))
            {
                m.Groups[1].Value.ShouldBe("crm-prometheus", $"{name}: veri kaynağı provizyonlanan uid olmalı");
            }

            foreach (var expr in Exprs(root))
            {
                foreach (var forbidden in new[] { "tenant_id", "tenantid", "user_id", "userid", "email" })
                {
                    expr.ShouldNotContain(forbidden, Case.Insensitive, $"{name}: ifade '{forbidden}' etiketi içeremez");
                }
            }
        }
    }

    [Fact]
    public void OpenTelemetry_IsReferencedOnlyBySharedInfrastructure()
    {
        var offenders = Directory.GetFiles(AppContext.BaseDirectory, "Sense.Crm.*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .Where(a => a.GetName().Name != "Sense.Crm.Shared.Infrastructure")
            .Where(a => a.GetReferencedAssemblies().Any(r => r.Name!.StartsWith("OpenTelemetry", StringComparison.Ordinal)))
            .Select(a => a.GetName().Name)
            .ToList();

        offenders.ShouldBeEmpty("OpenTelemetry yalnız Shared.Infrastructure'a aittir; modüller yalnız CrmMetrics (BCL Meter) kullanır");
    }

    private static List<string> PublishedPorts(string compose)
    {
        var ports = new List<string>();
        var lines = compose.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s+ports:\s*$"))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Length && Regex.IsMatch(lines[j], @"^\s+-\s"); j++)
            {
                ports.Add(lines[j].Trim().TrimStart('-').Trim().Trim('"'));
            }
        }

        return ports;
    }

    private static string ServiceBlock(string compose, string service)
    {
        var match = Regex.Match(compose, $@"(?ms)^  {Regex.Escape(service)}:\s*\n(.*?)(?=^  [a-z][a-z0-9\-]*:\s*\n|^[a-z]+:|\z)");
        match.Success.ShouldBeTrue($"{service} servisi bulunamadı");
        return match.Groups[1].Value;
    }

    private static IEnumerable<(string Name, string Body)> AlertBlocks()
    {
        var dir = Path.Combine(Root, "infra", "observability", "prometheus", "rules");
        foreach (var file in Directory.GetFiles(dir, "*.yml"))
        {
            var text = File.ReadAllText(file);
            var parts = Regex.Split(text, @"(?m)^\s+- alert:\s*").Skip(1);
            foreach (var part in parts)
            {
                var name = part.Split('\n', 2)[0].Trim();
                yield return (name, part);
            }
        }
    }

    private static IEnumerable<string> Exprs(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "expr" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString()!;
                    }
                    else
                    {
                        foreach (var nested in Exprs(property.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Exprs(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    [GeneratedRegex("\"datasource\"\\s*:\\s*\\{[^}]*\"uid\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex DatasourceUid();

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sense.Crm.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Sense.Crm.slnx bulunamadı.");
    }
}
