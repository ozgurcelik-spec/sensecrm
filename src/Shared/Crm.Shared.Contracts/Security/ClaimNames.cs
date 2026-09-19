namespace Crm.Shared.Contracts.Security;

/// <summary>JWT claim adları (tek kaynak: token üretimi ve middleware aynı sabitleri kullanır).</summary>
public static class ClaimNames
{
    public const string Subject = "sub";

    /// <summary>Aktif organizasyon (kiracı) kimliği (K6). Organizasyon değiştirilince yeni token verilir.</summary>
    public const string Tenant = "tid";
    public const string TenantSlug = "tslug";
    public const string Email = "email";
    public const string Name = "name";

    /// <summary>Aktif organizasyondaki rol adı (bilgi amaçlı; yetki kararı izin servisinden verilir).</summary>
    public const string Roles = "roles";
    public const string PlatformAdmin = "platform_admin";
    public const string Language = "lang";

    /// <summary>Parolası geçici olan hesap (<c>MustChangePassword</c>): değer <see cref="TrueValue"/> ise yalnız parola değiştirme uçları çalışır.</summary>
    public const string PasswordChangeRequired = "pwd_change";
    public const string TrueValue = "true";
}

/// <summary>HTTP başlık adları.</summary>
public static class CrmHeaderNames
{
    /// <summary>Uçtan uca mantıksal akış kimliği (istek + yanıt; giden HttpClient çağrılarına da taşınır). Yoksa sunucu üretir.</summary>
    public const string CorrelationId = "X-Correlation-Id";

    /// <summary>Tek bir HTTP isteğinin kimliği (proxy/ingress'ten gelirse korunur, yoksa sunucu üretir; yanıtta döner).</summary>
    public const string RequestId = "X-Request-Id";

    /// <summary>Yanıtta döner: W3C trace id.</summary>
    public const string TraceId = "X-Trace-Id";

    public const string RetryAfter = "Retry-After";

    /// <summary>
    /// Tarayıcıdaki JavaScript'in cross-origin yanıtta okuyabilmesi gereken başlıklar (CORS <c>Access-Control-Expose-Headers</c>).
    /// </summary>
    public static readonly string[] ExposedToClients =
    [
        CorrelationId,
        RequestId,
        TraceId,
        RetryAfter,
        "Content-Disposition",
        "api-supported-versions",
    ];
}

/// <summary>Sistem rolü kodları tüm modüllerin görebileceği yerde (yetki kontrolleri için).</summary>
public static class WellKnownRoles
{
    public const string System = "System";
}
