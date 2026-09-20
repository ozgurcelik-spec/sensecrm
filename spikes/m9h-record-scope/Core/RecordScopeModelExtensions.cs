using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Sense.Crm.Spikes.M9h.Core;

/// <summary>Filtre ifadesinin nasıl kurulacağı: A = statik çağrılar (DbContext örneği yok), B = <c>this.RecordScope</c> örnek üyesi (mevcut "Tenant" filtresi kalıbı).</summary>
public enum FilterStyle
{
    StaticCalls,
    ContextMember,

    /// <summary>C = statik yöntem, ilk argüman DbContext örneği (<c>RecordScopeContext.ReadAll(ctx, "deal")</c>): taban sınıfta üye gerektirmez, mühürlü bağlamlarda ModelCustomizer ile de çalışır.</summary>
    StaticWithContextArg,
}

/// <summary>Model kurarken filtre kurucusu (bağlam ifadesi B stili için verilir).</summary>
public delegate LambdaExpression RecordScopeFilterFactory(FilterStyle style, Expression? contextExpr, DbContext? context);

/// <summary>
/// Plan D7 API taslağı: <c>b.HasRecordScope("deal", x =&gt; x.OwnerUserId)</c> yalnız bir ANNOTATION bırakır; asıl <c>HasQueryFilter("RecordScope", …)</c>
/// çağrısını taban <c>OnModelCreating</c> döngüsü yapar (Tenant/SoftDelete ile aynı yer) → kaynak modül dosyalarında tek satır, unutulan varlık mimari testle yakalanır.
/// </summary>
public static class RecordScopeModelExtensions
{
    public const string FilterName = "RecordScope";
    public const string AnnotationName = "Sense:RecordScopeFilterFactory";
    public const string ResourceAnnotation = "Sense:RecordScopeResource";

    private static readonly MethodInfo Contains = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(typeof(Guid));

