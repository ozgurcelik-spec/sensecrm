using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Infrastructure.Entitlements;

/// <summary>
/// Platform modülü yüklü değilken (ör. yalnız Migrator ya da eski host) varsayılan davranış: her modül açık, tam erişim, limitsiz. Platform modülü
/// <c>services.Replace</c> ile gerçek uygulamalarını kaydeder.
/// </summary>
public sealed class UnlimitedEntitlements : ITenantEntitlements
{
    public const string PlanCode = "unlimited";

    private static readonly IReadOnlyDictionary<string, bool> AllModules = GatedModules.All.ToDictionary(m => m, _ => true, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, int?> NoRecordLimits = new Dictionary<string, int?>(StringComparer.Ordinal);

    public Task<EntitlementSnapshot> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(new EntitlementSnapshot(
            tenantId,
            PlanCode,
            PlanCode,
            StoredTenantStatuses.Active,
            SuspensionMode: null,
            TrialEndsAt: null,
            TrialEndsOn: null,
            TimeZone: "UTC",
            AllModules,
            MaxUsers: null,
            NoRecordLimits));
}

/// <summary>Varsayılan limit denetimi: hiçbir şeyi engellemez.</summary>
public sealed class AllowAllLimitGuard : ILimitGuard
{
    public Task<Result> EnsureAsync(LimitDemand demand, CancellationToken ct = default) => Task.FromResult(Result.Success());
}

/// <summary>Varsayılan plan kataloğu: her kod atanabilir (Platform yokken doğrulama yapılmaz).</summary>
public sealed class AllowAllPlanCatalog : IPlanCatalog
{
    public Task<bool> IsAssignableAsync(string planCode, CancellationToken ct = default) => Task.FromResult(true);

    public string ProvisioningPlanCode => UnlimitedEntitlements.PlanCode;
}

/// <summary>Varsayılan platform denetim alıcısı: hiçbir şey yazmaz (Platform modülü gerçeğini Replace eder).</summary>
public sealed class NoOpPlatformAuditSink : IPlatformAuditSink
{
    public Task RecordAsync(string action, Guid? targetTenantId, string? targetTenantName, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Varsayılan platform yöneticisi doğrulayıcısı: kimseyi kabul etmez (Identity gerçek uygulamayı kaydeder).</summary>
public sealed class DenyPlatformAdminVerifier : IPlatformAdminVerifier
{
    public Task<bool> IsPlatformAdminAsync(Guid userId, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Varsayılan API anahtarı doğrulayıcısı: her anahtarı reddeder (Integrations modülü gerçeğini Replace eder).</summary>
public sealed class DenyApiKeyAuthenticator : IApiKeyAuthenticator
{
    public Task<ApiKeyAuthResult> AuthenticateAsync(IReadOnlyList<string> authorizationHeaders, string? remoteIp, CancellationToken ct = default) =>
        Task.FromResult(ApiKeyAuthResult.Fail(Error.Unauthorized(ErrorCodes.Unauthenticated)));
}

/// <summary>Varsayılan kullanım alıcısı: hiçbir şey kaydetmez.</summary>
public sealed class NoOpApiKeyUsageSink : IApiKeyUsageSink
{
    public void Record(Guid tenantId, Guid keyId, int statusCode)
    {
    }
}
