using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// KVKK kiracı imhası (M7), kapsam testleri: iki kiracı (A silinecek, B kalacak) + ortak hesap + yalnız A'ya ait hesaplar + A'da üye platform yöneticisi;
/// yedi iş modülünde gerçek HTTP uçlarıyla üretilmiş veri (yumuşak silinenler, outbox, denetim, Conductor yürütmeleri). Tablo listesi EF modelinden
/// yansımayla üretilir, bu yüzden yeni tablo/modül otomatik kapsanır. Zamanlama/iptal/hata/yeniden deneme testleri <c>TenantErasureLifecycleApiTests</c>'tedir.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantErasureApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Erasure_LeavesNoRowOfTheTenantInAnyTable_KeepsTheOtherTenantIdentical_AndRemovesTheConductorExecutions()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Full);
        var seeded = await s.CountsAsync(s.A.TenantId);
        var engineIdsOfA = await s.EngineIdsAsync(s.A.TenantId);
        var engineIdsOfB = await s.EngineIdsAsync(s.B.TenantId);

        // Önkoşullar: sıfır beklentisi boş geçmesin — her iş modülünde, outbox'ta, denetimde, yumuşak silinenlerde ve motorda A verisi var.
        s.Tables.Count.ShouldBeGreaterThan(20);
        foreach (var name in new[] { "sales.accounts", "commerce.quote_lines", "commerce.sales_order_lines", "service.case_events", "identity.memberships", "audit.audit_log_entries", "workflows.outbox_messages" })
        {
            seeded.ShouldContainKey(name);
        }

        foreach (var schema in ErasureKit.BusinessSchemas)
        {
            seeded.Where(c => c.Key.StartsWith(schema + ".", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBeGreaterThan(0L, $"{schema} modülünde A verisi yok");
        }

        seeded["audit.audit_log_entries"].ShouldBeGreaterThan(0L);
        seeded["sales.outbox_messages"].ShouldBeGreaterThan(0L);
        (await s.CountAsync("SELECT count(*) FROM sales.accounts WHERE tenant_id = @t AND is_deleted", ("t", s.A.TenantId))).ShouldBeGreaterThan(0L);
        (await s.CountAsync("SELECT count(*) FROM commerce.products WHERE tenant_id = @t AND is_deleted", ("t", s.A.TenantId))).ShouldBeGreaterThan(0L);
        engineIdsOfA.Count.ShouldBeGreaterThanOrEqualTo(2);
        engineIdsOfB.Count.ShouldBeGreaterThanOrEqualTo(2);

        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        await s.RunJobAsync();

        await s.AssertCompletedAsync();

        // (1) A için her ITenantEntity tablosunda, outbox'larda ve audit.audit_log_entries'te satır yok (TenantErased kanıt olayı hariç).
        var remaining = (await s.CountsAsync(s.A.TenantId, ignoreErasedEvent: true)).Where(c => c.Value != 0);
        ErasureKit.Format(remaining).ShouldBeEmpty("A'nın satırı kalan tablolar");

        // (2) B'nin tablo başına satır sayıları imhadan önceki anlık görüntüyle birebir.
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(ErasureKit.Format(s.BBefore));

        // (4) identity.tenants: A yok, B var.
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.A.TenantId))).ShouldBe(0L);
        (await s.CountAsync("SELECT count(*) FROM identity.tenants WHERE id = @t", ("t", s.B.TenantId))).ShouldBe(1L);

        // (12) Conductor: A'nın her yürütme kimliği için motora Remove çağrıldı; B'ninkilere dokunulmadı.
        s.Engine.RemovedWorkflowIds.ShouldBeSubsetOf(engineIdsOfA.Concat(engineIdsOfB).ToList());
        foreach (var engineId in engineIdsOfA)
        {
            s.Engine.RemovedWorkflowIds.ShouldContain(engineId);
        }

        foreach (var engineId in engineIdsOfB)
        {
            s.Engine.RemovedWorkflowIds.ShouldNotContain(engineId);
            s.Engine.Workflows.ShouldContainKey(engineId);
        }

        // B'nin yöneticisi hâlâ çalışır durumda ve kendi verisini görür.
        (await s.B.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ChunkedDeletion_WithATinyChunkSize_DeletesEverything_AndTheReportMatchesTheRowCounts()
    {
        using var s = await ErasureScenario.CreateAsync(
            factory,
            SeedLevel.Full,
            new Dictionary<string, string> { ["Platform:Deletion:ChunkSize"] = "2" });
        await s.RequestDeletionAsync();
        var beforeErasure = await s.CountsAsync(s.A.TenantId);

        // Parça boyutundan (2) büyük tablolar var: silme birden çok tur döner.
        beforeErasure.Values.Count(v => v > 2).ShouldBeGreaterThanOrEqualTo(3);

        s.AdvancePastSchedule();
        await s.RunJobAsync();

        await s.AssertCompletedAsync();
        ErasureKit.Format((await s.CountsAsync(s.A.TenantId, ignoreErasedEvent: true)).Where(c => c.Value != 0)).ShouldBeEmpty();
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(ErasureKit.Format(s.BBefore));

        // Rapor: tablo başına silinen satır sayıları, imhadan hemen önceki gerçek satır sayılarının toplamıyla (tüm adımlar üzerinden) birebir.
        var totals = await s.ReportTotalsAsync();
        foreach (var (table, count) in beforeErasure.Where(c => c.Value > 0))
        {
            totals.GetValueOrDefault(table).ShouldBe(count, $"rapor sayısı ({table})");
        }
    }

    [Fact]
    public async Task Accounts_OnlyThoseWithoutAnyOtherMembership_AreErased_TheSharedAndThePlatformAdminAccountsSurvive()
    {
        using var s = await ErasureScenario.CreateAsync(factory);
        (await s.DetachPlatformAdminFromOtherOrganizationsAsync()).ShouldBe(1);

        // Önkoşullar.
        (await MembershipCountAsync(s, s.Shared.UserId)).ShouldBe(2L);
        (await MembershipCountAsync(s, s.PlatformAdminUserId)).ShouldBe(1L);
        foreach (var userId in new[] { s.A.AdminUserId, s.AMember.UserId })
        {
            (await s.CountAsync("SELECT count(*) FROM identity.refresh_tokens WHERE user_id = @u", ("u", userId))).ShouldBeGreaterThan(0L);
        }

        (await s.CountAsync("SELECT count(*) FROM identity.refresh_tokens WHERE user_id = @u AND organization_id = @o", ("u", s.Shared.UserId), ("o", s.A.TenantId))).ShouldBeGreaterThan(0L);

        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        await s.RunJobAsync();
        await s.AssertCompletedAsync();

        // Yalnız A'ya ait hesaplar ve refresh token'ları gitti.
        foreach (var userId in new[] { s.A.AdminUserId, s.AMember.UserId })
        {
            (await s.CountAsync("SELECT count(*) FROM identity.users WHERE id = @u", ("u", userId))).ShouldBe(0L);
            (await s.CountAsync("SELECT count(*) FROM identity.refresh_tokens WHERE user_id = @u", ("u", userId))).ShouldBe(0L);
        }

        // Ortak hesap yaşıyor; yalnız B üyeliği kaldı; A'ya bağlı refresh token'ları gitti.
        (await s.CountAsync("SELECT count(*) FROM identity.users WHERE id = @u", ("u", s.Shared.UserId))).ShouldBe(1L);
        (await MembershipCountAsync(s, s.Shared.UserId)).ShouldBe(1L);
        (await s.CountAsync("SELECT count(*) FROM identity.memberships WHERE user_id = @u AND tenant_id = @t", ("u", s.Shared.UserId), ("t", s.B.TenantId))).ShouldBe(1L);
        (await s.CountAsync("SELECT count(*) FROM identity.refresh_tokens WHERE user_id = @u AND organization_id = @o", ("u", s.Shared.UserId), ("o", s.A.TenantId))).ShouldBe(0L);

        // Platform yöneticisi yalnız A'da üyeydi: hesabı yaşıyor (yönetici bayrağıyla), A üyeliği gitti.
        (await s.CountAsync("SELECT count(*) FROM identity.users WHERE id = @u AND is_platform_admin", ("u", s.PlatformAdminUserId))).ShouldBe(1L);
        (await MembershipCountAsync(s, s.PlatformAdminUserId)).ShouldBe(0L);

        // B'nin hesapları etkilenmedi.
        (await s.CountAsync("SELECT count(*) FROM identity.users WHERE id = @u", ("u", s.B.AdminUserId))).ShouldBe(1L);

        // Ortak hesap B'ye giriş yapabilir (saat gerçek saate döner: jeton doğrulaması gerçek saatle); erişimi yalnız B'dir. A'nın yöneticisi giremez.
        s.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        var client = s.Host.CreateClient();
        client.WithToken((await client.LoginAsync(s.Shared.Email)).AccessToken);
        (await client.GetJsonAsync($"{Base}/me")).GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(s.B.TenantId);

        var denied = await s.Host.CreateClient().PostAsJsonAsync($"{Base}/auth/login", new { email = s.A.AdminEmail, password = DefaultPassword }, Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tombstone_KeepsProofWithoutPersonalData_AndTheAuditTrailIsRedacted()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        var tenantId = s.A.TenantId;
        await s.Host.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);
        (await s.CountAsync("SELECT count(*) FROM platform.usage_snapshots WHERE tenant_id = @t", ("t", tenantId))).ShouldBeGreaterThan(0L);
        var snapshotsOfB = await s.CountAsync("SELECT count(*) FROM platform.usage_snapshots WHERE tenant_id = @t", ("t", s.B.TenantId));
        snapshotsOfB.ShouldBeGreaterThan(0L);

        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        await s.RunJobAsync();

        // Hesap mezar taşı.
        (await s.AccountStatusAsync(tenantId)).ShouldBe("deleted");
        (await s.Factory.ScalarAsync<string>("SELECT name FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId))).ShouldBe("[deleted]");
        (await s.Factory.ScalarAsync<string>("SELECT slug FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId))).ShouldBe("deleted-" + tenantId.ToString("N")[^8..]);
        (await s.Factory.ScalarAsync<bool>("SELECT deleted_at IS NOT NULL AND overrides IS NULL FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId))).ShouldBeTrue();
        (await s.CountAsync("SELECT count(*) FROM platform.usage_snapshots WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(0L);
        (await s.CountAsync("SELECT count(*) FROM platform.usage_snapshots WHERE tenant_id = @t", ("t", s.B.TenantId))).ShouldBe(snapshotsOfB);
        (await s.AccountStatusAsync(s.B.TenantId)).ShouldBe("active");
        (await s.Factory.ScalarAsync<string>("SELECT name FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", s.B.TenantId))).ShouldBe(s.B.Name);

        // Talep: tamamlandı; adımlar (Order sırasıyla) ve rapor (sıfır olmayan sayılar, kişisel veri yok).
        await s.AssertCompletedAsync();
        (await s.Factory.ScalarAsync<bool>("SELECT completed_at IS NOT NULL AND last_error IS NULL FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldBeTrue();
        (await s.ErasedStepsAsync()).Where(ErasureKit.ExpectedSteps.Contains).ToArray().ShouldBe(ErasureKit.ExpectedSteps);

        var report = await s.ReportAsync();
        report.ValueKind.ShouldBe(JsonValueKind.Object);
        var totals = await s.ReportTotalsAsync();
        totals.Values.Sum().ShouldBeGreaterThan(0L);
        totals["sales.accounts"].ShouldBeGreaterThan(0L);
        totals["identity.memberships"].ShouldBeGreaterThan(0L);
        totals["audit.audit_log_entries"].ShouldBeGreaterThan(0L);
        totals["identity.tenants"].ShouldBe(1L);
        var rawReport = report.GetRawText();
        foreach (var marker in s.PersonalMarkers)
        {
            rawReport.Contains(marker, StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"rapor kişisel veri içeriyor: {marker}");
        }

        // Platform denetimi: satırlar duruyor, kiracı adı redakte; imha/istek eylemleri var; ad/e-posta işareti yok.
        var actions = await s.Factory.ErQueryAsync("SELECT action FROM platform.platform_audit_entries WHERE target_tenant_id = @t", ("t", tenantId));
        actions.ShouldContain("deletion.requested");
        actions.Count(a => a == "deletion.completed").ShouldBe(1);
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND target_tenant_name IS DISTINCT FROM '[deleted]'", ("t", tenantId))).ShouldBe(0L);
        var details = string.Concat(await s.Factory.ErQueryAsync("SELECT details::text FROM platform.platform_audit_entries WHERE target_tenant_id = @t", ("t", tenantId)));
        foreach (var marker in new[] { s.Tag, s.A.AdminEmail, s.AMember.Email })
        {
            details.Contains(marker, StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"platform denetimi kişisel veri içeriyor: {marker}");
        }

        // TenantErased olayı outbox'ta kanıt olarak kalır.
        (await s.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = @t AND type LIKE '%TenantErased'", ("t", tenantId))).ShouldBe(1L);
    }

    [Fact]
    public async Task Erasure_IsIdempotent_TheSecondRunIsANoOp_AndNeverTouchesTheTombstone()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        await s.RunJobAsync();
        await s.AssertCompletedAsync();

        var accountJson = await s.RowJsonAsync("platform.tenant_accounts", "tenant_id", s.A.TenantId);
        var requestJson = await s.RowJsonAsync("platform.deletion_requests", "id", s.Request.RequestId);
        var auditRows = await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t", ("t", s.A.TenantId));
        var outboxRows = await s.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = @t", ("t", s.A.TenantId));
        var bAfterFirstRun = ErasureKit.Format(await s.CountsAsync(s.B.TenantId));

        s.Clock.Advance(TimeSpan.FromHours(3));
        await s.RunJobAsync();
        await s.RunJobAsync();

        (await s.RowJsonAsync("platform.tenant_accounts", "tenant_id", s.A.TenantId)).ShouldBe(accountJson);
        (await s.RowJsonAsync("platform.deletion_requests", "id", s.Request.RequestId)).ShouldBe(requestJson);
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t", ("t", s.A.TenantId))).ShouldBe(auditRows);
        (await s.CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = @t", ("t", s.A.TenantId))).ShouldBe(outboxRows);
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = 'deletion.completed'", ("t", s.A.TenantId))).ShouldBe(1L);
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(bAfterFirstRun);
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(ErasureKit.Format(s.BBefore));
    }

    [Fact]
    public async Task DeletedTenantsReplay_AfterABackupRestore_RemovesTheReappearedRowsAgain_AndLeavesTheOtherTenantAlone()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        await s.RunJobAsync();
        await s.AssertCompletedAsync();
        var tombstone = await s.RowJsonAsync("platform.tenant_accounts", "tenant_id", s.A.TenantId);
        var requestRow = await s.RowJsonAsync("platform.deletion_requests", "id", s.Request.RequestId);

        // Yedekten geri yükleme benzetimi: A'nın satırları (hesap, kişi, outbox, denetim) B'nin satırlarından çoğaltılarak yeniden görünür.
        var restored = 0;
        foreach (var (schema, table) in new[] { ("sales", "accounts"), ("sales", "contacts"), ("sales", "outbox_messages"), ("audit", "audit_log_entries") })
        {
            restored += await s.Factory.CloneTenantRowsAsync(schema, table, s.B.TenantId, s.A.TenantId, limit: 3);
        }

        restored.ShouldBeGreaterThanOrEqualTo(4);
        (await s.CountAsync("SELECT count(*) FROM sales.accounts WHERE tenant_id = @t", ("t", s.A.TenantId))).ShouldBeGreaterThan(0L);
        ErasureKit.Format((await s.CountsAsync(s.A.TenantId, ignoreErasedEvent: true)).Where(c => c.Value != 0)).ShouldNotBeEmpty();

        long removed;
        using (var scope = s.Host.Services.CreateScope())
        {
            removed = await scope.ServiceProvider.GetRequiredService<DeletedTenantsReplay>().RunAsync(Ct);
        }

        removed.ShouldBeGreaterThanOrEqualTo(restored);
        ErasureKit.Format((await s.CountsAsync(s.A.TenantId, ignoreErasedEvent: true)).Where(c => c.Value != 0)).ShouldBeEmpty();
        ErasureKit.Format(await s.CountsAsync(s.B.TenantId)).ShouldBe(ErasureKit.Format(s.BBefore));

        // Mezar taşı ve talep satırı değişmedi; komut idempotent (ikinci koşu bir şey silmez).
        (await s.RowJsonAsync("platform.tenant_accounts", "tenant_id", s.A.TenantId)).ShouldBe(tombstone);
        (await s.RowJsonAsync("platform.deletion_requests", "id", s.Request.RequestId)).ShouldBe(requestRow);
        using (var scope = s.Host.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<DeletedTenantsReplay>().RunAsync(Ct)).ShouldBe(0L);
        }
    }

    private static Task<long> MembershipCountAsync(ErasureScenario s, Guid userId) =>
        s.CountAsync("SELECT count(*) FROM identity.memberships WHERE user_id = @u", ("u", userId));
}