    private static readonly MethodInfo AnyWithPredicate = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2);

    /// <summary>Tek sahip yolu: <c>ReadAll || ReadOwners.Contains(x.Owner)</c>.</summary>
    public static EntityTypeBuilder<T> HasRecordScope<T>(this EntityTypeBuilder<T> builder, string resource, Expression<Func<T, Guid>> owner)
        where T : class
    {
        builder.HasAnnotation(ResourceAnnotation, resource);
        builder.HasAnnotation(AnnotationName, (RecordScopeFilterFactory)((style, ctxExpr, _) => OwnerFilter(style, ctxExpr, resource, owner)));
        return builder;
    }

    /// <summary>Filtre ifadesini doğrudan üretir (mühürlü/gerçek bağlamlarda <c>IModelCustomizer</c> içinden kullanılır).</summary>
    public static Expression<Func<T, bool>> OwnerFilter<T>(FilterStyle style, Expression? ctxExpr, string resource, Expression<Func<T, Guid>> owner) =>
        Lambda<T>(owner.Parameters[0], Or(ReadAll(style, ctxExpr, resource), OwnerContains(style, ctxExpr, resource, owner.Body)));

    /// <summary>
    /// Çift yol + sahipsiz havuz (Service <c>case</c>): <c>ReadAll || (assigned == null &amp;&amp; IncludeUnowned) || Owners.Contains(assigned) || Owners.Contains(created)</c>.
    /// </summary>
    public static EntityTypeBuilder<T> HasRecordScope<T>(
        this EntityTypeBuilder<T> builder, string resource, Expression<Func<T, Guid?>> assigned, Expression<Func<T, Guid>> created)
        where T : class
    {
        builder.HasAnnotation(ResourceAnnotation, resource);
        builder.HasAnnotation(AnnotationName, (RecordScopeFilterFactory)((style, ctxExpr, _) =>
        {
            var p = assigned.Parameters[0];
            var createdBody = new ParameterReplacer(created.Parameters[0], p).Visit(created.Body);
            var assignedValue = Expression.Property(assigned.Body, nameof(Nullable<Guid>.Value));
            var isNull = Expression.Equal(assigned.Body, Expression.Constant(null, typeof(Guid?)));
            var pool = Expression.AndAlso(isNull, Unowned(style, ctxExpr, resource));
            var body = Or(ReadAll(style, ctxExpr, resource), Or(pool, Or(OwnerContains(style, ctxExpr, resource, assignedValue), OwnerContains(style, ctxExpr, resource, createdBody))));
            return Lambda<T>(p, body);
        }));
        return builder;
    }

    /// <summary>Alt kayıt (D7c): üst kaydın görünürlüğünü devralır: <c>ctx.Set&lt;TParent&gt;().Any(p =&gt; p.Id == c.ParentId)</c> (üstün kendi filtreleri iç sorguda uygulanır).</summary>
    public static EntityTypeBuilder<TChild> HasRecordScopeThroughParent<TChild, TParent>(
        this EntityTypeBuilder<TChild> builder, Expression<Func<TChild, Guid>> fk, Expression<Func<TParent, Guid>> parentKey)
        where TChild : class
        where TParent : class
    {
        builder.HasAnnotation(AnnotationName, (RecordScopeFilterFactory)((_, ctxExpr, ctx) =>
        {
            if (ctxExpr is null)
            {
                throw new InvalidOperationException("ThroughParent (EXISTS) bir DbContext örneği ister (Set<T>()).");
            }

            var setCall = Expression.Call(ctxExpr, typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(typeof(TParent)));
            var pp = parentKey.Parameters[0];
            var predicate = Expression.Lambda<Func<TParent, bool>>(Expression.Equal(parentKey.Body, fk.Body), pp);
            var any = Expression.Call(AnyWithPredicate.MakeGenericMethod(typeof(TParent)), setCall, Expression.Quote(predicate));
            return Expression.Lambda<Func<TChild, bool>>(any, fk.Parameters[0]);
        }));
        return builder;
    }

    /// <summary><c>IgnoreQueryFilters([RecordScope])</c> sarmalı: kiracı + silinmiş filtreleri KALIR (plan D10).</summary>
    public static IQueryable<T> IgnoreRecordScope<T>(this IQueryable<T> query)
        where T : class => query.IgnoreQueryFilters([FilterName]);

    private static Expression<Func<T, bool>> Lambda<T>(ParameterExpression p, Expression body) => Expression.Lambda<Func<T, bool>>(body, p);

    private static Expression Or(Expression a, Expression b) => Expression.OrElse(a, b);

    private static Expression ReadAll(FilterStyle style, Expression? ctxExpr, string resource) =>
        Call(style, ctxExpr, nameof(RecordScopeContext.ReadAll), resource);

    private static Expression Unowned(FilterStyle style, Expression? ctxExpr, string resource) =>
        Call(style, ctxExpr, nameof(RecordScopeContext.IncludeUnowned), resource);

    private static Expression OwnerContains(FilterStyle style, Expression? ctxExpr, string resource, Expression owner) =>
        Expression.Call(Contains, Call(style, ctxExpr, nameof(RecordScopeContext.ReadOwners), resource), owner);

    private static Expression Call(FilterStyle style, Expression? ctxExpr, string method, string resource)
    {
        var arg = Expression.Constant(resource);
        if (style == FilterStyle.StaticCalls || ctxExpr is null)
        {
            return Expression.Call(typeof(RecordScopeContext).GetMethod(method, [typeof(string)])!, arg);
        }

        if (style == FilterStyle.StaticWithContextArg)
        {
            return Expression.Call(typeof(RecordScopeContext).GetMethod(method, [typeof(Microsoft.EntityFrameworkCore.DbContext), typeof(string)])!, ctxExpr, arg);
        }

        var view = Expression.Property(ctxExpr, nameof(ISpikeContext.RecordScope));
        return Expression.Call(view, typeof(RecordScopeView).GetMethod(method, [typeof(string)])!, arg);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>B stili için DbContext'in sunduğu üye (mevcut <c>CurrentTenantId</c> kalıbı).</summary>
public interface ISpikeContext
{
    RecordScopeView RecordScope { get; }
}
