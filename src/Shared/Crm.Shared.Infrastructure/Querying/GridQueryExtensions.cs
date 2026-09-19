using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Crm.Shared.Contracts.Paging;
using Microsoft.EntityFrameworkCore;

namespace Crm.Shared.Infrastructure.Querying;

/// <summary>
/// Grid standardı: sıralama ve filtreleme yalnızca beyaz listedeki alanlarda uygulanır (bilinmeyen alan → yok sayılır).
/// Kullanım: query.ApplyGrid(paging, GridFields.For&lt;T&gt;().Sortable(x =&gt; x.CreatedAt)...)
/// </summary>
public static class GridQueryExtensions
{
    public static IQueryable<T> ApplyFilters<T>(this IQueryable<T> query, IReadOnlyList<FilterClause> filters, GridFieldMap<T> map)
    {
        foreach (var clause in filters)
        {
            if (!map.TryGetFilterable(clause.Field, out var member))
            {
                continue;
            }

            var predicate = FilterExpressionBuilder.Build<T>(member, clause);
            if (predicate is not null)
            {
                query = query.Where(predicate);
            }
        }

        return query;
    }

    public static IQueryable<T> ApplySort<T>(this IQueryable<T> query, IReadOnlyList<SortClause> sorts, GridFieldMap<T> map, Expression<Func<T, object>> defaultSort, bool defaultDescending = true)
    {
        IOrderedQueryable<T>? ordered = null;
        foreach (var sort in sorts)
        {
            if (!map.TryGetSortable(sort.Field, out var keySelector))
            {
                continue;
            }

            ordered = ordered is null
                ? (sort.Descending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector))
                : (sort.Descending ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector));
        }

        return ordered ?? (defaultDescending ? query.OrderByDescending(defaultSort) : query.OrderBy(defaultSort));
    }

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(this IQueryable<T> query, PagedQuery paging, CancellationToken cancellationToken)
    {
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(paging.Skip).Take(paging.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<T>(items, paging.Page, paging.PageSize, total);
    }
}

/// <summary>Alan beyaz listesi: API alan adı → entity üyesi.</summary>
public sealed class GridFieldMap<T>
{
    private readonly Dictionary<string, Expression<Func<T, object>>> _sortable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LambdaExpression> _filterable = new(StringComparer.OrdinalIgnoreCase);

    public static GridFieldMap<T> Create() => new();

    public GridFieldMap<T> Sortable(string name, Expression<Func<T, object>> selector)
    {
        _sortable[name] = selector;
        return this;
    }

    public GridFieldMap<T> Filterable<TProp>(string name, Expression<Func<T, TProp>> selector)
    {
        _filterable[name] = selector;
        return this;
    }

    public GridFieldMap<T> Field<TProp>(string name, Expression<Func<T, TProp>> selector)
    {
        _filterable[name] = selector;
        _sortable[name] = Expression.Lambda<Func<T, object>>(Expression.Convert(selector.Body, typeof(object)), selector.Parameters);
        return this;
    }

    public bool TryGetSortable(string name, out Expression<Func<T, object>> selector) => _sortable.TryGetValue(name, out selector!);

    public bool TryGetFilterable(string name, out LambdaExpression selector) => _filterable.TryGetValue(name, out selector!);
}

