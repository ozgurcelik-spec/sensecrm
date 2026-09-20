using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// KVKK kiracı imhası (M7), yaşam döngüsü testleri: zamanlama (sahte saat), iptal, <c>pending_deletion</c> erişim reddi, sistem kiracısı koruması,
/// yarım kalma/yeniden deneme/en çok deneme ve saklama günü sınırları. Kapsam (hangi satırlar gider/kalır) testleri <c>TenantErasureApiTests</c>'tedir.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantErasureLifecycleApiTests(CrmApiFactory factory)
{
    private static readonly string[] BusinessReadPaths =
    [
        "accounts", "contacts", "leads", "deals", "pipelines", "activities", "campaigns", "products", "quotes", "orders", "cases",
        "workflows/rules", "workflows/executions", "approvals?mine=true", "organization/members", "organization/roles",
    ];

    // ---- Zamanlama ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Timing_NothingIsTouchedBeforeScheduledFor_AndTheJobRunsExactlyAtTheBoundary()
    {
        using var s = await ErasureScenario.CreateAsync(factory);
        var info = await s.RequestDeletionAsync();
        info.ScheduledFor.ShouldBe(s.Clock.GetUtcNow().AddDays(7));
        var pending = ErasureKit.Format(await s.CountsAsync(s.A.TenantId));
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");

        async Task AssertUntouchedAsync()
        {
            (await s.RequestStatusAsync()).ShouldBe("scheduled");
            (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");
            ErasureKit.Format(await s.CountsAsync(s.A.TenantId)).ShouldBe(pending);
            (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.A.TenantId))).ShouldBe(1L);
        }

        await s.RunJobAsync();
        await AssertUntouchedAsync();

        s.Clock.SetUtcNow(info.ScheduledFor.AddDays(-3));
        await s.RunJobAsync();
        await AssertUntouchedAsync();

        s.Clock.SetUtcNow(info.ScheduledFor.AddSeconds(-1));
        await s.RunJobAsync();
        await AssertUntouchedAsync();

        s.Clock.SetUtcNow(info.ScheduledFor);
        await s.RunJobAsync();
        await s.AssertCompletedAsync();
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("deleted");
        ErasureKit.Format((await s.CountsAsync(s.A.TenantId, ignoreErasedEvent: true)).Where(c => c.Value != 0)).ShouldBeEmpty();
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(ErasureKit.Format(s.BBefore));
    }

    // ---- İptal -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_WithinTheWaitingPeriod_ReturnsTheTenantToItsPreviousStatus_AndTheJobNeverErasesIt()
    {
        using var s = await ErasureScenario.CreateAsync(factory);
        await s.RequestDeletionAsync();
        await s.CancelDeletionAsync();

        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("active");
        (await s.RequestStatusAsync()).ShouldBe("cancelled");
        (await s.Factory.ScalarAsync<bool>("SELECT cancelled_at IS NOT NULL AND completed_at IS NULL FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldBeTrue();
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = 'deletion.cancelled'", ("t", s.A.TenantId))).ShouldBe(1L);

        // Kiracı hemen yeniden çalışır.
        (await s.A.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
        var afterCancel = ErasureKit.Format(await s.CountsAsync(s.A.TenantId));

        s.AdvancePastSchedule();
        s.Clock.Advance(TimeSpan.FromDays(30));
        await s.RunJobAsync();

        (await s.RequestStatusAsync()).ShouldBe("cancelled");
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("active");
        ErasureKit.Format(await s.CountsAsync(s.A.TenantId)).ShouldBe(afterCancel);
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.A.TenantId))).ShouldBe(1L);

        // İptal sonrası yeni talep açılabilir (kiracı başına tek etkin talep kuralı yalnız etkin talepleri sayar).
        await s.RequestDeletionAsync();
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");
    }

    [Fact]
    public async Task Cancel_OfASuspendedTenant_RestoresTheSuspendedStatus()
    {
        using var s = await ErasureScenario.CreateAsync(factory);
        await s.Platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{s.A.TenantId}/suspend", new { reason = "Odeme gecikti", mode = "readOnly" }, HttpStatusCode.NoContent);
        await s.RequestDeletionAsync();
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");
        (await s.Factory.ScalarAsync<string>("SELECT previous_status FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldBe("suspended");

        await s.CancelDeletionAsync();

        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("suspended");
        s.AdvancePastSchedule();
        await s.RunJobAsync();
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("suspended");
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.A.TenantId))).ShouldBe(1L);
    }

    [Fact]
    public async Task Cancel_IsRejected_AfterTheWaitingPeriod_WhileRunningOrFailed_AndAfterCompletion()
    {
        var probe = new ErasureProbe { FailuresLeft = int.MaxValue };
        using var s = await ErasureScenario.CreateAsync(factory, services: services => services.AddProbeErasers(probe));
        probe.Tenant = s.A.TenantId;
        await s.RequestDeletionAsync();

        // Bekleme süresi doldu (iş henüz koşmadı): iptal edilemez.
        s.AdvancePastSchedule();
        await AssertNotCancellableAsync(s);
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");

        // Adım hatası: talep failed; iptal edilemez.
        await s.RunJobAsync();
        (await s.RequestStatusAsync()).ShouldBe("failed");
        await AssertNotCancellableAsync(s);

        // Çalışıyor: iptal edilemez.
        await s.Factory.SqlAsync("UPDATE platform.deletion_requests SET status = 'running' WHERE id = @r", ("r", s.Request.RequestId));
        await AssertNotCancellableAsync(s);
        await s.Factory.SqlAsync("UPDATE platform.deletion_requests SET status = 'failed' WHERE id = @r", ("r", s.Request.RequestId));

        // Hata giderildi, iş tamamlar; tamamlandıktan sonra iptal edilemez ve kiracı geri gelmez.
        probe.FailuresLeft = 0;
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await s.RunJobAsync();
        await s.AssertCompletedAsync();
        await AssertNotCancellableAsync(s);
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("deleted");
    }

    // ---- pending_deletion erişim reddi -------------------------------------------------------------------------------

    [Fact]
    public async Task PendingDeletionTenant_IsDeniedOnEveryEndpointImmediately_AndCannotLogIn()
    {
        using var s = await ErasureScenario.CreateAsync(factory);
        (await s.A.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);

        await s.RequestDeletionAsync();

        // Saat ilerlemeden, iş koşmadan: kiracının her ucu 403 tenant.suspended (reason = pending_deletion).
        foreach (var path in BusinessReadPaths)
        {
            await AssertPendingDeletionAsync(await s.A.Admin.GetAsync($"{Base}/{path}", Ct), $"GET {path}");
        }

        await AssertPendingDeletionAsync(await s.A.Admin.PostAsJsonAsync($"{Base}/accounts", new { name = "Yeni firma" }, Ct), "POST accounts");

        // Muaf uçlar (kullanıcı düzeyi/durumu gösteren): /me ve /subscription durumu bildirir.
        (await s.A.Admin.GetJsonAsync($"{Base}/me")).GetProperty("subscription").GetProperty("status").GetString().ShouldBe("pending_deletion");
        var subscription = await s.A.Admin.GetJsonAsync($"{Base}/subscription");
        subscription.GetProperty("status").GetString().ShouldBe("pending_deletion");
        subscription.GetProperty("accessLevel").GetString().ShouldBe("none");

        // Yalnız A'ya üye hesaplar giriş yapamaz.
        foreach (var email in new[] { s.A.AdminEmail, s.AMember.Email })
        {
            await AssertPendingDeletionAsync(await s.Host.CreateClient().PostAsJsonAsync($"{Base}/auth/login", new { email, password = DefaultPassword }, Ct), $"login {email}");
        }

        // B'ye de üye olan ortak hesap etkilenmez: engelli A atlanır, B'ye giriş yapar.
        var shared = s.Host.CreateClient();
        shared.WithToken((await shared.LoginAsync(s.Shared.Email)).AccessToken);
        (await shared.GetJsonAsync($"{Base}/me")).GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(s.B.TenantId);

        // B'nin kendi yöneticisi ve platform konsolu etkilenmez.
        (await s.B.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
        (await s.Platform.GetJsonAsync($"{PlatformBase}/organizations/{s.A.TenantId}")).GetProperty("status").GetString().ShouldBe("pending_deletion");
    }

    // ---- Sistem kiracısı ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SystemTenant_CannotBeDeletionRequested_AndTheJobNeverErasesAForcedRequestForIt()
    {
        using var s = await ErasureScenario.CreateAsync(factory);
        var system = await s.NewOrgAsync($"Sistem {s.Tag}");
        await system.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/accounts", new { name = $"Sistem firmasi {s.Tag}" }, HttpStatusCode.Created);
        await s.Host.SetAccountAsync(s.Factory, system.TenantId, "is_system = TRUE");

        var response = await s.Platform.PostAsJsonAsync($"{PlatformBase}/organizations/{system.TenantId}/deletion-request", new { reason = "Deneme", retentionDays = 7 }, Ct);
        await response.ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");
        (await s.CountAsync("SELECT count(*) FROM platform.deletion_requests WHERE tenant_id = @t", ("t", system.TenantId))).ShouldBe(0L);
        (await s.AccountStatusAsync(system.TenantId)).ShouldBe("active");

        // Doğrudan SQL ile zamanı gelmiş bir talep zorlanır: iş sistem hesabını atlar, hiçbir veri silinmez.
        var before = ErasureKit.Format(await s.CountsAsync(system.TenantId));
        var now = DateTime.UtcNow;
        var requestId = Guid.NewGuid();
        await s.Factory.SqlAsync(
            """
            INSERT INTO platform.deletion_requests (id, tenant_id, requested_at, reason, retention_days, scheduled_for, status, previous_status, attempts, erased_steps, created_at)
            VALUES (@id, @t, @requested, 'forced', 7, @scheduled, 'scheduled', 'active', 0, '{}', @requested)
            """,
            ("id", requestId), ("t", system.TenantId), ("requested", now.AddDays(-8)), ("scheduled", now.AddDays(-1)));
        s.Clock.SetUtcNow(new DateTimeOffset(now.AddDays(2), TimeSpan.Zero));

        await s.RunJobAsync();
        await s.RunJobAsync();

        ErasureKit.Format(await s.CountsAsync(system.TenantId)).ShouldBe(before);
        (await s.AccountStatusAsync(system.TenantId)).ShouldBe("active");
        (await s.Factory.ScalarAsync<string>("SELECT status FROM platform.deletion_requests WHERE id = @id", ("id", requestId))).ShouldBe("scheduled");
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", system.TenantId))).ShouldBe(1L);
        (await s.CountAsync("SELECT count(*) FROM identity.users WHERE id = @u", ("u", system.AdminUserId))).ShouldBe(1L);
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action LIKE 'deletion.%'", ("t", system.TenantId))).ShouldBe(0L);
    }

    // ---- Yarım kalma / yeniden deneme --------------------------------------------------------------------------------

    [Fact]
    public async Task PartialFailure_ResumesWithTheRemainingSteps_AndDoesNotRerunCompletedOnes()
    {
        var probe = new ErasureProbe { FailuresLeft = 1 };
        using var s = await ErasureScenario.CreateAsync(factory, services: services => services.AddProbeErasers(probe));
        probe.Tenant = s.A.TenantId;
        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();

        // 1. koşu: 700 numaralı adım hata verir → failed, attempts = 1, ilerleme erased_steps'te.
        await s.RunJobAsync();
        (await s.RequestStatusAsync()).ShouldBe("failed");
        (await s.RequestAttemptsAsync()).ShouldBe(1);
        (await s.Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldContain("InvalidOperationException");
        var completedSoFar = await s.ErasedStepsAsync();
        completedSoFar.ShouldContain("identity-accounts");
        completedSoFar.ShouldContain("test-before");
        completedSoFar.ShouldContain("workflows-conductor");
        completedSoFar.ShouldContain("module:sales");
        completedSoFar.ShouldNotContain("test-flaky");
        completedSoFar.ShouldNotContain("identity-tenant");
        completedSoFar.ShouldNotContain("audit");
        completedSoFar.ShouldNotContain(TenantErasureJob.TombstoneStep);
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.A.TenantId))).ShouldBe(1L);
        (await s.CountAsync("SELECT count(*) FROM sales.accounts WHERE tenant_id = @t", ("t", s.A.TenantId))).ShouldBe(0L, "tamamlanan modül adımı verisi silinmiş kalır");

        // Üstel bekleme: aynı anda yeniden koşmak talebi almaz.
        await s.RunJobAsync();
        (await s.RequestAttemptsAsync()).ShouldBe(1);
        probe.Calls("test-flaky").ShouldBe(1);

        // Bekleme geçince kalan adımlarla tamamlanır; tamamlananlar yeniden koşmaz.
        s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.RunJobAsync();

        await s.AssertCompletedAsync();
        (await s.RequestAttemptsAsync()).ShouldBe(1);
        probe.Calls("test-before").ShouldBe(1, "tamamlanan adım yeniden koşmamalı");
        probe.Calls("test-flaky").ShouldBe(2);
        (await s.ErasedStepsAsync()).Where(ErasureKit.ExpectedSteps.Contains).ToArray().ShouldBe(ErasureKit.ExpectedSteps);
        ErasureKit.Format((await s.CountsAsync(s.A.TenantId, ignoreErasedEvent: true)).Where(c => c.Value != 0)).ShouldBeEmpty();
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(ErasureKit.Format(s.BBefore));
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = 'deletion.failed'", ("t", s.A.TenantId))).ShouldBe(0L);
        (await s.Factory.ScalarAsync<bool>("SELECT last_error IS NULL FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldBeTrue();
    }

    [Fact]
    public async Task AfterMaxAttempts_TheRequestStaysFailed_AndADeletionFailedAuditRowIsWritten()
    {
        var probe = new ErasureProbe { FailuresLeft = int.MaxValue };
        using var s = await ErasureScenario.CreateAsync(
            factory,
            settings: new Dictionary<string, string> { ["Platform:Deletion:MaxAttempts"] = "2" },
            services: services => services.AddProbeErasers(probe));
        probe.Tenant = s.A.TenantId;
        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();

        await s.RunJobAsync();
        (await s.RequestAttemptsAsync()).ShouldBe(1);
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = 'deletion.failed'", ("t", s.A.TenantId))).ShouldBe(0L, "yalnız son deneme sonrası");

        s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.RunJobAsync();
        (await s.RequestAttemptsAsync()).ShouldBe(2);
        (await s.RequestStatusAsync()).ShouldBe("failed");
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = 'deletion.failed'", ("t", s.A.TenantId))).ShouldBe(1L);
        probe.Calls("test-flaky").ShouldBe(2);

        // Üçüncü koşu: deneme hakkı bitti; talep kuyruğa alınmaz, yeni denetim satırı yazılmaz.
        s.Clock.Advance(TimeSpan.FromHours(2));
        await s.RunJobAsync();
        await s.RunJobAsync();
        (await s.RequestStatusAsync()).ShouldBe("failed");
        (await s.RequestAttemptsAsync()).ShouldBe(2);
        probe.Calls("test-flaky").ShouldBe(2);
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = 'deletion.failed'", ("t", s.A.TenantId))).ShouldBe(1L);
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.A.TenantId))).ShouldBe(1L);
        (await s.Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldNotBeNullOrWhiteSpace();
    }

    // ---- Saklama günü sınırları --------------------------------------------------------------------------------------

    [Fact]
    public async Task RetentionDays_AreValidatedBetween7And90_AndDefaultTo30()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.None);
        var url = $"{PlatformBase}/organizations/{s.A.TenantId}/deletion-request";

        foreach (var days in new[] { 6, 91, 0, -1 })
        {
            var response = await s.Platform.PostAsJsonAsync(url, new { reason = "Sinir", retentionDays = days }, Ct);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
            errors.EnumerateObject().Any(p => p.Name.Equals("retentionDays", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue(errors.GetRawText());
        }

        var noReason = await s.Platform.PostAsJsonAsync(url, new { reason = "", retentionDays = 7 }, Ct);
        await noReason.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("active", "geçersiz talep hiçbir şeyi değiştirmez");

        (await s.RequestDeletionAsync(7)).ScheduledFor.ShouldBe(s.Clock.GetUtcNow().AddDays(7));
        await s.CancelDeletionAsync();
        (await s.RequestDeletionAsync(90)).ScheduledFor.ShouldBe(s.Clock.GetUtcNow().AddDays(90));
        await s.CancelDeletionAsync();
        (await s.RequestDeletionAsync(null)).ScheduledFor.ShouldBe(s.Clock.GetUtcNow().AddDays(30));
    }

    // ---- Yardımcılar -------------------------------------------------------------------------------------------------

    private static async Task AssertNotCancellableAsync(ErasureScenario s)
    {
        var problem = await s.CancelDeletionAsync(HttpStatusCode.Conflict);
        problem.GetProperty("code").GetString().ShouldBe("platform.deletion_not_cancellable");
    }

    private static async Task AssertPendingDeletionAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{what}: {body}");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe("tenant.suspended", $"{what}: {body}");
        json.RootElement.GetProperty("args").GetProperty("reason").GetString().ShouldBe("pending_deletion", $"{what}: {body}");
    }
}
