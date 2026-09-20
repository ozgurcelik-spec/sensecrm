using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Plans;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Kernel.Time;

namespace Sense.Crm.Modules.Platform.Application;

/// <summary>Saf yardımcılar: etkin hak birleştirme sonucundan görünüm/anlık görüntü, deneme günü ↔ an dönüşümü, aşım hesabı.</summary>
public static class EntitlementMath
{
    /// <summary>
    /// Deneme bitiş anı (UTC): <paramref name="trialEndsOn"/> gününden <b>sonraki</b> günün başlangıcı (kiracı saat diliminde). Europe/Istanbul'da
    /// <c>2026-12-31</c> → <c>2026-12-31T21:00:00Z</c>; <c>now &gt;= bu an</c> ise deneme bitmiştir.
    /// </summary>
    public static DateTime TrialEndsAtUtc(DateOnly trialEndsOn, string timeZone) =>
        TenantCalendar.For(timeZone).StartOfDayUtc(trialEndsOn.AddDays(1));

    /// <summary>Plan + hesap istisnası → etkin haklar.</summary>
    public static EffectiveLimits Effective(Plan plan, TenantAccount account) =>
        EffectiveEntitlements.Merge(plan.Limits, plan.Modules, TenantOverrides.FromJson(account.Overrides), GatedModules.All);

    public static EntitlementSnapshot ToSnapshot(TenantAccount account, Plan plan, string timeZone)
    {
        var effective = Effective(plan, account);
        return new EntitlementSnapshot(
            account.TenantId,
            plan.Code,
            plan.Name,
            account.Status,
            account.SuspensionMode,
            account.TrialEndsAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
            account.TrialEndsOn,
            timeZone,
            effective.Modules,
            effective.MaxUsers,
            effective.MaxRecords,
            effective.MaxWebhooks,
            effective.MaxApiKeys);
    }

    /// <summary>Mevcut kullanım (kullanıcı = etkin + bekleyen; kayıtlar yalnız açık modüller) sonlu limitin üstündeyse aşım listesi.</summary>
    public static IReadOnlyList<OverLimitDto> OverLimits(
        int? maxUsers,
        IReadOnlyDictionary<string, int?> maxRecords,
        IReadOnlyDictionary<string, bool> modules,
        UsageCollection usage,
        int? maxWebhooks = null,
        int? maxApiKeys = null)
    {
        var result = new List<OverLimitDto>();
        if (maxUsers is { } users && usage.UsersUsed > users)
        {
            result.Add(new OverLimitDto(LimitKeys.Users, null, users, usage.UsersUsed));
        }

        // M8B: webhook/API anahtari limitleri (kullanim: integrations.webhooks, integrations.api_keys; modul kapaliysa raporlanmaz).
        var integrationsOn = modules.TryGetValue(GatedModules.Integrations, out var integrations) && integrations;
        if (integrationsOn && maxWebhooks is { } webhooks && usage.Metrics.GetValueOrDefault("integrations.webhooks") > webhooks)
        {
            result.Add(new OverLimitDto(LimitKeys.Webhooks, null, webhooks, usage.Metrics["integrations.webhooks"]));
        }

        if (integrationsOn && maxApiKeys is { } apiKeys && usage.Metrics.GetValueOrDefault("integrations.api_keys") > apiKeys)
        {
            result.Add(new OverLimitDto(LimitKeys.ApiKeys, null, apiKeys, usage.Metrics["integrations.api_keys"]));
        }

        var records = usage.Records;
        foreach (var (module, max) in maxRecords.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (max is not { } limit || (GatedModules.IsGated(module) && !(modules.TryGetValue(module, out var on) && on)))
            {
                continue;
            }

            if (records.TryGetValue(module, out var used) && used > limit)
            {
                result.Add(new OverLimitDto(LimitKeys.Records, module, limit, used));
            }
        }

        return result;
    }

    /// <summary>Yalnız sonlu limitler.</summary>
    public static IReadOnlyDictionary<string, int> FiniteRecords(IReadOnlyDictionary<string, int?> maxRecords) =>
        maxRecords.Where(m => m.Value.HasValue).ToDictionary(m => m.Key, m => m.Value!.Value, StringComparer.Ordinal);

    /// <summary>Etkin durum, kiracı saat diliminde bugünden deneme bitişine kalan gün (0 = son gün); yalnız <c>trial</c> durumunda.</summary>
    public static int? TrialDaysLeft(EntitlementSnapshot snapshot, string status, DateTimeOffset now) => snapshot.TrialDaysLeft(status, now);
}
