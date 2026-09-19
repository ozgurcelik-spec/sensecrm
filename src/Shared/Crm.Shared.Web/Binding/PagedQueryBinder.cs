using System.Globalization;
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

        // L6: absürt sayfa değerleri (taşma) 500 yerine 400 "validation" (alan = page/pageSize) döner.
        var page = ParseInt(query[GridQueryParameters.Page], PagingDefaults.FirstPage);
        var pageSize = ParseInt(query[GridQueryParameters.PageSize], paging.Value.DefaultPageSize);
        if (IsOutOfRange(query[GridQueryParameters.Page], page, PagingDefaults.MaxPage))
        {
            bindingContext.ModelState.AddModelError(GridQueryParameters.Page, GridQueryParameters.InvalidPageMessage);
        }

        if (IsOutOfRange(query[GridQueryParameters.PageSize], pageSize, int.MaxValue))
        {
            bindingContext.ModelState.AddModelError(GridQueryParameters.PageSize, GridQueryParameters.InvalidPageMessage);
        }

        if (!bindingContext.ModelState.IsValid)
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        var result = new PagedQuery
        {
            Page = page,
            PageSize = pageSize,
            Sort = query[GridQueryParameters.Sort],
            Q = query[GridQueryParameters.Q],
            Filters = filters,
        }.Clamp(paging.Value);

        bindingContext.Result = ModelBindingResult.Success(result);
        return Task.CompletedTask;
    }

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    /// <summary>
    /// Değer verilmiş ama geçersizse (int'e sığmayan sayı ya da <paramref name="max"/>'ı aşan) true. Boş/sayısal olmayan değer
    /// eskisi gibi varsayılana düşer; küçük/negatif değerler <see cref="PagedQuery"/> içinde kırpılır.
    /// </summary>
    private static bool IsOutOfRange(string? raw, int parsed, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wide))
        {
            return wide > max || wide < int.MinValue;
        }

        // long'a bile sığmayan rakam dizisi de absürttür; rakam olmayan değer varsayılana düşer.
        return parsed == int.MinValue || raw.Trim().TrimStart('-', '+').All(char.IsAsciiDigit);
    }
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
    public const string InvalidPageMessage = "The value is out of the accepted range.";

    public static readonly HashSet<string> Reserved = new([Page, PageSize, Sort, Q], StringComparer.OrdinalIgnoreCase);
}
