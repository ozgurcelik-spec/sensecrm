using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Activities.Tests.Api.ActivitiesApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Activities.Tests.Api;

/// <summary>
/// L2 — <c>relatedName</c> yalnız çağıran ilişkili kaydın türünü okuyabiliyorsa döner; yalnız <c>crm.activities.read</c> nötr bir yer
/// tutucu (<c>***</c>) görür (tür ve kimlik yine döner). Liste ve tekil okuma aynı kuralı uygular.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RelatedNameVisibilityApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task RelatedName_IsHidden_UnlessTheCallerCanReadThatRecordType()
    {
        var org = await factory.NewOrgAsync("Related Name Org");
        var account = await org.Admin.CreateAccountAsync("Gizli Firma A.S.");
        var accountId = account.Id();
        var activity = await org.Admin.CreateActivityAsync("task", "Firmayı ara", new { relatedType = "account", relatedId = accountId });
        var activityId = activity.Id();

        var activitiesOnly = await factory.AddMemberAsync(org, "Yalnız Aktivite", "crm.activities.read");
        var withAccounts = await factory.AddMemberAsync(org, "Firma Okuyabilir", "crm.activities.read", "crm.accounts.read");
        var withOtherType = await factory.AddMemberAsync(org, "Kişi Okuyabilir", "crm.activities.read", "crm.contacts.read");

        // Yönetici (tüm izinler) adı görür.
        (await org.Admin.GetJsonAsync($"{ActivitiesPath}/{activityId}")).Str("relatedName").ShouldBe("Gizli Firma A.S.");

        // Yalnız activities.read: yer tutucu; tür ve kimlik dönmeye devam eder.
        var hidden = await activitiesOnly.Client.GetJsonAsync($"{ActivitiesPath}/{activityId}");
        hidden.Str("relatedName").ShouldBe("***");
        (hidden.Str("relatedType"), hidden.GetProperty("relatedId").GetGuid()).ShouldBe(("account", accountId));
        var listed = (await activitiesOnly.Client.GetJsonAsync(ActivitiesPath)).GetProperty("items").EnumerateArray().Single(a => a.Id() == activityId);
        listed.Str("relatedName").ShouldBe("***");

        // Firma okuma izni olan görür; başka tür (kişi) izni yetmez.
        (await withAccounts.Client.GetJsonAsync($"{ActivitiesPath}/{activityId}")).Str("relatedName").ShouldBe("Gizli Firma A.S.");
        (await withAccounts.Client.GetJsonAsync(ActivitiesPath)).GetProperty("items").EnumerateArray().Single(a => a.Id() == activityId).Str("relatedName").ShouldBe("Gizli Firma A.S.");
        (await withOtherType.Client.GetJsonAsync($"{ActivitiesPath}/{activityId}")).Str("relatedName").ShouldBe("***");
    }

    [Fact]
    public async Task ActivitiesWithoutARelatedRecord_HaveNoRelatedName()
    {
        var org = await factory.NewOrgAsync("No Related Org");
        var activity = await org.Admin.CreateActivityAsync("task", "İlişkisiz görev");
        var reader = await factory.AddMemberAsync(org, "Okuyucu", "crm.activities.read");

        (await reader.Client.GetJsonAsync($"{ActivitiesPath}/{activity.Id()}")).TryGetProperty("relatedName", out _).ShouldBeFalse();
    }
}
