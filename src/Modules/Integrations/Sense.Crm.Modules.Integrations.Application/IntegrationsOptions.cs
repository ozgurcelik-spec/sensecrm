namespace Sense.Crm.Modules.Integrations.Application;

/// <summary>
/// <c>Integrations</c> yapılandırma bölümü (ortam değişkeni <c>Integrations__…</c>; docs/plan/m8b-entegrasyonlar.md "Yapılandırma özeti"). Varsayılan
/// güvenli: webhook gönderimi <b>kapalıdır</b> (<see cref="WebhookOptions.Enabled"/> = false); açılış için egress ağı bilinçli yapılandırılmalıdır.
/// </summary>
public sealed class IntegrationsOptions
{
    public const string SectionName = "Integrations";

    public WebhookOptions Webhooks { get; set; } = new();

    public ApiKeyOptions ApiKeys { get; set; } = new();

    public EncryptionOptions Encryption { get; set; } = new();

    public OpenApiOptions OpenApi { get; set; } = new();

    /// <summary>
    /// Ortam Development/Testing mi? Yapılandırma dosyasından okunmaz: modül kaydı <c>IHostEnvironment</c>'tan yazar (<c>DevAllowLoopback</c>/<c>AllowDirectEgress</c>
    /// yalnız bu ortamlarda dikkate alınır; Production'da yok sayılır).
    /// </summary>
    public bool DevelopmentLike { get; set; }
}

public sealed class WebhookOptions
{
    /// <summary>Varsayılan kapalı: kapalıyken fan-out satır yazmaz, <c>test</c>/<c>redeliver</c> <c>409 webhook.delivery_unavailable</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Egress proxy adresi (<c>http://ip:3128</c>); Production'da <see cref="Enabled"/> ise zorunlu.</summary>
    public string? EgressProxy { get; set; }

    /// <summary>Çözümleyici (egress-dns) adresi (<c>ip</c> veya <c>ip:port</c>); boşsa sistem çözümleyicisi (yalnız Development/Testing).</summary>
    public string? DnsServer { get; set; }

    /// <summary>Proxy/DNS olmadan doğrudan çıkış (yalnız Development/Testing).</summary>
    public bool AllowDirectEgress { get; set; }

    /// <summary>Loopback hedeflerine (ve <c>http</c>) izin (yalnız Development/Testing; yerel alıcı testleri için).</summary>
    public bool DevAllowLoopback { get; set; }

    public IList<string>? AllowedHosts { get; set; }

    public IList<string>? AllowedPrivateCidrs { get; set; }

