using System.Text.Json;
using FluentValidation;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Platform.Application.Console;

// Platform konsolu (yalnız platform yöneticisi): her istek [PlatformAdminOnly] (mimari test bu ad alanı için zorunlu kılar). Yanıtlarda
// kiracı iş verisi yoktur; yalnız hesap/plan/sayaç.

/// <summary>
/// Organizasyon listesi: <c>q</c> (ad + slug, ILIKE + kaçışlı), <c>status</c> = <b>etkin</b> durum, <c>planCode</c>, <c>source</c>;
/// <c>sort</c>: <c>name</c> (varsayılan artan), <c>createdAt</c>, <c>users</c> (boşlar her iki yönde sonda), <c>plan</c>, <c>status</c>.
/// </summary>
[PlatformAdminOnly]
public sealed record ListOrganizationsQuery(PagedQuery Paging, string? Status, string? PlanCode, string? Source) : IQuery<PagedResult<OrganizationRowDto>>;

public sealed class ListOrganizationsValidator : AbstractValidator<ListOrganizationsQuery>
{
    public ListOrganizationsValidator()
    {
        RuleFor(x => x.Status).Must(v => string.IsNullOrEmpty(v) || TenantStatuses.All.Contains(v, StringComparer.Ordinal)).WithMessage(PlatformErrors.InvalidRange);
        RuleFor(x => x.Source).Must(v => string.IsNullOrEmpty(v) || AccountSources.All.Contains(v, StringComparer.Ordinal)).WithMessage(PlatformErrors.InvalidRange);
    }
}

public sealed class ListOrganizationsHandler(IPlatformReadStore store, TimeProvider clock) : IQueryHandler<ListOrganizationsQuery, PagedResult<OrganizationRowDto>>
{
    public async Task<Result<PagedResult<OrganizationRowDto>>> Handle(ListOrganizationsQuery query, CancellationToken cancellationToken) =>
        await store.ListOrganizationsAsync(
            query.Paging,
            new OrganizationFilter(NullIfEmpty(query.Status), NullIfEmpty(query.PlanCode), NullIfEmpty(query.Source)),
            clock.GetUtcNow().UtcDateTime,
            cancellationToken).ConfigureAwait(false);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Organizasyon detayı: satır alanları + etkin limitler + istisna + askı + silme talebi. Bilinmeyen kimlik 404.</summary>
[PlatformAdminOnly]
public sealed record GetOrganizationQuery(Guid TenantId) : IQuery<OrganizationDetailDto>;

public sealed class GetOrganizationHandler(
    IPlatformReadStore store,
    ITenantAccountRepository accounts,
    IPlanRepository plans,
    TimeProvider clock) : IQueryHandler<GetOrganizationQuery, OrganizationDetailDto>
{
    public async Task<Result<OrganizationDetailDto>> Handle(GetOrganizationQuery query, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var row = await store.GetOrganizationRowAsync(query.TenantId, now.UtcDateTime, cancellationToken).ConfigureAwait(false);
        var account = await accounts.GetAsync(query.TenantId, cancellationToken).ConfigureAwait(false);
        if (row is null || account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var plan = await plans.GetAsync(account.PlanCode, cancellationToken).ConfigureAwait(false);
        var effective = plan is null
            ? new EffectiveLimits(null, new Dictionary<string, int?>(), GatedModules.All.ToDictionary(m => m, _ => false, StringComparer.Ordinal))
            : EntitlementMath.Effective(plan, account);
        var deletion = await store.GetLatestDeletionAsync(query.TenantId, cancellationToken).ConfigureAwait(false);

        JsonElement? overrides = null;
        if (!string.IsNullOrWhiteSpace(account.Overrides))
        {
            using var document = JsonDocument.Parse(account.Overrides);
            overrides = document.RootElement.Clone();
        }

        var suspension = account.Status == AccountStatuses.Suspended && account.SuspensionMode is not null
            ? new SuspensionDto(account.SuspensionMode, account.SuspendedReason, account.SuspendedAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null)
            : null;
        var planChangedAt = account.PlanChangedAt is { } changed ? new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc)) : (DateTimeOffset?)null;

        return new OrganizationDetailDto(
            row.TenantId,
            row.Name,
            row.Slug,
            row.PlanCode,
            row.PlanName,
            row.Source,
            row.Status,
            row.AccessLevel,
            row.TrialEndsOn,
            row.IsSystem,
            row.CreatedAt,
            row.Usage,
            new EffectiveLimitsDto(effective.MaxUsers, EntitlementMath.FiniteRecords(effective.MaxRecords), effective.Modules, effective.MaxStorageMb),
            overrides,
            planChangedAt,
            suspension,
            deletion);
    }
}