/// <summary>FilterClause → Expression&lt;Func&lt;T,bool&gt;&gt; (string/guid/enum/sayı/tarih/bool).</summary>
public static class FilterExpressionBuilder
{
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo EfLike = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string)])!;
    private const string LikeWildcard = "%";

    public static Expression<Func<T, bool>>? Build<T>(LambdaExpression member, FilterClause clause)
    {
        var parameter = member.Parameters[0];
        var body = member.Body;
        var type = Nullable.GetUnderlyingType(body.Type) ?? body.Type;

        Expression? predicate = clause.Op switch
        {
            FilterOp.IsNull => Expression.Equal(body, Expression.Constant(null, body.Type)),
            FilterOp.NotNull => Expression.NotEqual(body, Expression.Constant(null, body.Type)),
            FilterOp.In or FilterOp.NotIn => BuildIn(body, type, clause),
            FilterOp.Between => BuildBetween(body, type, clause),
            FilterOp.Contains when type == typeof(string) => BuildLike(body, clause.Value, LikeWildcard, LikeWildcard),
            FilterOp.StartsWith when type == typeof(string) => BuildLike(body, clause.Value, string.Empty, LikeWildcard),
            _ => BuildComparison(body, type, clause),
        };

        return predicate is null ? null : Expression.Lambda<Func<T, bool>>(predicate, parameter);
    }

    private static Expression? BuildComparison(Expression body, Type type, FilterClause clause)
    {
        if (!TryConvert(clause.Value, type, out var value))
        {
            return null;
        }

        var constant = Expression.Constant(value, body.Type);
        return clause.Op switch
        {
            FilterOp.Eq => Expression.Equal(body, constant),
            FilterOp.Ne => Expression.NotEqual(body, constant),
            FilterOp.Gt => Expression.GreaterThan(body, constant),
            FilterOp.Gte => Expression.GreaterThanOrEqual(body, constant),
            FilterOp.Lt => Expression.LessThan(body, constant),
            FilterOp.Lte => Expression.LessThanOrEqual(body, constant),
            _ => null,
        };
    }

    private static Expression? BuildIn(Expression body, Type type, FilterClause clause)
    {
        var values = clause.Values.SelectMany(v => v.Split(FilterOpNames.ValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(v => TryConvert(v, type, out var c) ? c : null).Where(v => v is not null).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        var listType = typeof(List<>).MakeGenericType(body.Type);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var v in values)
        {
            list.Add(v);
        }

        var contains = typeof(Enumerable).GetMethods().First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(body.Type);
        Expression call = Expression.Call(contains, Expression.Constant(list, listType), body);
        return clause.Op == FilterOp.NotIn ? Expression.Not(call) : call;
    }

    private static Expression? BuildBetween(Expression body, Type type, FilterClause clause)
    {
        var parts = clause.Values.Count >= 2 ? clause.Values.ToArray() : clause.Value?.Split(FilterOpNames.ValueSeparator) ?? [];
        if (parts.Length < 2 || !TryConvert(parts[0], type, out var low) || !TryConvert(parts[1], type, out var high))
        {
            return null;
        }

        return Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(low, body.Type)),
            Expression.LessThanOrEqual(body, Expression.Constant(high, body.Type)));
    }

    private static Expression? BuildLike(Expression body, string? value, string prefix, string suffix)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var pattern = Expression.Constant(prefix + value + suffix);
        return Expression.Call(EfLike, Expression.Constant(EF.Functions), body, pattern);
    }

    private static bool TryConvert(string? raw, Type type, out object? value)
    {
        value = null;
        if (raw is null)
        {
            return false;
        }

        try
        {
            if (type == typeof(string)) { value = raw; return true; }
            if (type == typeof(Guid)) { value = Guid.Parse(raw); return true; }
            if (type.IsEnum) { value = Enum.Parse(type, raw, ignoreCase: true); return true; }
            if (type == typeof(bool)) { value = bool.Parse(raw); return true; }
            if (type == typeof(DateTime)) { value = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal); return true; }
            if (type == typeof(DateOnly)) { value = DateOnly.Parse(raw, CultureInfo.InvariantCulture); return true; }
            if (type == typeof(int)) { value = int.Parse(raw, CultureInfo.InvariantCulture); return true; }
            if (type == typeof(long)) { value = long.Parse(raw, CultureInfo.InvariantCulture); return true; }
            if (type == typeof(decimal)) { value = decimal.Parse(raw, CultureInfo.InvariantCulture); return true; }
            if (type == typeof(double)) { value = double.Parse(raw, CultureInfo.InvariantCulture); return true; }
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
