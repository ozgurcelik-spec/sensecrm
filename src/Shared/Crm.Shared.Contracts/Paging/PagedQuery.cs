using Crm.Shared.Contracts.Configuration;

namespace Crm.Shared.Contracts.Paging;

/// <summary>Tüm liste uçlarının ortak sayfalama/sıralama/filtre girdisi (grid standardı). Üst sınır <see cref="PagingOptions"/> ile ayarlanır.</summary>
public sealed record PagedQuery
{
    private readonly int _page = PagingDefaults.FirstPage;
    private readonly int _pageSize = PagingDefaults.DefaultPageSize;

    public int Page
    {
        get => _page;
        init => _page = value < PagingDefaults.FirstPage ? PagingDefaults.FirstPage : value;
    }

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value < 1 ? PagingDefaults.DefaultPageSize : value;
    }

    /// <summary>"-createdAt,lastName" biçimi: '-' öneki azalan.</summary>
    public string? Sort { get; init; }

    /// <summary>Serbest metin arama.</summary>
    public string? Q { get; init; }

    /// <summary>Alan bazlı filtreler.</summary>
    public IReadOnlyList<FilterClause> Filters { get; init; } = [];

    public int Skip => (Page - PagingDefaults.FirstPage) * PageSize;

    public IReadOnlyList<SortClause> SortClauses => SortClause.Parse(Sort);

    /// <summary>Ayarlardaki üst sınıra göre kırpılmış kopya.</summary>
    public PagedQuery Clamp(PagingOptions options) => this with
    {
        PageSize = Math.Min(PageSize <= 0 ? options.DefaultPageSize : PageSize, options.MaxPageSize),
    };
}

public enum FilterOp
{
    Eq,
    Ne,
    Gt,
    Gte,
    Lt,
    Lte,
    In,
    NotIn,
    Contains,
    StartsWith,
    Between,
    IsNull,
    NotNull,
}

/// <summary>Sorgu dizesindeki operatör ekleri: field.gte=…</summary>
public static class FilterOpNames
{
    public const string Eq = "eq";
    public const string Ne = "ne";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string In = "in";
    public const string NotIn = "notin";
    public const string Contains = "contains";
    public const string StartsWith = "startswith";
    public const string Between = "between";
    public const string IsNull = "isnull";
    public const string NotNull = "notnull";
    public const char Separator = '.';
    public const char ValueSeparator = ',';
}

public static class SortSyntax
{
    public const char Descending = '-';
    public const char Ascending = '+';
    public const char Separator = ',';
}

public sealed record FilterClause(string Field, FilterOp Op, IReadOnlyList<string> Values)
{
    public string? Value => Values.Count > 0 ? Values[0] : null;

    public static bool TryParseOp(string? op, out FilterOp result)
    {
        result = FilterOp.Eq;
        switch (op?.ToLowerInvariant())
        {
            case null or "" or FilterOpNames.Eq: result = FilterOp.Eq; return true;
            case FilterOpNames.Ne: result = FilterOp.Ne; return true;
            case FilterOpNames.Gt: result = FilterOp.Gt; return true;
            case FilterOpNames.Gte: result = FilterOp.Gte; return true;
            case FilterOpNames.Lt: result = FilterOp.Lt; return true;
            case FilterOpNames.Lte: result = FilterOp.Lte; return true;
            case FilterOpNames.In: result = FilterOp.In; return true;
            case FilterOpNames.NotIn: result = FilterOp.NotIn; return true;
            case FilterOpNames.Contains: result = FilterOp.Contains; return true;
            case FilterOpNames.StartsWith: result = FilterOp.StartsWith; return true;
            case FilterOpNames.Between: result = FilterOp.Between; return true;
            case FilterOpNames.IsNull: result = FilterOp.IsNull; return true;
            case FilterOpNames.NotNull: result = FilterOp.NotNull; return true;
            default: return false;
        }
    }
}

public sealed record SortClause(string Field, bool Descending)
{
    public static IReadOnlyList<SortClause> Parse(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return [];
        }

        return sort.Split(SortSyntax.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s[0] == SortSyntax.Descending
                ? new SortClause(s[1..], true)
                : new SortClause(s.TrimStart(SortSyntax.Ascending), false))
            .Where(s => s.Field.Length > 0)
            .ToList();
    }
}

/// <summary>Liste uçlarının ortak cevabı.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public bool HasNext => (long)Page * PageSize < TotalCount;

    public bool HasPrevious => Page > PagingDefaults.FirstPage;

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagedResult<T> Empty(PagedQuery query) => new([], query.Page, query.PageSize, 0);

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> map) => new(Items.Select(map).ToList(), Page, PageSize, TotalCount);
}

/// <summary>Sonsuz akış uçları için cursor tabanlı cevap.</summary>
public sealed record CursorResult<T>(IReadOnlyList<T> Items, string? NextCursor);
