using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Domain.Leads;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Kernel.Time;
using FluentValidation;

namespace Crm.Modules.Sales.Application.Reports;

/// <summary>Rapor tarih parametreleri: <c>from</c>/<c>to</c> (<c>YYYY-MM-DD</c>, uçlar dahil, kiracı saat diliminde takvim günü).</summary>
public interface IReportRange
{
    DateOnly? From { get; }

    DateOnly? To { get; }
}

public abstract class ReportRangeValidator<T> : AbstractValidator<T>
    where T : IReportRange
{
    private const int MaxSpanDays = 3660;
    public const string InvalidRange = "validation.date_range";

    protected ReportRangeValidator()
    {
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to >= query.From)
            .WithMessage(InvalidRange);
        RuleFor(x => x.To)
            .Must((query, to) => query.From is null || to is null || to.Value.DayNumber - query.From.Value.DayNumber <= MaxSpanDays)
            .WithMessage(InvalidRange);
    }
}

// ---- Huni ------------------------------------------------------------------------------------------------------------

/// <summary>Satış hunisi: huninin (verilmezse varsayılanın) her aşaması için güncel fırsat sayısı ve tutarı.</summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetFunnelReportQuery(Guid? PipelineId) : IQuery<FunnelDto>;

public sealed class GetFunnelReportHandler(ISalesReportStore store, DefaultPipelineResolver defaultPipeline) : IQueryHandler<GetFunnelReportQuery, FunnelDto>
{
    public async Task<Result<FunnelDto>> Handle(GetFunnelReportQuery query, CancellationToken cancellationToken)
    {
        var pipelineId = query.PipelineId ?? (await defaultPipeline.GetDefaultAsync(cancellationToken).ConfigureAwait(false))?.Id;
        if (pipelineId is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        return await store.GetFunnelAsync(pipelineId.Value, cancellationToken).ConfigureAwait(false) is { } funnel
            ? funnel
            : Error.NotFound(ErrorCodes.NotFound);
    }
}

// ---- Kazanılan / kaybedilen -----------------------------------------------------------------------------------------

/// <summary>
/// Kapanış tarihine (<c>closedAt</c>, kiracı saat diliminde) göre ay veya ISO hafta bazında kazanılan/kaybedilen fırsatlar.
/// Aralıktaki boş dönemler 0 ile doldurulur; ilk/son dönem aralık kenarında kısmi olabilir.
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetWonLostReportQuery(DateOnly? From, DateOnly? To, WonLostGroupBy GroupBy) : IQuery<IReadOnlyList<WonLostPeriodDto>>, IReportRange;

public sealed class GetWonLostReportValidator : ReportRangeValidator<GetWonLostReportQuery>;

public sealed class GetWonLostReportHandler(ISalesReportStore store, TenantCalendarService ranges)
    : IQueryHandler<GetWonLostReportQuery, IReadOnlyList<WonLostPeriodDto>>
{
    public async Task<Result<IReadOnlyList<WonLostPeriodDto>>> Handle(GetWonLostReportQuery query, CancellationToken cancellationToken)
    {
        var range = await ranges.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var rows = await store.GetClosedDealsByDayAsync(range.Value.FromUtc, range.Value.ToExclusiveUtc, range.Value.Calendar.IanaId, cancellationToken).ConfigureAwait(false);
        return Result.Success(WonLostPeriods.Build(rows, range.Value.From, range.Value.To, query.GroupBy));
    }
}

/// <summary>Günlük satırları ay/ISO hafta dönemlerine toplar ve aralıktaki boş dönemleri sıfırla doldurur (saf hesap).</summary>
public static class WonLostPeriods
{
    public static IReadOnlyList<WonLostPeriodDto> Build(IEnumerable<ClosedDealDayRow> rows, DateOnly from, DateOnly to, WonLostGroupBy groupBy)
    {
        var totals = new Dictionary<string, (int WonCount, decimal WonAmount, int LostCount, decimal LostAmount)>(StringComparer.Ordinal);
        foreach (var label in Labels(from, to, groupBy))
        {
            totals[label] = default;
        }

        foreach (var row in rows)
        {
            if (row.Day < from || row.Day > to || row.Kind == StageKind.Open)
            {
                continue;
            }

            var label = LabelOf(row.Day, groupBy);
            var current = totals.GetValueOrDefault(label);
            totals[label] = row.Kind == StageKind.Won
                ? (current.WonCount + row.Count, current.WonAmount + row.Amount, current.LostCount, current.LostAmount)
                : (current.WonCount, current.WonAmount, current.LostCount + row.Count, current.LostAmount + row.Amount);
        }

        return totals
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new WonLostPeriodDto(t.Key, t.Value.WonCount, t.Value.WonAmount, t.Value.LostCount, t.Value.LostAmount))
            .ToList();
    }

