using Crm.Modules.Service.Contracts;
using Crm.Modules.Service.Domain;
using Crm.Modules.Service.Domain.Cases;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Service.Application.Cases;

/// <summary>Liste süzgeci ayrıştırma: <c>status</c>/<c>priority</c> virgülle çoklu, <c>slaState</c> tek değer; bilinmeyen değer geçersizdir.</summary>
public static class CaseFilterParser
{
    private const char ListSeparator = ',';

    public static bool TryParseList<T>(string? text, out IReadOnlyList<T> values)
        where T : struct, Enum
    {
        var parsed = new List<T>();
        values = parsed;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var token in text.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!EnumText.TryParse<T>(token, out var value))
            {
                return false;
            }

            if (!parsed.Contains(value))
            {
                parsed.Add(value);
            }
        }

        return true;
    }

    public static bool TryParseSingle<T>(string? text, out T? value)
        where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!EnumText.TryParse<T>(text, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static CaseFilter Build(ListCasesQuery query)
    {
        TryParseList<CaseStatus>(query.Status, out var statuses);
        TryParseList<CasePriority>(query.Priority, out var priorities);
        TryParseSingle<SlaState>(query.SlaState, out var sla);
        return new CaseFilter(statuses, priorities, query.Channel, query.AssignedUserId, query.Unassigned is true, query.AccountId, query.ContactId, sla);
    }
}

/// <summary>
/// Talep listesi. Filtreler: <c>q</c> (numara + konu), <c>status</c>/<c>priority</c> (virgülle çoklu), <c>channel</c>, <c>assignedUserId</c>,
/// <c>unassigned=true</c> (<c>assignedUserId</c> ile birlikte verilemez), <c>accountId</c>, <c>contactId</c>, <c>slaState</c>
/// (<c>ok|atRisk|breached</c>, tek değer; okuma anında hesaplanır). Bilinmeyen enum değeri → <c>validation</c>.
/// </summary>
[RequiresPermission(ServicePermissions.CasesRead)]
public sealed record ListCasesQuery(
    PagedQuery Paging,
    string? Status,
    string? Priority,
    CaseChannel? Channel,
    Guid? AssignedUserId,
    bool? Unassigned,
    Guid? AccountId,
    Guid? ContactId,
    string? SlaState) : IQuery<PagedResult<CaseListItemDto>>;

public sealed class ListCasesValidator : AbstractValidator<ListCasesQuery>
{
    public ListCasesValidator()
    {
        RuleFor(x => x.Status).Must(s => CaseFilterParser.TryParseList<CaseStatus>(s, out _)).WithMessage(ServiceErrors.InvalidFilterValue);
        RuleFor(x => x.Priority).Must(p => CaseFilterParser.TryParseList<CasePriority>(p, out _)).WithMessage(ServiceErrors.InvalidFilterValue);
        RuleFor(x => x.SlaState).Must(s => CaseFilterParser.TryParseSingle<SlaState>(s, out _)).WithMessage(ServiceErrors.InvalidFilterValue);
        RuleFor(x => x.Unassigned)
            .Must((query, unassigned) => unassigned is not true || query.AssignedUserId is null)
            .WithMessage(ServiceErrors.UnassignedConflict);
    }
}

public sealed class ListCasesHandler(ICaseReadStore store, TimeProvider clock) : IQueryHandler<ListCasesQuery, PagedResult<CaseListItemDto>>
{
    public async Task<Result<PagedResult<CaseListItemDto>>> Handle(ListCasesQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, CaseFilterParser.Build(query), clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(ServicePermissions.CasesRead)]
public sealed record GetCaseQuery(Guid Id) : IQuery<CaseDetailDto>;

public sealed class GetCaseHandler(ICaseReadStore store, TimeProvider clock) : IQueryHandler<GetCaseQuery, CaseDetailDto>
{
    public async Task<Result<CaseDetailDto>> Handle(GetCaseQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false) is { } detail
            ? detail
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>
/// Ana sayfa sayaçları (aktif = <c>new|open|pending</c>): <c>openCount</c>, <c>overdueCount</c> (aktif ve SLA ihlalli; liste süzgeci
/// <c>status=new,open,pending&amp;slaState=breached</c> karşılığı), <c>mineCount</c> (çağırana atanmış), <c>unassignedCount</c>.
/// </summary>
[RequiresPermission(ServicePermissions.CasesRead)]
public sealed record GetCaseSummaryQuery : IQuery<CaseSummaryDto>;

public sealed class GetCaseSummaryHandler(ICaseReadStore store, ICurrentUser user, TimeProvider clock) : IQueryHandler<GetCaseSummaryQuery, CaseSummaryDto>
{
    public async Task<Result<CaseSummaryDto>> Handle(GetCaseSummaryQuery query, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        return await store.GetSummaryAsync(userId, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Yorumlar + olaylar birleşik zaman çizelgesi, en yeni önce (<c>createdAt desc, id desc</c>), sayfalı.</summary>
[RequiresPermission(ServicePermissions.CasesRead)]
public sealed record GetCaseTimelineQuery(Guid Id, PagedQuery Paging) : IQuery<PagedResult<TimelineItemDto>>;

public sealed class GetCaseTimelineHandler(ICaseReadStore store) : IQueryHandler<GetCaseTimelineQuery, PagedResult<TimelineItemDto>>
{
    public async Task<Result<PagedResult<TimelineItemDto>>> Handle(GetCaseTimelineQuery query, CancellationToken cancellationToken) =>
        await store.GetTimelineAsync(query.Id, query.Paging, cancellationToken).ConfigureAwait(false) is { } page
            ? page
            : Error.NotFound(ErrorCodes.NotFound);
}
