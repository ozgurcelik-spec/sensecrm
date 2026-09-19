namespace Crm.Shared.Contracts.Configuration;

/// <summary>appsettings bölüm adları.</summary>
public static class ConfigurationSections
{
    public const string Paging = "Paging";
    public const string Localization = "Localization";
    public const string Caching = "Caching";
    public const string Outbox = "Outbox";
    public const string Diagnostics = "Diagnostics";
    public const string ProblemDetails = "ProblemDetails";
    public const string Auth = "Auth";
    public const string Cors = "Cors";
    public const string Identity = "Identity";
    public const string RateLimiting = "RateLimiting";
    public const string Registration = "Registration";
    public const string ForwardedHeaders = "ForwardedHeaders";
    public const string RequestLimits = "RequestLimits";
}

public static class ConnectionStringNames
{
    public const string Database = "Database";

    /// <summary>İsteğe bağlı: verilirse HybridCache'in L2 katmanı Redis olur; boşsa yalnız bellek içi (L1).</summary>
    public const string Redis = "Redis";
}

/// <summary>Liste uçlarının sayfalama sınırları.</summary>
public sealed class PagingOptions
{
    public int DefaultPageSize { get; set; } = PagingDefaults.DefaultPageSize;

    public int MaxPageSize { get; set; } = PagingDefaults.MaxPageSize;
}

public static class PagingDefaults
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int FirstPage = 1;

    /// <summary>Kabul edilen en büyük sayfa numarası; üstü 400 <c>validation</c> (L6: taşma/500 yok).</summary>
    public const int MaxPage = 1_000_000;
}

/// <summary>Desteklenen kültürler; varsayılan tr-TR (K8).</summary>
public sealed class LocalizationOptions
{
    public string DefaultCulture { get; set; } = Cultures.Turkish;

    public IList<string> SupportedCultures { get; set; } = [Cultures.Turkish, Cultures.English];

    public string ResourcesPath { get; set; } = LocalizationDefaults.ResourcesPath;
}

public static class Cultures
{
    public const string Turkish = "tr-TR";
    public const string English = "en-US";
    public const string TurkishLanguage = "tr";
    public const string EnglishLanguage = "en";

    /// <summary>Kullanıcı/organizasyon dil tercihi için geçerli değerler (K8).</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = [TurkishLanguage, EnglishLanguage];

    public static bool IsSupportedLanguage(string? value) => value is not null && SupportedLanguages.Contains(value, StringComparer.Ordinal);
}

public static class LocalizationDefaults
{
    public const string ResourcesPath = "Resources";
}

public sealed class CachingOptions
{
    public int DefaultExpirationMinutes { get; set; } = CachingDefaults.DefaultExpirationMinutes;

    public int LocalExpirationSeconds { get; set; } = CachingDefaults.LocalExpirationSeconds;

    public int PermissionExpirationMinutes { get; set; } = CachingDefaults.PermissionExpirationMinutes;

    public string KeyPrefixGlobal { get; set; } = CachingDefaults.GlobalPrefix;
}

public static class CachingDefaults
{
    public const int DefaultExpirationMinutes = 5;
    public const int LocalExpirationSeconds = 30;
    public const int PermissionExpirationMinutes = 10;

    /// <summary>
    /// Redis (paylaşımlı L2) yapılandırılmamışsa izin önbelleği yalnız süreç belleğindedir: rol/üyelik değişimi başka bir örnekte
    /// geçersiz kılınamaz, bu yüzden bayatlık üst sınırı kısa tutulur (M9). <c>Caching:PermissionExpirationMinutes</c> açıkça verilirse o kullanılır.
    /// </summary>
    public const int PermissionExpirationMinutesWithoutRedis = 2;
    public const string GlobalPrefix = "global";
    public const char KeySeparator = ':';
}

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = OutboxDefaults.BatchSize;

    public int MaxAttempts { get; set; } = OutboxDefaults.MaxAttempts;

    public int BaseBackoffSeconds { get; set; } = OutboxDefaults.BaseBackoffSeconds;

    public int MaxBackoffSeconds { get; set; } = OutboxDefaults.MaxBackoffSeconds;

    public int PollingIntervalSeconds { get; set; } = OutboxDefaults.PollingIntervalSeconds;
}

