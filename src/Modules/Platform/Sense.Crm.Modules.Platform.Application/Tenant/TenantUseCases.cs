using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Platform.Application.Tenant;

// Kiracı tarafı: kendi aboneliği ve onboarding. URL'de kiracı kimliği YOKTUR (yalnız token'daki kiracı); [RequiresPermission] zorunlu
// (mimari test bu ad alanı için). Bu uçlar plan/askı bilgisini göstermek içindir.

/// <summary>
/// <c>GET /subscription</c> (<c>org.settings.manage</c>): plan, etkin durum, deneme, modüller, <b>sonlu</b> limitler ve kullanım. Kayıt sayıları önbellekli
/// (≤ 5 dk; <c>asOf</c>), kullanıcı sayısı canlı. <c>[TenantStatusExempt]</c>: askıdaki/silinmedeki kiracıda da çalışır (durumu göstermek için).
/// </summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
[TenantStatusExempt("Abonelik ekranı askıda/deneme bitmiş kiracıda da durumu göstermelidir")]
public sealed record GetSubscriptionQuery : IQuery<SubscriptionDto>;

public sealed class GetSubscriptionHandler(ITenantContext tenant, ITenantEntitlements entitlements, IUsageMeter meter, TimeProvider clock)
    : IQueryHandler<GetSubscriptionQuery, SubscriptionDto>
{
    public async Task<Result<SubscriptionDto>> Handle(GetSubscriptionQuery query, CancellationToken cancellationToken)
    {
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var (status, access) = snapshot.Evaluate(now);

        var users = await meter.CountUsersAsync(cancellationToken).ConfigureAwait(false);
        var counts = await meter.GetRecordCountsAsync(cancellationToken).ConfigureAwait(false);
        var records = counts.Records
            .Where(r => snapshot.IsModuleEnabled(r.Key))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        var metrics = counts.Records.ToDictionary(r => r.Key + ".records", r => r.Value, StringComparer.Ordinal);
        metrics[EntitlementMath.StorageBytesKey] = counts.StorageBytes; // M8C
        // M8B: webhook/API anahtari sayaclari canlidir (sert limit; onbelleksiz), yalniz modul planda aciksa sorgulanir.
        var integrationsOn = snapshot.IsModuleEnabled(GatedModules.Integrations);
        var integrationsCounts = integrationsOn ? await meter.CountModuleAsync(GatedModules.Integrations, cancellationToken).ConfigureAwait(false) : new Dictionary<string, long>();
        foreach (var (key, value) in integrationsCounts)
        {
            metrics[key] = value;
        }

        var usageWithIntegrations = new UsageCollection(users.Active, users.Pending, metrics);
        var overLimit = EntitlementMath.OverLimits(snapshot.MaxUsers, snapshot.MaxRecords, snapshot.Modules, usageWithIntegrations, snapshot.MaxWebhooks, snapshot.MaxApiKeys, snapshot.MaxStorageMb);

        var modules = GatedModules.All.ToDictionary(m => m, m => snapshot.IsModuleEnabled(m), StringComparer.Ordinal);
        return new SubscriptionDto(
            snapshot.PlanCode,
            snapshot.PlanName,
            status,
            access,
            snapshot.TrialEndsOn,
            EntitlementMath.TrialDaysLeft(snapshot, status, now),
            modules,
            new SubscriptionLimitsDto(snapshot.MaxUsers, EntitlementMath.FiniteRecords(snapshot.MaxRecords), snapshot.MaxWebhooks, snapshot.MaxApiKeys, snapshot.MaxStorageMb),
            new SubscriptionUsageDto(
                counts.AsOf,
                users.Active,
                users.Pending,
                records,
                integrationsOn ? integrationsCounts.GetValueOrDefault("integrations.webhooks") : null,
                integrationsOn ? integrationsCounts.GetValueOrDefault("integrations.api_keys") : null,
                counts.StorageBytes,
                counts.FileCount),
            overLimit);
    }
}

/// <summary>
/// <c>GET /onboarding</c> (<c>org.settings.manage</c>): dört sabit adım (<c>profile</c>, <c>invite_user</c>, <c>create_lead</c>, <c>create_workflow_rule</c>;
/// <c>workflows</c> kapalıysa son adım listeden çıkar). Tamamlanan adım kalıcı yazılır (sonra tekrar sayılmaz); <c>dismissed</c> veya hepsi tamam ise sayım
/// <b>yapılmadan</b> dönülür. Sayım önbellek kullanmaz.
/// </summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record GetOnboardingQuery : IQuery<OnboardingDto>;

public sealed class GetOnboardingHandler(
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    ITenantAccountRepository accounts,
    IUsageMeter meter,
    IPlatformUnitOfWork unitOfWork) : IQueryHandler<GetOnboardingQuery, OnboardingDto>
{
    public async Task<Result<OnboardingDto>> Handle(GetOnboardingQuery query, CancellationToken cancellationToken)
    {
        // Satır yoksa (olay henüz işlenmedi) tembel satır burada açılır.
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var account = await accounts.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var workflowsOn = snapshot.IsModuleEnabled(GatedModules.Workflows);
        var keys = new List<string> { OnboardingKeys.Profile, OnboardingKeys.InviteUser, OnboardingKeys.CreateLead };
        if (workflowsOn)
        {
            keys.Add(OnboardingKeys.CreateWorkflowRule);
        }

        var done = keys.Where(k => account.OnboardingDone.Contains(k, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (account.OnboardingDismissedAt is null && done.Count < keys.Count)
        {
            var usage = await meter.CollectAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
            var m = usage.Metrics;
            var newlyDone = new List<string>();
            AddIf(newlyDone, done, OnboardingKeys.Profile, m.GetValueOrDefault("identity.profile_completed") >= 1);
            AddIf(newlyDone, done, OnboardingKeys.InviteUser, usage.UsersUsed >= 2);
            AddIf(newlyDone, done, OnboardingKeys.CreateLead, m.GetValueOrDefault("sales.leads") >= 1);
            if (workflowsOn)
            {
                AddIf(newlyDone, done, OnboardingKeys.CreateWorkflowRule, m.GetValueOrDefault("workflows.workflow_rules") >= 1);
            }

            if (account.MarkOnboardingDone(newlyDone))
            {
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var items = keys.Select(k => new OnboardingItemDto(k, done.Contains(k))).ToList();
        return new OnboardingDto(account.OnboardingDismissedAt is not null, items.Count(i => i.Done), items.Count, items);
    }

    private static void AddIf(List<string> newlyDone, HashSet<string> done, string key, bool condition)
    {
        if (!done.Contains(key) && condition)
        {
            done.Add(key);
            newlyDone.Add(key);
        }
    }
}

/// <summary><c>POST /onboarding/dismiss</c> → 204 (idempotent; salt okunur kipte yazma olduğundan <c>tenant.suspended</c>).</summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record DismissOnboardingCommand : ICommand;

public sealed class DismissOnboardingHandler(ITenantContext tenant, ITenantEntitlements entitlements, ITenantAccountRepository accounts, TimeProvider clock)
    : ICommandHandler<DismissOnboardingCommand>
{
    public async Task<Result> Handle(DismissOnboardingCommand command, CancellationToken cancellationToken)
    {
        await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var account = await accounts.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        account.DismissOnboarding(clock.GetUtcNow().UtcDateTime);
        return Result.Success();
    }
}
