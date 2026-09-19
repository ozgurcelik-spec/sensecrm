using FluentValidation;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Service.Domain.Cases;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Service.Application.Reports;

/// <summary>Rapor tarih aralığı doğrulaması (M3 ile aynı): ters aralık ve 10 yılı aşan aralık <c>validation</c>.</summary>
public abstract class ServiceReportRangeValidator<T> : AbstractValidator<T>
    where T : IServiceReportRange
{
    private const int MaxSpanDays = 3660;
    public const string InvalidRange = "validation.date_range";

    protected ServiceReportRangeValidator()
    {
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to >= query.From)
            .WithMessage(InvalidRange);
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to.Value.DayNumber - query.From.Value.DayNumber <= MaxSpanDays)
            .WithMessage(InvalidRange);
    }
}

public interface IServiceReportRange
{
    DateOnly? From { get; }

    DateOnly? To { get; }
}

/// <summary>
/// Servis özeti (<c>crm.reports.read</c>). Kohort: <c>createdAt</c> aralıkta olan, silinmemiş talepler; <c>from</c>/<c>to</c> kiracı saat
/// diliminde takvim günü (uçlar dahil), varsayılan son 12 ay. "İhlal", rapor anındaki <c>isSlaBreached</c> tanımıdır.
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetServiceSummaryReportQuery(DateOnly? From, DateOnly? To) : IQuery<ServiceSummaryReportDto>, IServiceReportRange;

public sealed class GetServiceSummaryReportValidator : ServiceReportRangeValidator<GetServiceSummaryReportQuery>;

public sealed class GetServiceSummaryReportHandler(ICaseReadStore store, TenantCalendarService calendars)
    : IQueryHandler<GetServiceSummaryReportQuery, ServiceSummaryReportDto>
{
    private const int AverageDecimals = 1;
    private const int RateDecimals = 4;

    public async Task<Result<ServiceSummaryReportDto>> Handle(GetServiceSummaryReportQuery query, CancellationToken cancellationToken)
    {
        var range = await calendars.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var totals = await store.GetSummaryTotalsAsync(range.Value.FromUtc, range.Value.ToExclusiveUtc, calendars.UtcNow.UtcDateTime, cancellationToken).ConfigureAwait(false);

        var byStatus = Enum.GetValues<CaseStatus>().Select(s => new StatusCount(s, totals.ByStatus.GetValueOrDefault(s))).ToList();
        var byPriority = Enum.GetValues<CasePriority>().Select(p => new PriorityCount(p, totals.ByPriority.GetValueOrDefault(p))).ToList();
        var rate = totals.TotalCount == 0 ? 0d : Math.Round((double)totals.SlaBreachedCount / totals.TotalCount, RateDecimals);

        return new ServiceSummaryReportDto(
            range.Value.From,
            range.Value.To,
            totals.TotalCount,
            totals.ResolvedCount,
            byStatus,
            byPriority,
            RoundOrNull(totals.AvgFirstResponseMinutes),
            RoundOrNull(totals.AvgResolutionMinutes),
            totals.SlaBreachedCount,
            rate);

        static double? RoundOrNull(double? value) => value is { } v ? Math.Round(v, AverageDecimals) : null;
    }
}

/// <summary>
/// Temsilci bazlı servis raporu (<c>crm.reports.read</c>): aynı kohort ve tanımlar; <c>openCount</c> aktif (güncel); atanmamış talepler
/// tek satırdır. Sıra: <c>totalCount</c> azalan, sonra ad. Adlar pasif üyeler dahil çözülür.
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetServiceByAssigneeReportQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<AssigneeReportRowDto>>, IServiceReportRange;

public sealed class GetServiceByAssigneeReportValidator : ServiceReportRangeValidator<GetServiceByAssigneeReportQuery>;

public sealed class GetServiceByAssigneeReportHandler(ICaseReadStore store, TenantCalendarService calendars, IMemberLookup members)
    : IQueryHandler<GetServiceByAssigneeReportQuery, IReadOnlyList<AssigneeReportRowDto>>
{
    private const int AverageDecimals = 1;

    public async Task<Result<IReadOnlyList<AssigneeReportRowDto>>> Handle(GetServiceByAssigneeReportQuery query, CancellationToken cancellationToken)
    {
        var range = await calendars.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var totals = await store.GetAssigneeTotalsAsync(range.Value.FromUtc, range.Value.ToExclusiveUtc, calendars.UtcNow.UtcDateTime, cancellationToken).ConfigureAwait(false);
        var userIds = totals.Where(t => t.AssignedUserId is not null).Select(t => t.AssignedUserId!.Value).ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.GetDisplayNamesAsync(userIds, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<AssigneeReportRowDto> rows = totals
            .Select(t => new AssigneeReportRowDto(
                t.AssignedUserId,
                t.AssignedUserId is { } id ? names.GetValueOrDefault(id) : null,
                t.TotalCount,
                t.OpenCount,
                t.ResolvedCount,
                t.AvgFirstResponseMinutes is { } first ? Math.Round(first, AverageDecimals) : null,
                t.AvgResolutionMinutes is { } resolution ? Math.Round(resolution, AverageDecimals) : null,
                t.SlaBreachedCount))
            .OrderByDescending(r => r.TotalCount)
            .ThenBy(r => r.AssignedUserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.AssignedUserId)
            .ToList();
        return Result.Success(rows);
    }
}
