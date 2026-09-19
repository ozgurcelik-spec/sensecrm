using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Application;
using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Domain.Accounts;
using Crm.Modules.Sales.Domain.Contacts;
using Crm.Modules.Sales.Domain.Deals;
using Crm.Modules.Sales.Domain.Leads;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Infrastructure.Querying;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary>
/// Sorgu projeksiyonları (kiracı + yumuşak silme filtresi altında). Arama <c>ILIKE</c> + kaçışlı parametre; sıralama yalnız
/// <see cref="GridFieldMap{T}"/> beyaz listesindeki alanlarda (bilinmeyen alan yok sayılır) ve her zaman <c>Id</c> ile kararlı.
/// İlişkili adlar (firma, kişi, aşama, sahip) sayfa başına toplu tek sorguyla çözülür.
/// </summary>
public sealed class SalesReadStore(SalesDbContext db, IMemberLookup members) : ISalesReadStore
{
    private const int RelatedListLimit = 200;
    private const int BoardDealLimit = 100;

    private static readonly GridFieldMap<Account> AccountFields = GridFieldMap<Account>.Create()
        .Field("name", a => a.Name)
        .Field("industry", a => a.Industry)
        .Field("createdAt", a => a.CreatedAt)
        .Field("updatedAt", a => a.ModifiedDate);

    private static readonly GridFieldMap<Contact> ContactFields = GridFieldMap<Contact>.Create()
        .Field("lastName", c => c.LastName)
        .Field("firstName", c => c.FirstName)
        .Field("email", c => c.Email)
        .Field("createdAt", c => c.CreatedAt)
        .Field("updatedAt", c => c.ModifiedDate);

    private static readonly GridFieldMap<Lead> LeadFields = GridFieldMap<Lead>.Create()
        .Field("lastName", l => l.LastName)
        .Field("company", l => l.Company)
        .Field("status", l => l.Status)
        .Field("source", l => l.Source)
        .Field("rating", l => l.Rating)
        .Field("createdAt", l => l.CreatedAt)
        .Field("updatedAt", l => l.ModifiedDate);

    private static readonly GridFieldMap<Deal> DealFields = GridFieldMap<Deal>.Create()
        .Field("name", d => d.Name)
        .Field("amount", d => d.Amount)
        .Field("closingDate", d => d.ClosingDate)
        .Field("closedAt", d => d.ClosedAt)
        .Field("createdAt", d => d.CreatedAt)
        .Field("updatedAt", d => d.ModifiedDate);

    // ---- Firmalar ---------------------------------------------------------------------------------------------------

    public async Task<PagedResult<AccountDto>> ListAccountsAsync(PagedQuery paging, Guid? ownerUserId, string? industry, CancellationToken ct)
    {
        var query = db.Accounts.AsNoTracking();
        if (ownerUserId is { } owner)
        {
            query = query.Where(a => a.OwnerUserId == owner);
        }

        if (!string.IsNullOrWhiteSpace(industry))
        {
            var pattern = SearchPattern.Contains(industry)!;
            query = query.Where(a => a.Industry != null && EF.Functions.ILike(a.Industry, pattern, SearchPattern.Escape));
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(a => EF.Functions.ILike(a.Name, q, SearchPattern.Escape)
                || (a.Email != null && EF.Functions.ILike(a.Email, q, SearchPattern.Escape)));
        }

        var (rows, total) = await PageAsync(query, paging, AccountFields, a => a.CreatedAt, a => a.Id, ct);
        var owners = await OwnerNamesAsync(rows.Select(a => a.OwnerUserId), ct);
        return new PagedResult<AccountDto>(rows.Select(a => ToDto(a, owners)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return null;
        }

        var owners = await OwnerNamesAsync([account.OwnerUserId], ct);
        var contactCount = await db.Contacts.CountAsync(c => c.AccountId == id, ct);
        var dealCount = await db.Deals.CountAsync(d => d.AccountId == id, ct);
        return ToDto(account, owners) with { ContactCount = contactCount, DealCount = dealCount };
    }

    public async Task<IReadOnlyList<ContactDto>> ListAccountContactsAsync(Guid accountId, CancellationToken ct)
    {
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.AccountId == accountId)
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ThenBy(c => c.Id)
            .Take(RelatedListLimit)
            .ToListAsync(ct);
        return await MapContactsAsync(contacts, ct);
    }

