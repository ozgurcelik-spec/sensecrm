using Crm.Modules.Activities.Domain;
using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Activities.Application.Reports;

/// <summary>
/// Kullanıcı bazında aktivite raporu (<c>crm.reports.read</c>): aralıkta tamamlananlar (<c>completedAt</c>), son tarihi aralıkta olan
/// açıklar (<c>dueAt</c>) ve bunların gecikenleri. Notlar sayılmaz. <c>from</c>/<c>to</c> kiracı saat diliminde takvim günüdür,
/// uçlar dahil; verilmezse son 12 ay. Sıralama: tamamlanan azalan, sonra ad.
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetActivitiesByUserReportQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<ActivityUserReportRow>>;

public sealed class GetActivitiesByUserReportValidator : AbstractValidator<GetActivitiesByUserReportQuery>
{
    private const int MaxSpanDays = 3660;
    public const string InvalidRange = "validation.date_range";

    public GetActivitiesByUserReportValidator()
    {
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to >= query.From)
            .WithMessage(InvalidRange);
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to.Value.DayNumber - query.From.Value.DayNumber <= MaxSpanDays)
            .WithMessage(InvalidRange);
    }
}

public sealed class GetActivitiesByUserReportHandler(IActivityReadStore store, TenantCalendarService calendars, IMemberLookup members)
    : IQueryHandler<GetActivitiesByUserReportQuery, IReadOnlyList<ActivityUserReportRow>>
{
    public async Task<Result<IReadOnlyList<ActivityUserReportRow>>> Handle(GetActivitiesByUserReportQuery query, CancellationToken cancellationToken)
    {
        var range = await calendars.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var totals = await store.GetUserTotalsAsync(range.Value.FromUtc, range.Value.ToExclusiveUtc, calendars.UtcNow.UtcDateTime, cancellationToken).ConfigureAwait(false);
        var names = totals.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.GetDisplayNamesAsync(totals.Select(t => t.UserId).ToList(), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ActivityUserReportRow> rows = totals
            .Select(t => new ActivityUserReportRow(t.UserId, names.GetValueOrDefault(t.UserId), t.CompletedCount, t.OpenCount, t.OverdueCount))
            .OrderByDescending(r => r.CompletedCount)
            .ThenBy(r => r.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.UserId)
            .ToList();
        return Result.Success(rows);
    }
}
