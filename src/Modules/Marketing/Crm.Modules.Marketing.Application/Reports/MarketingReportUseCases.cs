using Crm.Modules.Identity.Contracts;
using Crm.Modules.Marketing.Domain;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Marketing.Application.Reports;

/// <summary>
/// Pazarlama özeti (<c>crm.reports.read</c>): kampanya sayısı, durum/tür dağılımı, toplamlar (oranlar toplam sayılar üzerinden) ve en iyi
/// 10 kampanya. <c>from</c>/<c>to</c> kiracı saat diliminde takvim günüdür, uçlar dahil; verilmezse son 12 ay. Kampanya aralığa
/// <c>startDate</c> (yoksa <c>createdAt</c>) ile girer.
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetMarketingSummaryQuery(DateOnly? From, DateOnly? To) : IQuery<MarketingSummaryDto>;

public sealed class GetMarketingSummaryValidator : AbstractValidator<GetMarketingSummaryQuery>
{
    private const int MaxSpanDays = 3660;

    public GetMarketingSummaryValidator()
    {
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to >= query.From)
            .WithMessage(MarketingErrors.InvalidReportRange);
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to.Value.DayNumber - query.From.Value.DayNumber <= MaxSpanDays)
            .WithMessage(MarketingErrors.InvalidReportRange);
    }
}

public sealed class GetMarketingSummaryHandler(IMarketingReportStore store, TenantCalendarService calendars)
    : IQueryHandler<GetMarketingSummaryQuery, MarketingSummaryDto>
{
    public async Task<Result<MarketingSummaryDto>> Handle(GetMarketingSummaryQuery query, CancellationToken cancellationToken)
    {
        var range = await calendars.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        return await store.GetSummaryAsync(range.Value.From, range.Value.To, range.Value.FromUtc, range.Value.ToExclusiveUtc, cancellationToken).ConfigureAwait(false);
    }
}