    public async Task<IReadOnlyList<DealDto>> ListAccountDealsAsync(Guid accountId, CancellationToken ct)
    {
        var deals = await db.Deals.AsNoTracking()
            .Where(d => d.AccountId == accountId)
            .OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id)
            .Take(RelatedListLimit)
            .ToListAsync(ct);
        return await MapDealsAsync(deals, ct);
    }

    // ---- Kişiler ----------------------------------------------------------------------------------------------------

    public async Task<PagedResult<ContactDto>> ListContactsAsync(PagedQuery paging, Guid? accountId, Guid? ownerUserId, CancellationToken ct)
    {
        var query = db.Contacts.AsNoTracking();
        if (accountId is { } account)
        {
            query = query.Where(c => c.AccountId == account);
        }

        if (ownerUserId is { } owner)
        {
            query = query.Where(c => c.OwnerUserId == owner);
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(c => EF.Functions.ILike((c.FirstName ?? string.Empty) + " " + c.LastName, q, SearchPattern.Escape)
                || (c.Email != null && EF.Functions.ILike(c.Email, q, SearchPattern.Escape)));
        }

        var (rows, total) = await PageAsync(query, paging, ContactFields, c => c.CreatedAt, c => c.Id, ct);
        return new PagedResult<ContactDto>(await MapContactsAsync(rows, ct), paging.Page, paging.PageSize, total);
    }

    public async Task<ContactDto?> GetContactAsync(Guid id, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return contact is null ? null : (await MapContactsAsync([contact], ct))[0];
    }

    // ---- Potansiyel müşteriler --------------------------------------------------------------------------------------

    public async Task<PagedResult<LeadDto>> ListLeadsAsync(PagedQuery paging, LeadStatus? status, LeadSource? source, Guid? ownerUserId, CancellationToken ct)
    {
        var query = db.Leads.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(l => l.Status == s);
        }

        if (source is { } src)
        {
            query = query.Where(l => l.Source == src);
        }

        if (ownerUserId is { } owner)
        {
            query = query.Where(l => l.OwnerUserId == owner);
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(l => EF.Functions.ILike((l.FirstName ?? string.Empty) + " " + l.LastName, q, SearchPattern.Escape)
                || EF.Functions.ILike(l.Company, q, SearchPattern.Escape)
                || (l.Email != null && EF.Functions.ILike(l.Email, q, SearchPattern.Escape)));
        }

        var (rows, total) = await PageAsync(query, paging, LeadFields, l => l.CreatedAt, l => l.Id, ct);
        var owners = await OwnerNamesAsync(rows.Select(l => l.OwnerUserId), ct);
        return new PagedResult<LeadDto>(rows.Select(l => ToDto(l, owners)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<LeadDto?> GetLeadAsync(Guid id, CancellationToken ct)
    {
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return null;
        }

        return ToDto(lead, await OwnerNamesAsync([lead.OwnerUserId], ct));
    }

    // ---- Huniler ----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PipelineDto>> ListPipelinesAsync(CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking().Include(p => p.Stages)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(ct);
        return pipelines.Select(ToDto).ToList();
    }

    public async Task<PipelineDto?> GetPipelineAsync(Guid id, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().Include(p => p.Stages).FirstOrDefaultAsync(p => p.Id == id, ct);
        return pipeline is null ? null : ToDto(pipeline);
    }

    // ---- Fırsatlar --------------------------------------------------------------------------------------------------

    public async Task<PagedResult<DealDto>> ListDealsAsync(PagedQuery paging, DealFilter filter, CancellationToken ct)
    {
        var query = db.Deals.AsNoTracking();
        if (filter.PipelineId is { } pipelineId)
        {
            query = query.Where(d => d.PipelineId == pipelineId);
        }

        if (filter.StageId is { } stageId)
        {
            query = query.Where(d => d.StageId == stageId);
        }

        if (filter.StageKind is { } kind)
        {
            query = query.Where(d => db.PipelineStages.Any(s => s.Id == d.StageId && s.Kind == kind));
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(d => d.OwnerUserId == owner);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(d => d.AccountId == accountId);
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(d => EF.Functions.ILike(d.Name, q, SearchPattern.Escape)
                || db.Accounts.Any(a => a.Id == d.AccountId && EF.Functions.ILike(a.Name, q, SearchPattern.Escape)));
        }

        var (rows, total) = await PageAsync(query, paging, DealFields, d => d.CreatedAt, d => d.Id, ct);
        return new PagedResult<DealDto>(await MapDealsAsync(rows, ct), paging.Page, paging.PageSize, total);
    }

    public async Task<DealDto?> GetDealAsync(Guid id, CancellationToken ct)
    {
        var deal = await db.Deals.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return deal is null ? null : (await MapDealsAsync([deal], ct))[0];
    }

    public async Task<DealBoardDto?> GetBoardAsync(Guid pipelineId, Guid? ownerUserId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().Include(p => p.Stages).FirstOrDefaultAsync(p => p.Id == pipelineId, ct);
        if (pipeline is null)
        {
            return null;
        }

        var deals = db.Deals.AsNoTracking().Where(d => d.PipelineId == pipelineId);
        if (ownerUserId is { } owner)
        {
            deals = deals.Where(d => d.OwnerUserId == owner);
        }

        var totals = (await deals.GroupBy(d => d.StageId)
                .Select(g => new { StageId = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount ?? 0m) })
                .ToListAsync(ct))
            .ToDictionary(t => t.StageId);

        var stages = pipeline.Stages.OrderBy(s => s.Order).ToList();
        var cards = new Dictionary<Guid, List<Deal>>();
        foreach (var stage in stages)
        {
            var stageId = stage.Id;
            cards[stageId] = await deals.Where(d => d.StageId == stageId)
                .OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id)
                .Take(BoardDealLimit)
                .ToListAsync(ct);
        }

        var allDeals = cards.Values.SelectMany(c => c).ToList();
        var accountNames = await AccountNamesAsync(allDeals.Select(d => d.AccountId), ct);
        var owners = await OwnerNamesAsync(allDeals.Select(d => d.OwnerUserId), ct);

        var boardStages = stages.Select(stage =>
        {
            totals.TryGetValue(stage.Id, out var total);
            var summaries = cards[stage.Id]
                .Select(d => new BoardDealDto(
                    d.Id,
                    d.Name,
                    accountNames.GetValueOrDefault(d.AccountId, string.Empty),
                    d.Amount,
                    d.Currency,
                    d.ClosingDate,
                    owners.GetValueOrDefault(d.OwnerUserId)))
                .ToList();
            return new BoardStageDto(stage.Id, stage.Name, stage.Kind, stage.Probability, total?.Total ?? 0m, total?.Count ?? 0, summaries);
        }).ToList();

        return new DealBoardDto(pipeline.Id, boardStages);
    }

    // ---- Ortak yardımcılar ------------------------------------------------------------------------------------------

    private static async Task<(List<T> Rows, long Total)> PageAsync<T>(
        IQueryable<T> query,
        PagedQuery paging,
        GridFieldMap<T> fields,
        System.Linq.Expressions.Expression<Func<T, object>> defaultSort,
        System.Linq.Expressions.Expression<Func<T, Guid>> tieBreaker,
        CancellationToken ct)
    {
        var total = await query.LongCountAsync(ct);
        var ordered = (IOrderedQueryable<T>)query.ApplySort(paging.SortClauses, fields, defaultSort);
        var rows = await ordered.ThenBy(tieBreaker).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct);
        return (rows, total);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> OwnerNamesAsync(IEnumerable<Guid> ownerIds, CancellationToken ct)
    {
        var ids = ownerIds.Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(ids, ct);
    }

    private async Task<Dictionary<Guid, string>> AccountNamesAsync(IEnumerable<Guid> accountIds, CancellationToken ct)
    {
        var ids = accountIds.Distinct().ToArray();
        return ids.Length == 0
            ? []
            : await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    private async Task<IReadOnlyList<ContactDto>> MapContactsAsync(IReadOnlyList<Contact> contacts, CancellationToken ct)
    {
        var accountNames = await AccountNamesAsync(contacts.Where(c => c.AccountId is not null).Select(c => c.AccountId!.Value), ct);
        var owners = await OwnerNamesAsync(contacts.Select(c => c.OwnerUserId), ct);
        return contacts.Select(c => ToDto(c, accountNames, owners)).ToList();
    }

    private async Task<IReadOnlyList<DealDto>> MapDealsAsync(IReadOnlyList<Deal> deals, CancellationToken ct)
    {
        var stageIds = deals.Select(d => d.StageId).Distinct().ToArray();
        var pipelineIds = deals.Select(d => d.PipelineId).Distinct().ToArray();
        var contactIds = deals.Where(d => d.ContactId is not null).Select(d => d.ContactId!.Value).Distinct().ToArray();

        var stages = stageIds.Length == 0
            ? []
            : await db.PipelineStages.AsNoTracking().Where(s => stageIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var pipelineNames = pipelineIds.Length == 0
            ? []
            : await db.Pipelines.AsNoTracking().Where(p => pipelineIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var contactNames = contactIds.Length == 0
            ? []
            : (await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToListAsync(ct)).ToDictionary(c => c.Id, c => c.FullName);
        var accountNames = await AccountNamesAsync(deals.Select(d => d.AccountId), ct);
        var owners = await OwnerNamesAsync(deals.Select(d => d.OwnerUserId), ct);

        return deals.Select(d =>
        {
            var stage = stages.GetValueOrDefault(d.StageId);
            return new DealDto(
                d.Id,
                d.Name,
                d.AccountId,
                accountNames.GetValueOrDefault(d.AccountId, string.Empty),
                d.ContactId,
                d.ContactId is { } contactId ? contactNames.GetValueOrDefault(contactId) : null,
                d.PipelineId,
                pipelineNames.GetValueOrDefault(d.PipelineId, string.Empty),
                d.StageId,
                stage?.Name ?? string.Empty,
                stage?.Kind ?? StageKind.Open,
                stage?.Probability ?? 0,
                d.Amount,
                d.Currency,
                d.ClosingDate,
                d.ClosedAt,
                d.LostReason,
                d.OwnerUserId,
                owners.GetValueOrDefault(d.OwnerUserId),
                d.CreatedAt,
                d.ModifiedDate);
        }).ToList();
    }

    private static AddressDto? ToDto(Address? address) => address is null ? null : new AddressDto(address.Street, address.City, address.State, address.PostalCode, address.Country);

    private static AccountDto ToDto(Account a, IReadOnlyDictionary<Guid, string> owners) => new(
        a.Id, a.Name, a.Industry, a.Website, a.Phone, a.Email, ToDto(a.BillingAddress), a.Description,
        a.OwnerUserId, owners.GetValueOrDefault(a.OwnerUserId), a.CreatedAt, a.ModifiedDate);

    private static ContactDto ToDto(Contact c, IReadOnlyDictionary<Guid, string> accountNames, IReadOnlyDictionary<Guid, string> owners) => new(
        c.Id, c.FirstName, c.LastName, c.FullName, c.Email, c.Phone, c.Mobile, c.Title,
        c.AccountId, c.AccountId is { } accountId ? accountNames.GetValueOrDefault(accountId) : null,
        ToDto(c.MailingAddress), c.OwnerUserId, owners.GetValueOrDefault(c.OwnerUserId), c.CreatedAt, c.ModifiedDate);

    private static LeadDto ToDto(Lead l, IReadOnlyDictionary<Guid, string> owners) => new(
        l.Id, l.FirstName, l.LastName, l.FullName, l.Company, l.Email, l.Phone, l.Source, l.Status, l.Rating,
        l.OwnerUserId, owners.GetValueOrDefault(l.OwnerUserId),
        l.ConvertedAccountId, l.ConvertedContactId, l.ConvertedDealId, l.ConvertedAt, l.CreatedAt, l.ModifiedDate);

    private static PipelineDto ToDto(Pipeline p) => new(
        p.Id,
        p.Name,
        p.IsDefault,
        p.Stages.OrderBy(s => s.Order).Select(s => new PipelineStageDto(s.Id, s.Name, s.Order, s.Probability, s.Kind)).ToList());
}

/// <summary>ILIKE arama deseni: kullanıcı girdisindeki joker karakterler kaçışlanır; desen her zaman parametre olarak gider.</summary>
internal static class SearchPattern
{
    public const string Escape = "\\";

    public static string? Contains(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var escaped = trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}
