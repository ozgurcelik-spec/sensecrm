using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Kernel.Time;

namespace Sense.Crm.Modules.Platform.Application.Provisioning;

/// <summary>
/// Kiracı hesabı açılışı ve backfill (docs/plan/m7-saas-hazirlik.md "Yaşam döngüsü başlangıcı"). Hesap: plan = istekteki <c>PlanCode</c> ?? (<c>signup</c> ise
/// <c>Platform:Signup:PlanCode</c>, aksi <c>Platform:Provisioning:DefaultPlanCode</c>); deneme = istekteki <c>TrialEndsOn</c> ?? (planın <c>trialDays</c>'i
/// varsa bugün + <c>trialDays</c>, kiracı saatinde); <c>is_system</c> = <c>origin = bootstrap</c>. <b>İdempotent upsert</b>: satır yoksa ekler, <c>source = lazy</c> satırı
/// gerçek olayın planı/kaynağıyla düzeltir, başka kaynaklı satıra dokunmaz. Kaydetme çağıranın sorumluluğundadır.
/// </summary>
public sealed class AccountProvisioner(
    ITenantAccountRepository accounts,
    IPlanRepository plans,
    IOptions<PlatformOptions> options,
    TimeProvider clock)
{
    public const string InternalPlanCode = "internal";

    public async Task<TenantAccount?> ProvisionAsync(OrganizationCreated created, TenantInfo? info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(created);
        var settings = options.Value;
        var source = created.Origin switch
        {
            OrganizationOrigin.Signup => AccountSources.Signup,
            OrganizationOrigin.Bootstrap => AccountSources.Bootstrap,
            _ => AccountSources.Platform,
        };
        var planCode = !string.IsNullOrWhiteSpace(created.PlanCode)
            ? created.PlanCode.Trim()
            : created.Origin == OrganizationOrigin.Signup ? settings.Signup.PlanCode : settings.Provisioning.DefaultPlanCode;
        var plan = await plans.GetAsync(planCode, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Plan '{planCode}' is not in the catalog (run the Migrator).");

        var timeZone = info?.TimeZone ?? TenantCalendar.UtcId;
        var now = clock.GetUtcNow();
        var trialOn = created.TrialEndsOn
            ?? (plan.TrialDays is { } days ? TenantCalendar.For(timeZone).Today(now).AddDays(days) : (DateOnly?)null);
        DateTime? trialAt = trialOn is { } on ? EntitlementMath.TrialEndsAtUtc(on, timeZone) : null;

        var name = string.IsNullOrWhiteSpace(created.Name) ? info?.Name ?? created.TenantId.ToString("N") : created.Name;
        var slug = !string.IsNullOrWhiteSpace(created.Slug) ? created.Slug : SlugOf(created.TenantId, info);
        var isSystem = created.Origin == OrganizationOrigin.Bootstrap;

        var existing = await accounts.GetAsync(created.TenantId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var account = TenantAccount.Create(created.TenantId, name, slug, plan.Code, source, isSystem, trialOn, trialAt, created.OccurredAt.UtcDateTime, onboardingDismissedAt: null);
            accounts.Add(account);
            return account;
        }

        if (existing.Source == AccountSources.Lazy)
        {
            existing.ApplyProvisioning(plan.Code, source, isSystem, trialOn, trialAt, name, slug);
        }

        return existing;
    }

    /// <summary>
    /// Mevcut (satırı olmayan) kiracı için <c>internal</c> plan, <c>source = backfill</c>, denemesiz, onboarding kapalı. <paramref name="isSystem"/>: platform
    /// işletim organizasyonu. Satır varsa dokunmaz (idempotent).
    /// </summary>
    public async Task<bool> EnsureBackfilledAsync(TenantInfo info, bool isSystem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        var existing = await accounts.GetAsync(info.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return false;
        }

        _ = await plans.GetAsync(InternalPlanCode, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Plan '{InternalPlanCode}' is not in the catalog (run sync-plans first).");
        var now = clock.GetUtcNow().UtcDateTime;
        var created = info.CreatedAt is { } at ? at.UtcDateTime : now;
        accounts.Add(TenantAccount.Create(
            info.Id,
            info.Name,
            SlugOf(info.Id, info),
            InternalPlanCode,
            isSystem ? AccountSources.Bootstrap : AccountSources.Backfill,
            isSystem,
            trialEndsOn: null,
            trialEndsAt: null,
            created,
            onboardingDismissedAt: now));
        return true;
    }

    /// <summary>
    /// Platform işletim organizasyonu (<c>create-platform-admin</c>): satır yoksa <c>internal</c> plan + <c>is_system</c>, <c>source = bootstrap</c> ile açılır; varsa
    /// yalnız <c>is_system</c> işaretlenir (Worker'ı beklemez). Kaydetme çağıranındır.
    /// </summary>
    public async Task EnsureSystemAsync(TenantInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        var existing = await accounts.GetAsync(info.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.MarkSystem();
            return;
        }

        await EnsureBackfilledAsync(info, isSystem: true, cancellationToken).ConfigureAwait(false);
    }

    private static string SlugOf(Guid tenantId, TenantInfo? info) =>
        !string.IsNullOrWhiteSpace(info?.Slug) ? info.Slug : "t-" + tenantId.ToString("N")[..12];
}

/// <summary>
/// <c>OrganizationCreated</c> → Platform hesabı (<c>[EntitlementExempt]</c>: yaşam döngüsünün kendisi kiracı durumundan bağımsız çalışır).
/// Olay Worker'da (ve testlerde outbox boşaltıldığında) işlenir; hesap yoksa <c>ITenantEntitlements</c> tembel satır açar (bkz. Infrastructure).
/// </summary>
[EntitlementExempt("Platform hesap açılışı kiracı durumundan bağımsızdır")]
public sealed class OrganizationCreatedAccountHandler(AccountProvisioner provisioner, ITenantDirectory directory, IPlatformUnitOfWork unitOfWork, IEntitlementCache cache, IPlatformAuditReconciler auditReconciler)
    : IIntegrationEventHandler<OrganizationCreated>
{
    public async Task Handle(OrganizationCreated integrationEvent, CancellationToken cancellationToken)
    {
        var info = await directory.FindAsync(integrationEvent.TenantId, cancellationToken).ConfigureAwait(false);
        var account = await provisioner.ProvisionAsync(integrationEvent, info, cancellationToken).ConfigureAwait(false);

        // C-SEC2 L4: platform yolunda açılan organizasyonun `organization.created` denetim satırı iki DbContext arasında aynı işlemde yazılamaz (doğrudan yol Identity işleminden SONRA çalışır);
        // olay ise Identity işlemiyle birlikte outbox'a yazıldığı için burada satır yoksa tamamlanır (idempotent).
        if (integrationEvent is { Origin: OrganizationOrigin.Platform, ActorUserId: { } actor })
        {
            await auditReconciler.EnsureAsync(
                PlatformAuditActions.OrganizationCreated,
                integrationEvent.TenantId,
                account?.Name ?? info?.Name,
                actor,
                integrationEvent.OccurredAt.UtcDateTime,
                new Dictionary<string, object?> { ["planCode"] = account?.PlanCode ?? integrationEvent.PlanCode, ["trialEndsOn"] = integrationEvent.TrialEndsOn?.ToString("yyyy-MM-dd"), ["reconciled"] = true },
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await cache.InvalidateAsync(integrationEvent.TenantId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>OrganizationUpdated</c> → konsoldaki ad okuma kopyasını eşitler.</summary>
[EntitlementExempt("Platform okuma kopyası eşitleme kiracı durumundan bağımsızdır")]
public sealed class OrganizationUpdatedAccountHandler(ITenantAccountRepository accounts, IPlatformUnitOfWork unitOfWork) : IIntegrationEventHandler<OrganizationUpdated>
{
    public async Task Handle(OrganizationUpdated integrationEvent, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(integrationEvent.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        account.SyncName(integrationEvent.Name);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
