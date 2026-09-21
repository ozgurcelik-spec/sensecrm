using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// C-SEC2 H1 + H2 + M6 (HTTP): yıkıcı platform komutlarında sunucu tarafı <b>onay adı</b> ve <b>step-up</b> (çağıranın parolası; hız sınırlı), aktif platform yöneticisi üyesi olan
/// kiracının (eski, <c>is_system</c> eksik kurulum biçiminde bile) askıya alınamaması/silinememesi, denetim + <c>TenantDeletionRequested</c> olayı, başarısız imhayı yeniden deneme
/// ve platform yöneticisi geri alma (son yönetici korunur, oturumlar kapanır).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StepUpAndProtectionApiTests(CrmApiFactory factory)
{
    // ---- H2: onay adı --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("baska bir ad")]
    [InlineData("yanlis")]
    public async Task DeletionRequest_WithAMissingOrWrongOrganizationName_Is422_AndChangesNothing(string? typed)
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("cfm") + " onay");

        var body = new { reason = "musteri talebi", retentionDays = 7, confirmTenantName = typed, currentPassword = PlatformPassword };
        var response = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", body);

        await response.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.confirmation_mismatch");
        await AssertUntouchedAsync(org.TenantId);
    }

    [Fact]
    public async Task DeletionRequest_NameMatch_IsTrimmedButCaseSensitive()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("cs") + " Buyuk Kucuk");

        var wrongCase = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name.ToUpperInvariant(), currentPassword = PlatformPassword });
        await wrongCase.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.confirmation_mismatch");

        var padded = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = $"  {org.Name}  ", currentPassword = PlatformPassword });
        padded.StatusCode.ShouldBe(HttpStatusCode.OK, await padded.Content.ReadAsStringAsync(Ct));
    }

    // ---- H2: step-up ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletionRequest_WithoutThePassword_Is422StepUpRequired_AndAWrongPasswordIs422StepUpFailed_NeitherChangesAnything()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("stp") + " stepup");

        var missing = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name });
        await missing.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_required");

        var empty = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name, currentPassword = "" });
        await empty.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_required");

        var wrong = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name, currentPassword = "Yanlis.Parola.1" });
        await wrong.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_failed");

        // Oturum düşmez (401 DEĞİL): aynı jetonla doğru parola hemen çalışır.
        var ok = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name, currentPassword = PlatformPassword });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task StepUp_IsRateLimited_AfterRepeatedWrongPasswords_EvenForTheCorrectPasswordAfterwards()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("rl") + " ratelimit");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name, currentPassword = $"Yanlis.{attempt}.Parola" });
            await wrong.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_failed");
        }

        var blocked = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name, currentPassword = PlatformPassword });
        await blocked.ProblemBodyAsync(HttpStatusCode.TooManyRequests, "platform.step_up_rate_limited");
        await AssertUntouchedAsync(org.TenantId);
    }

    [Fact]
    public async Task BlockedSuspension_RequiresStepUp_ButReadOnlySuspensionDoesNot()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("blk") + " askida");

        var noPassword = await platform.SuspendRawAsync(org.TenantId, "kural ihlali", "blocked", currentPassword: null);
        await noPassword.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_required");
        var wrong = await platform.SuspendRawAsync(org.TenantId, "kural ihlali", "blocked", currentPassword: "Yanlis.Parola.2");
        await wrong.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_failed");
        (await AccountStatusAsync(org.TenantId)).ShouldBe("active", "başarısız step-up hiçbir şeyi değiştirmez");

        var readOnly = await platform.SuspendRawAsync(org.TenantId, "odeme gecikti", "readOnly", currentPassword: null);
        readOnly.StatusCode.ShouldBe(HttpStatusCode.NoContent, await readOnly.Content.ReadAsStringAsync(Ct));
        (await platform.ReactivateRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var blocked = await platform.SuspendRawAsync(org.TenantId, "kural ihlali", "blocked");
        blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent, await blocked.Content.ReadAsStringAsync(Ct));
        (await AccountStatusAsync(org.TenantId)).ShouldBe("suspended");
    }

    [Fact]
    public async Task ATenantAdministrator_EvenWithAValidPassword_CannotReachTheDestructiveEndpoints()
    {
        var org = await factory.SyncedOrgAsync(Token("adm") + " tenantadmin");

        var response = await org.Admin.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", new { reason = "x", confirmTenantName = org.Name, currentPassword = DefaultPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertUntouchedAsync(org.TenantId);
    }

    // ---- H2: denetim + olay ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletionRequest_WritesTheAuditRow_AndTheTenantDeletionRequestedEvent_InTheSameTransaction()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("evt") + " olay");

        var response = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", await platform.DeletionBodyAsync(org.TenantId, "musteri talebi", 14), HttpStatusCode.OK);

        var audit = (await factory.AuditDetailsAsync(org.TenantId, "deletion.requested")).ShouldHaveSingleItem();
        audit.GetProperty("retentionDays").GetInt32().ShouldBe(14);
        var events = await factory.PlatformEventsAsync(org.TenantId, "TenantDeletionRequested");
        events.ShouldHaveSingleItem().GetProperty("scheduledFor").GetDateTimeOffset().ShouldBe(response.GetProperty("scheduledFor").GetDateTimeOffset());
    }

    // ---- H1: korunan kiracı -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATenantWithAnActivePlatformAdminMember_CanNeverBeSuspendedOrDeleted_EvenWhenIsSystemIsMissing()
    {
        var platform = await factory.PlatformAdminAsync();
        var (_, _, operatingTenant) = await platform.WhoAmIAsync();

        // Eski (M7 backfill'i öncesi) kurulum biçimi: platform işletim organizasyonunun hesabı is_system = false yazılmış.
        await factory.DrainOutboxesAsync();
        await factory.SetAccountAsync(factory, operatingTenant, "is_system = FALSE, source = 'backfill'");

        foreach (var mode in new[] { "readOnly", "blocked" })
        {
            var suspend = await platform.SuspendRawAsync(operatingTenant, "deneme", mode);
            await suspend.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");
        }

        var deletion = await platform.RequestDeletionRawAsync(operatingTenant);
        await deletion.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");

        (await AccountStatusAsync(operatingTenant)).ShouldBe("active");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.deletion_requests WHERE tenant_id = @t", ("t", operatingTenant))).ShouldBe(0L);
    }

    [Fact]
    public async Task AdminMemberProtection_Ends_WhenTheAdminBecomesInactiveOrTheFlagIsRevoked()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("prot") + " korumasi");
        var (adminId, _, _) = await platform.WhoAmIAsync();

        // Platform yöneticisi organizasyona üye olur (davet + kabul): organizasyon artık korunur.
        var roleId = await ErasureKit.StandardRoleAsync(org.Admin);
        var platformEmail = (await platform.GetJsonAsync($"{Base}/me")).GetProperty("user").GetProperty("email").GetString()!;
        await ErasureKit.InviteAndAcceptAsync(org.Admin, platformEmail, roleId, platform, org.TenantId);

        var protectedResponse = await platform.SuspendRawAsync(org.TenantId, "deneme", "readOnly");
        await protectedResponse.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");

        // Yöneticinin üyeliği pasifleşince koruma biter.
        (await factory.SqlAsync("UPDATE identity.memberships SET is_active = FALSE WHERE tenant_id = @t AND user_id = @u", ("t", org.TenantId), ("u", adminId))).ShouldBe(1);
        var allowed = await platform.SuspendRawAsync(org.TenantId, "deneme", "readOnly");
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await allowed.Content.ReadAsStringAsync(Ct));
    }

    // ---- L3: başarısız imhayı yeniden dene ---------------------------------------------------------------------------------

    [Fact]
    public async Task RetryDeletion_IsOnlyForFailedRequests_RequiresStepUp_ResetsAttempts_AndIsAudited()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync(Token("rty") + " yeniden");
        await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", await platform.DeletionBodyAsync(org.TenantId, "x", 7), HttpStatusCode.OK);

        // scheduled: yeniden denenecek bir şey yok.
        var notFailed = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request/retry", new { currentPassword = PlatformPassword });
        await notFailed.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.deletion_not_retryable");

        (await factory.SqlAsync("UPDATE platform.deletion_requests SET status = 'failed', attempts = 10, last_error = 'platform-tombstone: erasure.verification_failed: sales.leads=3' WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);

        var noPassword = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request/retry", new { });
        await noPassword.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_required");
        var wrong = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request/retry", new { currentPassword = "Yanlis.Parola.3" });
        await wrong.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_failed");
        (await factory.ScalarAsync<int>("SELECT attempts FROM platform.deletion_requests WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(10);

        // lastError konsolda görünür (kişisel veri yok: kod + tablo/sayı).
        var detail = await platform.DetailAsync(org.TenantId);
        detail.GetProperty("deletion").GetProperty("lastError").GetString()!.ShouldContain("erasure.verification_failed");

        var ok = await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request/retry", new { currentPassword = PlatformPassword });
        ok.StatusCode.ShouldBe(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync(Ct));
        (await factory.ScalarAsync<int>("SELECT attempts FROM platform.deletion_requests WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(0);
        (await factory.AuditCountAsync(org.TenantId, "deletion.retried")).ShouldBe(1L);
    }

    // ---- M6: platform yöneticisi yaşam döngüsü ----------------------------------------------------------------------------

    [Fact]
    public async Task PlatformAdmins_CanBeListed_AndRevoked_WithStepUp_TheirSessionsClose_AndTheLastActiveAdminIsProtected()
    {
        var first = await factory.PlatformAdminAsync();
        var second = await factory.PlatformAdminAsync();
        var (secondId, secondEmail, _) = await second.WhoAmIAsync();

        var list = await first.GetJsonAsync($"{PlatformBase}/admins");
        list.EnumerateArray().Select(a => a.Str("email")).ShouldContain(secondEmail);

        // Step-up zorunlu.
        var noPassword = await first.SendRawAsync(HttpMethod.Post, $"{PlatformBase}/admins/{secondId}/revoke", new { });
        await noPassword.ProblemBodyAsync(HttpStatusCode.UnprocessableEntity, "platform.step_up_required");

        // Refresh token'lı bir oturum aç: geri almada kapanmalı.
        var login = await factory.CreateClient().LoginAsync(secondEmail, PlatformPassword);
        var revoke = await first.SendRawAsync(HttpMethod.Post, $"{PlatformBase}/admins/{secondId}/revoke", new { currentPassword = PlatformPassword, deactivate = false });
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent, await revoke.Content.ReadAsStringAsync(Ct));

        // Yetki hemen düşer (bayrak her istekte veritabanından doğrulanır) ve refresh token'lar iptal edilir.
        (await second.GetAsync($"{PlatformBase}/organizations", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var refreshed = await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken = login.RefreshToken }, Ct);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE action = 'platform_admin.revoked' AND details->>'userId' = @u", ("u", secondId.ToString()))).ShouldBe(1L);

        // İkinci kez: artık yönetici değil.
        var again = await first.SendRawAsync(HttpMethod.Post, $"{PlatformBase}/admins/{secondId}/revoke", new { currentPassword = PlatformPassword });
        await again.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.not_a_platform_admin");
    }

    [Fact]
    public async Task TheLastActivePlatformAdmin_CannotBeRevoked_OrDeactivated()
    {
        // Diğer testler de yönetici açtığından "tek yönetici" durumu SQL ile kurulur: yalnız bu hesap aktif kalır, diğerleri pasifleşir ve test sonunda geri açılır.
        var only = await factory.PlatformAdminAsync();
        var (onlyId, _, _) = await only.WhoAmIAsync();
        var others = await factory.ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE is_platform_admin AND is_active AND id <> @u", ("u", onlyId));
        (await factory.SqlAsync("UPDATE identity.users SET is_active = FALSE WHERE is_platform_admin AND is_active AND id <> @u", ("u", onlyId))).ShouldBe((int)others);
        try
        {
            var revoke = await only.SendRawAsync(HttpMethod.Post, $"{PlatformBase}/admins/{onlyId}/revoke", new { currentPassword = PlatformPassword });
            await revoke.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.last_platform_admin");
            var deactivate = await only.SendRawAsync(HttpMethod.Post, $"{PlatformBase}/admins/{onlyId}/revoke", new { currentPassword = PlatformPassword, deactivate = true });
            await deactivate.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.last_platform_admin");
            (await factory.ScalarAsync<bool>("SELECT is_platform_admin AND is_active FROM identity.users WHERE id = @u", ("u", onlyId))).ShouldBeTrue();
        }
        finally
        {
            await factory.SqlAsync("UPDATE identity.users SET is_active = TRUE WHERE is_platform_admin AND NOT is_active");
        }
    }

    // ---- yardımcılar ---------------------------------------------------------------------------------------------------

    private Task<string> AccountStatusAsync(Guid tenantId) =>
        factory.ScalarAsync<string>("SELECT status FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId));

    private async Task AssertUntouchedAsync(Guid tenantId)
    {
        (await AccountStatusAsync(tenantId)).ShouldBe("active");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.deletion_requests WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(0L);
        (await factory.AuditCountAsync(tenantId, "deletion.requested")).ShouldBe(0L);
        (await factory.PlatformEventsAsync(tenantId, "TenantDeletionRequested")).ShouldBeEmpty();
    }
}