public static class OutboxDefaults
{
    public const int BatchSize = 50;
    public const int MaxAttempts = 10;
    public const int BaseBackoffSeconds = 5;
    public const int MaxBackoffSeconds = 3600;
    public const int PollingIntervalSeconds = 2;
    public const int MaxErrorLength = 4000;
}

public sealed class DiagnosticsOptions
{
    public int SlowRequestThresholdMs { get; set; } = DiagnosticsDefaults.SlowRequestThresholdMs;
}

public static class DiagnosticsDefaults
{
    public const int SlowRequestThresholdMs = 500;
}

public sealed class ProblemMappingOptions
{
    /// <summary>ProblemDetails.type için taban adres; sonuna hata kodu eklenir.</summary>
    public string TypeBaseUrl { get; set; } = ProblemDetailsDefaults.TypeBaseUrl;

    public bool IncludeExceptionDetails { get; set; }
}

public static class ProblemDetailsDefaults
{
    public const string TypeBaseUrl = "https://errors.crm.local/";
    public const string CodeExtension = "code";
    public const string TraceIdExtension = "traceId";
    public const string ErrorsExtension = "errors";
    public const string ArgsExtension = "args";
}

/// <summary>Tarayıcı istemcisinin (web/) çağırabileceği kökenler.</summary>
public sealed class CorsOptions
{
    public IList<string> AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Anonim kimlik doğrulama uçları (kayıt, giriş, yenileme) için IP bazlı sabit pencere hız sınırlama.
/// Politika adı (<see cref="RateLimitPolicyNames.Auth"/>) uçlara <c>[EnableRateLimiting]</c> ile bağlanır.
/// </summary>
public sealed class RateLimitingOptions
{
    public RateLimitPolicyOptions Auth { get; set; } = new();

    /// <summary>
    /// Kimliği doğrulanmış isteklerde kullanıcı başına genel sınır (M2). Cömert varsayılan: normal SPA kullanımı asla dokunmaz,
    /// çalınmış token / betikle kötüye kullanımı sınırlar. Kullanıcı kimliği JWT <c>sub</c> claim'idir.
    /// </summary>
    public RateLimitPolicyOptions User { get; set; } = new() { PermitLimit = RateLimitingDefaults.UserPermitLimit };

    /// <summary>Kimliği doğrulanmış isteklerde kiracı (organizasyon, JWT <c>tid</c>) başına genel sınır (M2).</summary>
    public RateLimitPolicyOptions Tenant { get; set; } = new() { PermitLimit = RateLimitingDefaults.TenantPermitLimit };

    /// <summary>Giriş denemeleri için e-posta anahtarlı ikinci kova (IP kovasına ek; M3). Bellek içi, tek örnek.</summary>
    public RateLimitPolicyOptions LoginEmail { get; set; } = new() { PermitLimit = RateLimitingDefaults.LoginEmailPermitLimit };
}

/// <summary>İstek boyutu sınırları (M2). Kestrel <c>MaxRequestBodySize</c> değeri buradan gelir.</summary>
public sealed class RequestLimitsOptions
{
    public long MaxRequestBodyBytes { get; set; } = RequestLimitsDefaults.MaxRequestBodyBytes;
}

public static class RequestLimitsDefaults
{
    /// <summary>1 MB: bu API yalnız küçük JSON gövdeleri alır (dosya yükleme yok).</summary>
    public const long MaxRequestBodyBytes = 1024 * 1024;
}

public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; set; } = RateLimitingDefaults.AuthPermitLimit;

    public int WindowSeconds { get; set; } = RateLimitingDefaults.WindowSeconds;

    public int QueueLimit { get; set; }
}

public static class RateLimitingDefaults
{
    public const int AuthPermitLimit = 20;
    public const int WindowSeconds = 60;
    public const int UserPermitLimit = 600;
    public const int TenantPermitLimit = 3000;
    public const int LoginEmailPermitLimit = 20;
}

public static class RateLimitPolicyNames
{
    public const string Auth = "auth";
}