    /// <summary>Aralığı kapsayan dönem etiketleri (artan, tekrarsız).</summary>
    public static IEnumerable<string> Labels(DateOnly from, DateOnly to, WonLostGroupBy groupBy)
    {
        if (groupBy == WonLostGroupBy.Week)
        {
            for (var monday = TenantCalendar.StartOfWeek(from); monday <= to; monday = monday.AddDays(7))
            {
                yield return TenantCalendar.IsoWeekLabel(monday);
            }

            yield break;
        }

        for (var first = new DateOnly(from.Year, from.Month, 1); first <= to; first = first.AddMonths(1))
        {
            yield return TenantCalendar.MonthLabel(first);
        }
    }

    public static string LabelOf(DateOnly day, WonLostGroupBy groupBy) =>
        groupBy == WonLostGroupBy.Week ? TenantCalendar.IsoWeekLabel(day) : TenantCalendar.MonthLabel(day);
}

// ---- Potansiyel kaynakları -------------------------------------------------------------------------------------------

/// <summary>Oluşturulma tarihine göre potansiyel müşteriler: kaynak bazında toplam ve dönüşen sayısı.</summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetLeadsBySourceReportQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<LeadSourceReportRow>>, IReportRange;

public sealed class GetLeadsBySourceReportValidator : ReportRangeValidator<GetLeadsBySourceReportQuery>;

public sealed class GetLeadsBySourceReportHandler(ISalesReportStore store, TenantCalendarService ranges)
    : IQueryHandler<GetLeadsBySourceReportQuery, IReadOnlyList<LeadSourceReportRow>>
{
    public async Task<Result<IReadOnlyList<LeadSourceReportRow>>> Handle(GetLeadsBySourceReportQuery query, CancellationToken cancellationToken)
    {
        var range = await ranges.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        return Result.Success(await store.GetLeadsBySourceAsync(range.Value.FromUtc, range.Value.ToExclusiveUtc, cancellationToken).ConfigureAwait(false));
    }
}

// ---- Satış temsilcisi -------------------------------------------------------------------------------------------------

/// <summary>
/// Sahip (satış temsilcisi) bazında: açık fırsatlar (güncel durum), aralıkta kazanılanlar ve aralıkta oluşturulan potansiyeller.
/// Sıralama: kazanılan tutar azalan, sonra ad.
/// </summary>
[RequiresPermission(CrmPermissions.ReportsRead)]
public sealed record GetByOwnerReportQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<OwnerReportRow>>, IReportRange;

public sealed class GetByOwnerReportValidator : ReportRangeValidator<GetByOwnerReportQuery>;

public sealed class GetByOwnerReportHandler(ISalesReportStore store, TenantCalendarService ranges, IMemberLookup members)
    : IQueryHandler<GetByOwnerReportQuery, IReadOnlyList<OwnerReportRow>>
{
    public async Task<Result<IReadOnlyList<OwnerReportRow>>> Handle(GetByOwnerReportQuery query, CancellationToken cancellationToken)
    {
        var range = await ranges.ResolveReportRangeAsync(query.From, query.To, cancellationToken).ConfigureAwait(false);
        if (range.IsFailure)
        {
            return range.Error;
        }

        var totals = await store.GetOwnerTotalsAsync(range.Value.FromUtc, range.Value.ToExclusiveUtc, cancellationToken).ConfigureAwait(false);
        var names = totals.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.GetDisplayNamesAsync(totals.Select(t => t.OwnerUserId).ToList(), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<OwnerReportRow> rows = totals
            .Select(t => new OwnerReportRow(t.OwnerUserId, names.GetValueOrDefault(t.OwnerUserId), t.OpenDealCount, t.OpenDealAmount, t.WonCount, t.WonAmount, t.LeadCount))
            .OrderByDescending(r => r.WonAmount)
            .ThenBy(r => r.OwnerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.OwnerUserId)
            .ToList();
        return Result.Success(rows);
    }
}
