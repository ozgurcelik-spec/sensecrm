using FluentValidation;
using FluentValidation.Results;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Platform.Application.Console;

// ---------------------------------------------------------------------------------------------------------------------
// Kullanım: seri, canlı yenileme, finans CSV dışa aktarma.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Kullanım aralığı çözümleme yardımcıları (UTC günleri, uçlar dahil).</summary>
internal static class UsageRange
{
    public static (DateOnly From, DateOnly To) Resolve(DateOnly? from, DateOnly? to, DateOnly today, int defaultDays)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(1 - defaultDays);
        return (start, end);
    }

    public static bool IsValid(DateOnly from, DateOnly to) => to >= from && to.DayNumber - from.DayNumber + 1 <= PlatformLimits.MaxUsageRangeDays;
}

/// <summary><c>GET …/usage?from&amp;to</c>: UTC günleri (dahil), varsayılan son 90 gün, aralık ≤ 400 gün; gün artan; anlık görüntüsü olmayan gün yok.</summary>
[PlatformAdminOnly]
public sealed record GetUsageQuery(Guid TenantId, DateOnly? From, DateOnly? To) : IQuery<UsageSeriesDto>;

public sealed class GetUsageHandler(IPlatformReadStore store, ITenantAccountRepository accounts, TimeProvider clock) : IQueryHandler<GetUsageQuery, UsageSeriesDto>
{
    private const int DefaultDays = 90;

    public async Task<Result<UsageSeriesDto>> Handle(GetUsageQuery query, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var (from, to) = UsageRange.Resolve(query.From, query.To, today, DefaultDays);
        if (!UsageRange.IsValid(from, to))
        {
            throw new ValidationException([new ValidationFailure("From", PlatformErrors.InvalidRange)]);
        }

        if (await accounts.GetAsync(query.TenantId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        return new UsageSeriesDto(await store.ListUsageAsync(query.TenantId, from, to, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// <c>POST …/usage/refresh</c>: kiracı kapsamında <b>canlı</b> sayım yapar ve bugünün (UTC) anlık görüntüsünü yazar. Yalnız
/// <c>active | suspended</c> (etkin durum <c>trial*</c> dahil); <c>pending_deletion|deleted</c> için 409.
/// </summary>
[PlatformAdminOnly]
public sealed record RefreshUsageCommand(Guid TenantId) : ICommand<UsageSeriesDto>;

public sealed class RefreshUsageHandler(
    ITenantAccountRepository accounts,
    IUsageMeter meter,
    IUsageSnapshotWriter writer,
    IPlatformAudit audit,
    TimeProvider clock) : ICommandHandler<RefreshUsageCommand, UsageSeriesDto>
{
    public async Task<Result<UsageSeriesDto>> Handle(RefreshUsageCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (account.Status is not (AccountStatuses.Active or AccountStatuses.Suspended))
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", account.Status), ("to", account.Status));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var usage = await meter.CollectAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        await writer.UpsertAsync(account.TenantId, today, usage, now, cancellationToken).ConfigureAwait(false);
        audit.Record(
            PlatformAuditActions.UsageRefreshed,
            account,
            null,
            new Dictionary<string, object?> { ["day"] = today.ToString("yyyy-MM-dd"), ["usersActive"] = usage.UsersActive, ["usersPending"] = usage.UsersPending });

        return new UsageSeriesDto([new UsageDayDto(today, usage.UsersActive, usage.UsersPending, usage.Metrics)]);
    }
}

/// <summary>
/// <c>GET /platform/usage/export?from&amp;to</c> (CSV): önce bu komut aralığı doğrular (verilmezse <b>önceki takvim ayı</b>, ≤ 400 gün), satır sayısını
/// hesaplar ve <c>usage.exported</c> denetimini yazar; gövde controller'da <see cref="IUsageExportWriter"/> ile akış halinde yazılır.
/// </summary>
[PlatformAdminOnly]
public sealed record ExportUsageCommand(DateOnly? From, DateOnly? To) : ICommand<UsageExportInfoDto>;

public sealed class ExportUsageHandler(IPlatformReadStore store, IPlatformAudit audit, TimeProvider clock) : ICommandHandler<ExportUsageCommand, UsageExportInfoDto>
{
    public async Task<Result<UsageExportInfoDto>> Handle(ExportUsageCommand command, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        DateOnly from;
        DateOnly to;
        if (command.From is null && command.To is null)
        {
            var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
            from = firstOfThisMonth.AddMonths(-1);
            to = firstOfThisMonth.AddDays(-1);
        }
        else
        {
            to = command.To ?? today;
            from = command.From ?? to.AddDays(-29);
        }

        if (!UsageRange.IsValid(from, to))
        {
            throw new ValidationException([new ValidationFailure("From", PlatformErrors.InvalidRange)]);
        }

        var rows = await store.CountUsageRowsAsync(from, to, cancellationToken).ConfigureAwait(false);
        audit.Record(
            PlatformAuditActions.UsageExported,
            null,
            null,
            new Dictionary<string, object?> { ["from"] = from.ToString("yyyy-MM-dd"), ["to"] = to.ToString("yyyy-MM-dd"), ["rows"] = rows });
        return new UsageExportInfoDto(from, to, rows);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Katalog ve denetim.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>GET /platform/plans</c>: salt okunur katalog (<c>sortOrder</c>; <c>assignedCount</c> silinmemiş hesap sayısı). Yazma ucu yoktur.</summary>
[PlatformAdminOnly]
public sealed record ListPlansQuery : IQuery<IReadOnlyList<PlanDto>>;

public sealed class ListPlansHandler(IPlatformReadStore store) : IQueryHandler<ListPlansQuery, IReadOnlyList<PlanDto>>
{
    public async Task<Result<IReadOnlyList<PlanDto>>> Handle(ListPlansQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.ListPlansAsync(cancellationToken).ConfigureAwait(false));
}

/// <summary><c>GET /platform/audit</c>: filtreli sayfalı liste (en yeni önce; <c>from/to</c> UTC günü, dahil).</summary>
[PlatformAdminOnly]
public sealed record ListPlatformAuditQuery(PagedQuery Paging, Guid? TenantId, string? Action, Guid? ActorUserId, DateOnly? From, DateOnly? To) : IQuery<PagedResult<PlatformAuditDto>>;

public sealed class ListPlatformAuditValidator : AbstractValidator<ListPlatformAuditQuery>
{
    public ListPlatformAuditValidator() =>
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From!.Value).When(x => x.From is not null && x.To is not null).WithMessage(PlatformErrors.InvalidRange);
}

public sealed class ListPlatformAuditHandler(IPlatformReadStore store) : IQueryHandler<ListPlatformAuditQuery, PagedResult<PlatformAuditDto>>
{
    public async Task<Result<PagedResult<PlatformAuditDto>>> Handle(ListPlatformAuditQuery query, CancellationToken cancellationToken) =>
        await store.ListAuditAsync(
            query.Paging,
            new AuditFilter(query.TenantId, string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim(), query.ActorUserId, query.From, query.To),
            cancellationToken).ConfigureAwait(false);
}
