using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

/// <summary>Mezar taşı (tombstone) alanları: ad/slug redaksiyonu ve slug benzersizliği.</summary>
public sealed class TenantTombstoneTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MarkDeleted_RedactsTheName_AndBuildsAnEightHexSlug()
    {
        var tenantId = Guid.Parse("018f3b2a-1111-7000-8000-00000000abcd");
        var account = PendingDeletion(tenantId, "Gizli Musteri A.S.", "gizli-musteri");

        account.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();

        account.Name.ShouldBe("[deleted]");
        account.Slug.ShouldMatch("^deleted-[0-9a-f]{8}$");
        account.Slug.ShouldNotContain("gizli");
        account.Status.ShouldBe(AccountStatuses.Deleted);
        account.Overrides.ShouldBeNull();
    }

    /// <summary>
    /// Kiracı kimlikleri UUIDv7'dir (zamana göre sıralı): kısa süre içinde açılan iki kiracının ilk 8 hex karakteri aynıdır. Slug'ın bu önekten türetilmesi
    /// <c>ix_tenant_accounts_slug</c> benzersiz indeksini çakıştırıp ikinci imhayı sonsuza dek <c>failed</c> bırakırdı.
    /// </summary>
    [Fact]
    public void MarkDeleted_ProducesDistinctSlugs_ForTenantsCreatedWithinTheSameMinute()
    {
        var first = PendingDeletion(Guid.Parse("018f3b2a-1111-7000-8000-aaaaaaaa0001"), "Bir", "bir");
        var second = PendingDeletion(Guid.Parse("018f3b2a-1111-7000-9000-bbbbbbbb0002"), "Iki", "iki");
        first.TenantId.ToString("N")[..8].ShouldBe(second.TenantId.ToString("N")[..8]);

        first.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();
        second.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();

        first.Slug.ShouldNotBe(second.Slug);
    }

    [Fact]
    public void MarkDeleted_IsIdempotent_AndKeepsTheFirstDeletionTime()
    {
        var account = PendingDeletion(Guid.Parse("018f3b2a-1111-7000-8000-00000000abcd"), "Musteri", "musteri");
        account.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();
        var slug = account.Slug;

        account.MarkDeleted(NowUtc.AddDays(5)).IsSuccess.ShouldBeTrue();

        account.Slug.ShouldBe(slug);
        account.DeletedAt.ShouldBe(NowUtc);
    }

    private static TenantAccount PendingDeletion(Guid tenantId, string name, string slug)
    {
        var account = TenantAccount.Create(tenantId, name, slug, "internal", AccountSources.Signup, isSystem: false, trialEndsOn: null, trialEndsAt: null, NowUtc, onboardingDismissedAt: null);
        account.MarkPendingDeletion().IsSuccess.ShouldBeTrue();
        return account;
    }
}
