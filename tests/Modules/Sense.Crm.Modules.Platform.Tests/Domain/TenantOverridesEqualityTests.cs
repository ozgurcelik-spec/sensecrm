using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

/// <summary>
/// Kiracı istisnasının yapısal eşitliği: <c>jsonb</c> sütunu JSON metnini normalize eder (boşluk, anahtar sırası), bu yüzden "değişiklik yok" tespiti metinle değil yapıyla yapılır;
/// aksi hâlde aynı istisnayla yapılan PUT sahte <c>PlanChanged</c> olayı/denetim satırı üretirdi.
/// </summary>
public sealed class TenantOverridesEqualityTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameAs_IgnoresWhitespaceAndKeyOrder()
    {
        var written = TenantOverrides.FromJson("""{"maxUsers":7,"maxRecords":{"sales":100,"activities":5},"modules":{"workflows":true,"commerce":false}}""");
        var normalized = TenantOverrides.FromJson("""{"modules": {"commerce": false, "workflows": true}, "maxUsers": 7, "maxRecords": {"activities": 5, "sales": 100}}""");

        written.SameAs(normalized).ShouldBeTrue();
        normalized.SameAs(written).ShouldBeTrue();
        TenantOverrides.None.SameAs(TenantOverrides.FromJson(null)).ShouldBeTrue();
        TenantOverrides.None.SameAs(TenantOverrides.FromJson("{}")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"maxUsers":7}""", """{"maxUsers":8}""")]
    [InlineData("""{"maxUsers":null}""", """{}""")]
    [InlineData("""{"maxUsers":null}""", """{"maxUsers":0}""")]
    [InlineData("""{"maxRecords":{"sales":1}}""", """{"maxRecords":{"sales":null}}""")]
    [InlineData("""{"maxRecords":{"sales":1}}""", """{"maxRecords":{"sales":1,"activities":1}}""")]
    [InlineData("""{"modules":{"workflows":true}}""", """{"modules":{"workflows":false}}""")]
    [InlineData("""{"modules":{"workflows":true}}""", """{"modules":{"workflows":true,"commerce":true}}""")]
    public void SameAs_DistinguishesEveryRealDifference(string left, string right)
    {
        TenantOverrides.FromJson(left).SameAs(TenantOverrides.FromJson(right)).ShouldBeFalse();
        TenantOverrides.FromJson(right).SameAs(TenantOverrides.FromJson(left)).ShouldBeFalse();
    }

    [Fact]
    public void ChangeSubscription_WithTheSameOverridesInDifferentJsonForm_IsNotAChange()
    {
        var account = TenantAccount.Create(Guid.NewGuid(), "Acme", "acme", "internal", AccountSources.Signup, isSystem: false, null, null, Now, null);
        var first = account.ChangeSubscription("internal", null, null, TenantOverrides.FromJson("""{"maxUsers":7,"modules":{"workflows":true,"commerce":false}}"""), Now);
        first.Value.OverridesChanged.ShouldBeTrue();
        var changedAt = account.PlanChangedAt;

        // jsonb'den okunan metin farklı biçimlendirilmiş olabilir; aynı istisna değişiklik sayılmaz.
        var again = account.ChangeSubscription("internal", null, null, TenantOverrides.FromJson("""{"modules": {"commerce": false, "workflows": true}, "maxUsers": 7}"""), Now.AddHours(1));

        again.Value.Changed.ShouldBeFalse();
        account.PlanChangedAt.ShouldBe(changedAt, "değişiklik yoksa hiçbir alan değişmez");

        var real = account.ChangeSubscription("internal", null, null, TenantOverrides.FromJson("""{"maxUsers":8,"modules":{"workflows":true,"commerce":false}}"""), Now.AddHours(2));
        real.Value.OverridesChanged.ShouldBeTrue();
    }
}
