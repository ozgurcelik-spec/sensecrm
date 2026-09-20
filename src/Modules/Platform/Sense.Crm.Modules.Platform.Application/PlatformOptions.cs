using System.Text.RegularExpressions;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Shared.Contracts.Entitlements;

namespace Sense.Crm.Modules.Platform.Application;

/// <summary>
/// <c>Platform</c> yapılandırma bölümü (docs/plan/m7-saas-hazirlik.md). Ortam değişkeni: <c>Platform__…</c>. Plan kataloğu (<see cref="Plans"/>)
/// Migrator'da (<c>migrate</c>/<c>sync-plans</c>) <c>platform.plans</c>'a idempotent upsert edilir; API/Worker yalnız DB'den okur.
/// </summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public SignupOptions Signup { get; set; } = new();

    public ProvisioningOptions Provisioning { get; set; } = new();

    public IList<PlanDefinition> Plans { get; set; } = [];

    public EntitlementCacheOptions Entitlements { get; set; } = new();

    public UsageOptions Usage { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public DeletionOptions Deletion { get; set; } = new();
}

/// <summary>Kendi kendine kayıt (yalnız <c>Registration:Mode=open</c>): varsayılan plan.</summary>
public sealed class SignupOptions
{
    public string PlanCode { get; set; } = "starter";
}

/// <summary>Platform yöneticisinin açtığı organizasyonun (plan verilmezse) varsayılan planı.</summary>
public sealed class ProvisioningOptions
{
    public string DefaultPlanCode { get; set; } = "internal";
}

public sealed class EntitlementCacheOptions
{
    /// <summary>Ham durum önbellek süresi (sn); çok kopyada diğer kopyalar ≤ bu süre + L2 süresi bayat kalır.</summary>
    public int CacheSeconds { get; set; } = 30;
}

public sealed class UsageOptions
{
    /// <summary>Kayıt sayaçlarının önbellek süresi (sn; yumuşak limit). 0 = önbelleksiz.</summary>
    public int CacheSeconds { get; set; } = 300;

    public int SnapshotPollMinutes { get; set; } = 30;

    public int RetentionDays { get; set; } = 400;
}

public sealed class AuditOptions
{
    public int RetentionDays { get; set; } = 1825;
}

public sealed class DeletionOptions
{
    public int RetentionDays { get; set; } = 30;

    public int MinRetentionDays { get; set; } = PlatformLimits.MinRetentionDays;

    public int MaxRetentionDays { get; set; } = PlatformLimits.MaxRetentionDays;

    public int PollMinutes { get; set; } = 10;

    public int MaxAttempts { get; set; } = 10;

    public int ChunkSize { get; set; } = 10_000;
}

