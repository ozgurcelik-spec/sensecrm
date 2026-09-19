using Crm.Modules.Commerce.Domain.Orders;
using Crm.Modules.Commerce.Domain.Quotes;
using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Commerce.Application.Reports;

/// <summary>
/// Ticaret özeti (<c>crm.reports.read</c>). <c>from</c>/<c>to</c> M3 kurallarıyla aynı (<c>YYYY-MM-DD</c>, uçlar dahil, kiracı saatinde
/// takvim günü, varsayılan son 12 ay, ters/10 yılı aşan aralık <c>validation</c>). Teklifler <c>createdAt</c> (yerel gün), siparişler
/// <c>orderDate</c> aralığına göre; silinmişler yok. <c>byStatus</c> her zaman tüm durumları sabit sırayla döner; teklif durumları
/// <b>etkin</b> durumdur (<c>expired</c> bugünün kiracı tarihine göre). <c>orders.totalAmount/totalCount</c> iptaller hariçtir.
/// <c>conversionRate</c> = accepted / taslak olmayan teklifler (payda 0 → alan yok).
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetCommerceSummaryQuery(DateOnly? From, DateOnly? To) : IQuery<CommerceSummaryDto>;

public sealed class GetCommerceSummaryValidator : AbstractValidator<GetCommerceSummaryQuery>
{
    private const int MaxSpanDays = 3660;
    public const string InvalidRange = "validation.date_range";

    public GetCommerceSummaryValidator()
    {
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to >= query.From)
            .WithMessage(InvalidRange);
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to.Value.DayNumber - query.From.Value.DayNumber <= MaxSpanDays)
            .WithMessage(InvalidRange);
    }
}

public sealed class GetCommerceSummaryHandler(ICommerceReportStore store, TenantCalendarService ranges)
    : IQueryHandler<GetCommerceSummaryQuery, CommerceSummaryDto>
{
    private const int RateDigits = 4;

    private static readonly QuoteStatus[] QuoteOrder =
        [QuoteStatus.Draft, QuoteStatus.Sent, QuoteStatus.Accepted, QuoteStatus.Rejected, QuoteStatus.Expired];

    private static readonly SalesOrderStatus[] OrderOrder =
        [SalesOrderStatus.Draft, SalesOrderStatus.Confirmed, SalesOrderStatus.Fulfilled, SalesOrderStatus.Cancelled];

    public async Task<Result<CommerceSummaryDto>> Handle(GetCommerceSummaryQuery query, CancellationToken cancellationToken)
    {
        var range = await ranges.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var resolved = range.Value;
        var today = resolved.Calendar.Today(ranges.UtcNow);
        var quoteTotals = await store.GetQuoteTotalsAsync(resolved.FromUtc, resolved.ToExclusiveUtc, today, cancellationToken).ConfigureAwait(false);
        var orderTotals = await store.GetOrderTotalsAsync(resolved.From, resolved.To, cancellationToken).ConfigureAwait(false);
        var currencies = await store.GetCurrenciesAsync(resolved.FromUtc, resolved.ToExclusiveUtc, resolved.From, resolved.To, cancellationToken).ConfigureAwait(false);

        var quoteRows = QuoteOrder.Select(s => Fill(s, quoteTotals)).ToList();
        var orderRows = OrderOrder.Select(s => Fill(s, orderTotals)).ToList();
        var activeOrders = orderRows.Where(r => r.Status != SalesOrderStatus.Cancelled).ToList();

        var accepted = quoteRows.Single(r => r.Status == QuoteStatus.Accepted).Count;
        var submitted = quoteRows.Where(r => r.Status != QuoteStatus.Draft).Sum(r => r.Count);
        decimal? rate = submitted == 0 ? null : decimal.Round(accepted / (decimal)submitted, RateDigits, MidpointRounding.AwayFromZero);

        return new CommerceSummaryDto(
            currencies,
            new QuoteReportDto(quoteRows.Sum(r => r.Count), quoteRows.Sum(r => r.Amount), quoteRows),
            new OrderReportDto(activeOrders.Sum(r => r.Count), activeOrders.Sum(r => r.Amount), orderRows),
            rate);
    }

    private static StatusTotalDto<TStatus> Fill<TStatus>(TStatus status, IReadOnlyList<StatusTotal<TStatus>> totals)
        where TStatus : struct, Enum
    {
        var row = totals.FirstOrDefault(t => EqualityComparer<TStatus>.Default.Equals(t.Status, status));
        return new StatusTotalDto<TStatus>(status, row?.Count ?? 0, row?.Amount ?? 0m);
    }
}
