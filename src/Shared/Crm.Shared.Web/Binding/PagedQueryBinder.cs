using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Paging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.Options;

namespace Crm.Shared.Web.Binding;

/// <summary>
/// ?page=2&amp;pageSize=25&amp;sort=-createdAt,lastName&amp;q=ahmet&amp;status=active&amp;createdAt.gte=2026-01-01
/// sorgu dizesini PagedQuery'ye bağlar. Bilinen parametreler dışındaki her anahtar filtre kabul edilir (alan[.op]).
/// </summary>
public sealed class PagedQueryBinder(IOptions<PagingOptions> paging) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var query = bindingContext.HttpContext.Request.Query;
        var filters = new List<FilterClause>();

        foreach (var (key, values) in query)
        {
            if (GridQueryParameters.Reserved.Contains(key))
            {
                continue;
            }

            var field = key;
            var opName = string.Empty;
            var dot = key.LastIndexOf(FilterOpNames.Separator);
            if (dot > 0)
            {
                field = key[..dot];
                opName = key[(dot + 1)..];
            }

            if (!FilterClause.TryParseOp(opName, out var op))
            {
                continue;
            }

            filters.Add(new FilterClause(field, op, values.Where(v => v is not null).Select(v => v!).ToList()));
        }

        var result = new PagedQuery
        {
            Page = ParseInt(query[GridQueryParameters.Page], PagingDefaults.FirstPage),
            PageSize = ParseInt(query[GridQueryParameters.PageSize], paging.Value.DefaultPageSize),
            Sort = query[GridQueryParameters.Sort],
            Q = query[GridQueryParameters.Q],
            Filters = filters,
        }.Clamp(paging.Value);

        bindingContext.Result = ModelBindingResult.Success(result);
        return Task.CompletedTask;
    }

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var i) ? i : fallback;
}

public sealed class PagedQueryBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.Metadata.ModelType == typeof(PagedQuery) ? new BinderTypeModelBinder(typeof(PagedQueryBinder)) : null;
}

public static class GridQueryParameters
{
    public const string Page = "page";
    public const string PageSize = "pageSize";
    public const string Sort = "sort";
    public const string Q = "q";

    public static readonly HashSet<string> Reserved = new([Page, PageSize, Sort, Q], StringComparer.OrdinalIgnoreCase);
}