/// <summary>Yapılandırmadaki bir plan tanımı.</summary>
public sealed class PlanDefinition
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public int? TrialDays { get; set; }

    public PlanLimitsDefinition Limits { get; set; } = new();

    public Dictionary<string, bool> Modules { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PlanLimitsDefinition
{
    public int? MaxUsers { get; set; }

    public Dictionary<string, int?> MaxRecords { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Yapılandırma doğrulaması (Migrator hatada çıkış ≠ 0 verir → yayın durur; API/Worker <c>ValidateOnStart</c>): kod deseni
/// <c>^[a-z][a-z0-9_]{1,31}$</c>, limitler ≥ 0, <c>trialDays</c> 1–365 veya null, bilinmeyen modül anahtarı yok, kayıt ve varsayılan plan katalogda.
/// </summary>
public static partial class PlanCatalogValidator
{
    /// <summary>Limit anahtarı olarak kabul edilen modüller (çekirdek + kapı modülleri).</summary>
    public static IReadOnlyList<string> KnownModules { get; } = ["sales", "activities", .. GatedModules.All];

    [GeneratedRegex("^[a-z][a-z0-9_]{1,31}$")]
    private static partial Regex CodePattern();

    public static IReadOnlyList<string> ValidatePlans(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plan in options.Plans)
        {
            var label = string.IsNullOrEmpty(plan.Code) ? "(no code)" : plan.Code;
            if (!CodePattern().IsMatch(plan.Code))
            {
                errors.Add($"Plan '{label}': code must match ^[a-z][a-z0-9_]{{1,31}}$.");
            }
            else if (!seen.Add(plan.Code))
            {
                errors.Add($"Plan '{label}': duplicate code.");
            }

            if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > PlatformLimits.PlanNameMaxLength)
            {
                errors.Add($"Plan '{label}': name is required (max {PlatformLimits.PlanNameMaxLength}).");
            }

            if (plan.TrialDays is { } days && (days < 1 || days > 365))
            {
                errors.Add($"Plan '{label}': trialDays must be 1-365 or null.");
            }

            if (plan.Limits.MaxUsers is < 0)
            {
                errors.Add($"Plan '{label}': limits.maxUsers must be >= 0.");
            }

            foreach (var (module, max) in plan.Limits.MaxRecords)
            {
                if (!KnownModules.Contains(module, StringComparer.Ordinal))
                {
                    errors.Add($"Plan '{label}': limits.maxRecords has unknown module '{module}'.");
                }

                if (max is < 0)
                {
                    errors.Add($"Plan '{label}': limits.maxRecords.{module} must be >= 0.");
                }
            }

            foreach (var module in plan.Modules.Keys.Where(m => !GatedModules.IsGated(m)))
            {
                errors.Add($"Plan '{label}': modules has unknown or non-gateable module '{module}' (only {string.Join(", ", GatedModules.All)}).");
            }
        }

        return errors;
    }

    /// <summary>Plan doğrulaması + kayıt/varsayılan plan katalogda olmalı + aralık tutarlılığı.</summary>
    public static IReadOnlyList<string> Validate(PlatformOptions options)
    {
        var errors = ValidatePlans(options).ToList();
        var codes = options.Plans.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

        if (!codes.Contains(options.Signup.PlanCode))
        {
            errors.Add($"Platform:Signup:PlanCode '{options.Signup.PlanCode}' is not in Platform:Plans.");
        }

        if (!codes.Contains(options.Provisioning.DefaultPlanCode))
        {
            errors.Add($"Platform:Provisioning:DefaultPlanCode '{options.Provisioning.DefaultPlanCode}' is not in Platform:Plans.");
        }

        errors.AddRange(ValidateRanges(options));
        return errors;
    }

    /// <summary>Katalogdan bağımsız aralık tutarlılığı (API/Worker açılışında da denetlenir).</summary>
    public static IReadOnlyList<string> ValidateRanges(PlatformOptions options)
    {
        var errors = new List<string>();
        var deletion = options.Deletion;
        if (deletion.MinRetentionDays < 1 || deletion.MaxRetentionDays < deletion.MinRetentionDays)
        {
            errors.Add("Platform:Deletion: MinRetentionDays must be >= 1 and <= MaxRetentionDays.");
        }
        else if (deletion.RetentionDays < deletion.MinRetentionDays || deletion.RetentionDays > deletion.MaxRetentionDays)
        {
            errors.Add("Platform:Deletion:RetentionDays must be within [MinRetentionDays, MaxRetentionDays].");
        }

        if (deletion.PollMinutes < 1 || deletion.MaxAttempts < 1 || deletion.ChunkSize < 1)
        {
            errors.Add("Platform:Deletion: PollMinutes, MaxAttempts and ChunkSize must be >= 1.");
        }

        if (options.Entitlements.CacheSeconds < 0 || options.Usage.CacheSeconds < 0 || options.Usage.SnapshotPollMinutes < 1
            || options.Usage.RetentionDays < 1 || options.Audit.RetentionDays < 1)
        {
            errors.Add("Platform: cache seconds must be >= 0; poll minutes and retention days must be >= 1.");
        }

        return errors;
    }
}
