using System.Linq.Expressions;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Infrastructure.Querying;
using Shouldly;
using Xunit;

namespace Crm.Shared.Infrastructure.Tests;

/// <summary>L8 — grid <c>contains</c>/<c>startswith</c> filtreleri kullanıcı girdisindeki LIKE jokerlerini kaçışlar.</summary>
public sealed class GridLikeEscapingTests
{
    private sealed record Item(string Name);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("%_\\", "\\%\\_\\\\")]
    public void EscapeLike_EscapesWildcardsAndTheEscapeCharacter(string input, string expected) =>
        FilterExpressionBuilder.EscapeLike(input).ShouldBe(expected);

    [Theory]
    [InlineData(FilterOp.Contains, "50%_off", "%50\\%\\_off%")]
    [InlineData(FilterOp.StartsWith, "50%_off", "50\\%\\_off%")]
    public void ContainsAndStartsWith_PassAnEscapedPattern_AndAnEscapeCharacter(FilterOp op, string value, string expectedPattern)
    {
        Expression<Func<Item, string>> member = x => x.Name;

        var predicate = FilterExpressionBuilder.Build<Item>(member, new FilterClause("name", op, [value]));

        var call = (predicate!.Body as MethodCallExpression).ShouldNotBeNull(); // (alt tür MethodCallExpression4: 4 bağımsız değişken)
        call.Method.Name.ShouldBe("Like");
        call.Arguments.Count.ShouldBe(4, "EF.Functions.Like(matchExpression, pattern, escapeCharacter)");
        ((ConstantExpression)call.Arguments[2]).Value.ShouldBe(expectedPattern);
        ((ConstantExpression)call.Arguments[3]).Value.ShouldBe("\\");
    }
}