    public IList<int>? AllowedPorts { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public int ConnectTimeoutSeconds { get; set; } = 5;

    public int MaxResponseBytes { get; set; } = 65536;

    public int ResponseSnippetBytes { get; set; } = 2048;

    public int MaxPayloadBytes { get; set; } = 65536;

    public int MaxAttempts { get; set; } = 8;

    /// <summary>Hata sonrası bekleme (sn): 10 sn → 1 dk → 5 dk → 30 dk → 2 sa → 6 sa → 12 sa (toplam ≈ 20,6 sa).</summary>
    public IList<int>? BackoffSeconds { get; set; }

    public int JitterPercent { get; set; } = 20;

    public int MaxEventAgeHours { get; set; } = 72;

    public int MaxConcurrentPerTenant { get; set; } = 4;

    public int MaxConcurrentGlobal { get; set; } = 32;

    public int MaxConcurrentPerHost { get; set; } = 2;

    public int PerTenantPerMinute { get; set; } = 600;

    public int PollSeconds { get; set; } = 2;

    public int BatchSize { get; set; } = 50;

    public int PerTenantBatch { get; set; } = 10;

    public int LeaseSeconds { get; set; } = 60;

    public int AutoDisableAfterFailures { get; set; } = 10;

    public int DeliveryRetentionDays { get; set; } = 30;

    public int TestPingPerMinute { get; set; } = 5;

    /// <summary>Yeniden gönderme: kiracı başına dakikada.</summary>
    public int RedeliverPerMinute { get; set; } = 20;

    /// <summary>Kimlik doğrulama başarısızlık sonrası kısa yeniden dene aralığı (sn) — kota dolunca ertelemenin alt sınırı.</summary>
    public int DeferMinSeconds { get; set; } = 5;

    public int DeferMaxSeconds { get; set; } = 15;

    /// <summary>Askı/plan kapalı kiracıda satırın ertelenme süresi (dk).</summary>
    public int SuspendedRecheckMinutes { get; set; } = 5;

    public static IReadOnlyList<int> DefaultBackoffSeconds { get; } = [10, 60, 300, 1800, 7200, 21600, 43200];

    public static IReadOnlyList<int> DefaultPorts { get; } = [443, 8443];

    public IReadOnlyList<int> EffectiveBackoff => BackoffSeconds is { Count: > 0 } list ? [.. list] : DefaultBackoffSeconds;

    public IReadOnlyList<int> EffectivePorts => AllowedPorts is { Count: > 0 } list ? [.. list] : DefaultPorts;

    /// <summary>Liste öğeleri virgül/boşlukla da bölünür (tek ortam değişkeninde birden çok değer: <c>WEBHOOKS_ALLOWED_HOSTS="a.com *.b.com"</c>).</summary>
    public IReadOnlyList<string> EffectiveAllowedHosts => Split(AllowedHosts);

    public IReadOnlyList<string> EffectivePrivateCidrs => Split(AllowedPrivateCidrs);

    private static List<string> Split(IList<string>? values) =>
        values is null ? [] : [.. values.SelectMany(v => (v ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
}

public sealed class ApiKeyOptions
{
    public int DefaultLifetimeDays { get; set; } = 365;

    public int MaxLifetimeDays { get; set; } = 730;

    public int CacheSeconds { get; set; } = 30;

    public int LastUsedWriteMinutes { get; set; } = 5;

    public int UsageFlushSeconds { get; set; } = 60;

    public int UsageRetentionDays { get; set; } = 180;

    public int MaxCidrsPerKey { get; set; } = 10;

    public FailureThrottleOptions FailureThrottle { get; set; } = new();
}

public sealed class FailureThrottleOptions
{
    public int MaxFailures { get; set; } = 20;

    public int WindowMinutes { get; set; } = 10;
}

public sealed class EncryptionOptions
{
    public const string DefaultKeyId = "k1";

    public string CurrentKeyId { get; set; } = DefaultKeyId;

    /// <summary><c>Integrations:Encryption:Keys:&lt;id&gt;</c> = base64(32 bayt). Production'da en az mevcut anahtar zorunludur (yoksa API/Worker başlamaz).</summary>
    public IDictionary<string, string?> Keys { get; set; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}

public sealed class OpenApiOptions
{
    public int CacheMinutes { get; set; } = 10;
}

/// <summary>Yapılandırma doğrulaması (<c>ValidateOnStart</c>): tutarsız aralık, eksik egress/şifreleme yapılandırması açılışta reddedilir.</summary>
public static class IntegrationsOptionsValidator
{
    public static IReadOnlyList<string> Validate(IntegrationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var w = options.Webhooks;
        if (w.TimeoutSeconds is < 1 or > 30 || w.ConnectTimeoutSeconds is < 1 or > 30 || w.ConnectTimeoutSeconds > w.TimeoutSeconds)
        {
            errors.Add("Integrations:Webhooks: TimeoutSeconds must be 1-30 and ConnectTimeoutSeconds 1-TimeoutSeconds.");
        }

        if (w.MaxAttempts < 1 || w.MaxAttempts > 20 || w.EffectiveBackoff.Count < w.MaxAttempts - 1 || w.EffectiveBackoff.Any(b => b < 1))
        {
            errors.Add("Integrations:Webhooks: MaxAttempts must be 1-20 and BackoffSeconds must cover MaxAttempts - 1 positive delays.");
        }

        if (w.JitterPercent is < 0 or > 100 || w.MaxResponseBytes < 1 || w.ResponseSnippetBytes < 1 || w.MaxPayloadBytes < 1024 || w.MaxEventAgeHours < 1)
        {
            errors.Add("Integrations:Webhooks: JitterPercent 0-100; MaxResponseBytes, ResponseSnippetBytes >= 1; MaxPayloadBytes >= 1024; MaxEventAgeHours >= 1.");
        }

        if (w.MaxConcurrentGlobal < 1 || w.MaxConcurrentPerTenant < 1 || w.MaxConcurrentPerHost < 1 || w.PerTenantPerMinute < 1 || w.PollSeconds < 1
            || w.BatchSize < 1 || w.PerTenantBatch < 1 || w.LeaseSeconds < 1 || w.AutoDisableAfterFailures < 1 || w.DeliveryRetentionDays < 1)
        {
            errors.Add("Integrations:Webhooks: concurrency, batch, lease, poll and retention values must be >= 1.");
        }

        if (w.EffectivePorts.Any(p => p is < 1 or > 65535))
        {
            errors.Add("Integrations:Webhooks:AllowedPorts must be valid TCP ports.");
        }

        foreach (var cidr in w.EffectivePrivateCidrs)
        {
            if (!Security.Cidr.TryParse(cidr, out _))
            {
                errors.Add($"Integrations:Webhooks:AllowedPrivateCidrs has an invalid CIDR '{cidr}'.");
            }
        }

        if (w.Enabled && !options.DevelopmentLike && (string.IsNullOrWhiteSpace(w.EgressProxy) || string.IsNullOrWhiteSpace(w.DnsServer)))
        {
            errors.Add("Integrations:Webhooks:Enabled requires EgressProxy and DnsServer outside Development/Testing (direct egress is dev-only).");
        }

        var a = options.ApiKeys;
        if (a.DefaultLifetimeDays < 1 || a.MaxLifetimeDays < a.DefaultLifetimeDays || a.CacheSeconds < 0 || a.LastUsedWriteMinutes < 1 || a.UsageFlushSeconds < 1
            || a.MaxCidrsPerKey < 1 || a.FailureThrottle.MaxFailures < 1 || a.FailureThrottle.WindowMinutes < 1)
        {
            errors.Add("Integrations:ApiKeys: lifetimes, cache, flush and throttle values are inconsistent.");
        }

        return errors;
    }
}
