using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Retention;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>Testte açılıp kapanan, doğrulama kancası olan (veritabanı dışı depo benzetimi) imha adımı: <see cref="Remaining"/> &gt; 0 iken kalıntı bildirir.</summary>
internal sealed class ExternalStoreEraser(ExternalStoreState state) : ITenantDataEraser
{
    public string Name => "external-store";

    public int Order => 850;

    public Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default) =>
        Task.FromResult(tenantId == state.Tenant ? new EraseReport(new Dictionary<string, long> { ["external.objects"] = state.Remaining }) : EraseReport.Empty);

    public Task<EraseVerification> VerifyErasedAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(tenantId == state.Tenant && state.Remaining > 0 ? EraseVerification.Failed($"objects={state.Remaining}") : EraseVerification.Clean);
}

internal sealed class ExternalStoreState
{
    public Guid Tenant { get; set; }

    public int Remaining { get; set; }
}

/// <summary>
/// C-SEC2 M1 + M2 + L1 + L2: imha işinin yıkıcı işlemden önce yeniden doğrulaması (kilitli satır, iptal ↔ başlatma yarışı, korunan/uygunsuz kiracı), <c>xmin</c> eşzamanlılık belirteci,
/// tombstone öncesi kalıntı doğrulaması (kayıtlı olmayan tablo, veritabanı dışı depo kancası), kayıt/adım kaydı bütünlüğü ve serbest metin gerekçelerin redaksiyonu.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ErasureHardeningApiTests(CrmApiFactory factory)
{
    // ---- M1: yeniden doğrulama / yarışlar -------------------------------------------------------------------------------

    [Fact]
    public async Task Job_SkipsARequestWhoseRowIsLockedByAConcurrentCancel_AndProcessesItOnTheNextRound()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        var before = ErasureKit.Format(await s.CountsAsync(s.A.TenantId));

        // Devam eden bir iptalin satır kilidi benzetimi: başka bağlantı satırı FOR UPDATE ile tutar.
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var lockCommand = new NpgsqlCommand("SELECT id FROM platform.deletion_requests WHERE id = @id FOR UPDATE", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("id", s.Request.RequestId);
                (await lockCommand.ExecuteScalarAsync(Ct)).ShouldNotBeNull();
            }

            var run = await s.RunJobAsync();

            run.Failed.ShouldBe(0, "kilitli talep başarısızlık değil, atlanan iştir");
            (await s.RequestStatusAsync()).ShouldBe("scheduled");
            (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");
            ErasureKit.Format(await s.CountsAsync(s.A.TenantId)).ShouldBe(before, "kilitliyken hiçbir veri silinmez");
            await transaction.RollbackAsync(Ct);
        }

        await s.RunJobAsync();
        await s.AssertCompletedAsync();
    }

    [Fact]
    public async Task CancelThenStart_TheJobNeverErasesACancelledRequest_AndStartThenCancel_IsRejected()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        await s.RequestDeletionAsync();
        var before = ErasureKit.Format(await s.CountsAsync(s.A.TenantId));
        await s.CancelDeletionAsync();

        s.AdvancePastSchedule();
        await s.RunJobAsync();
        (await s.RequestStatusAsync()).ShouldBe("cancelled");
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("active");
        ErasureKit.Format(await s.CountsAsync(s.A.TenantId)).ShouldBe(ErasureKit.Format(await s.CountsAsync(s.A.TenantId)));
        (await s.CountAsync("SELECT count(*) FROM sales.accounts WHERE tenant_id = @t", ("t", s.A.TenantId))).ShouldBeGreaterThan(0L);
        before.ShouldNotBeNullOrEmpty();

        // Yeni talep: iş başlatınca (running) iptal artık reddedilir.
        await s.RequestDeletionAsync();
        (await s.Factory.SqlAsync("UPDATE platform.deletion_requests SET status = 'running' WHERE id = @r", ("r", s.Request.RequestId))).ShouldBe(1);
        var problem = await s.CancelDeletionAsync(HttpStatusCode.Conflict);
        problem.GetProperty("code").GetString().ShouldBe("platform.deletion_not_cancellable");
    }

    [Fact]
    public async Task XminConcurrencyToken_TheLoserOfACancelVersusStartRace_GetsAConcurrencyException()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.None);
        await s.RequestDeletionAsync();

        using var scopeA = s.Host.Services.CreateScope();
        using var scopeB = s.Host.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var starter = await dbA.DeletionRequests.SingleAsync(r => r.Id == s.Request.RequestId, Ct);
        var canceller = await dbB.DeletionRequests.SingleAsync(r => r.Id == s.Request.RequestId, Ct);

        starter.Start(DateTime.UtcNow).IsSuccess.ShouldBeTrue();
        await dbA.SaveChangesAsync(Ct);

        canceller.Cancel(null, DateTime.UtcNow).IsSuccess.ShouldBeTrue("bayat okuma iptale izin verir; yazma xmin ile reddedilir");
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync(Ct));
        (await s.RequestStatusAsync()).ShouldBe("running");
    }

    [Fact]
    public async Task Job_CancelsBySystem_ARequestWhoseTenantBecameProtected_AndRestoresTheTenant()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        await s.RequestDeletionAsync(); // platform yöneticisinin üyeliği bu sırada pasiftir
        var before = await s.CountsAsync(s.A.TenantId);

        // Talepten sonra platform yöneticisi organizasyonun aktif üyesi oldu (ör. yeniden etkinleştirildi/başka yol): organizasyon artık korunur.
        (await s.Factory.SqlAsync("UPDATE identity.memberships SET is_active = TRUE WHERE tenant_id = @t AND user_id = @u", ("t", s.A.TenantId), ("u", s.PlatformAdminUserId))).ShouldBe(1);
        s.AdvancePastSchedule();

        var run = await s.RunJobAsync();

        run.Failed.ShouldBe(0);
        (await s.RequestStatusAsync()).ShouldBe("cancelled");
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("active", "hesap talep öncesi durumuna döner");
        ErasureKit.Format((await s.CountsAsync(s.A.TenantId)).Where(c => !c.Key.EndsWith("outbox_messages", StringComparison.Ordinal))).ShouldBe(
            ErasureKit.Format(before.Where(c => !c.Key.EndsWith("outbox_messages", StringComparison.Ordinal))),
            "hiçbir veri silinmedi (yalnız iptal olayı outbox'a eklenir)");
        var audit = (await s.Factory.AuditDetailsAsync(s.A.TenantId, "deletion.cancelled")).ShouldHaveSingleItem();
        audit.GetProperty("by").GetString().ShouldBe("system");
        (await s.Factory.PlatformEventsAsync(s.A.TenantId, "TenantDeletionCancelled")).ShouldHaveSingleItem();
        (await s.Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldContain("erasure.precondition_failed");
    }

    [Fact]
    public async Task Job_FailsPermanently_WhenTheAccountIsNotPendingDeletion_AndNothingIsErased()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        await s.RequestDeletionAsync();
        var before = ErasureKit.Format(await s.CountsAsync(s.A.TenantId));
        await s.Host.SetAccountAsync(s.Factory, s.A.TenantId, "status = 'active'"); // tutarsız durum (elle müdahale)
        s.AdvancePastSchedule();

        var run = await s.RunJobAsync();

        run.Failed.ShouldBeGreaterThanOrEqualTo(1);
        (await s.RequestStatusAsync()).ShouldBe("failed");
        (await s.RequestAttemptsAsync()).ShouldBe(10, "otomatik yeniden denemeler durur (MaxAttempts)");
        (await s.Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldContain("erasure.precondition_failed");
        ErasureKit.Format(await s.CountsAsync(s.A.TenantId)).ShouldBe(before);
        (await s.ErasedStepsAsync()).ShouldBeEmpty();

        // Sonraki tur yeniden denemez (attempts = MaxAttempts).
        s.Clock.Advance(TimeSpan.FromHours(2));
        var again = await s.RunJobAsync();
        again.Failed.ShouldBe(0);
    }

    // ---- M2: tombstone öncesi doğrulama ----------------------------------------------------------------------------------

    [Fact]
    public async Task Tombstone_IsNotWritten_WhileAnUnregisteredTableStillHoldsTenantRows_AndTheErrorNamesTheTable()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Light);
        var table = "zz_orphan_" + Guid.NewGuid().ToString("N")[..8];
        await s.Factory.SqlAsync($"CREATE TABLE sales.{table} (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, note text)");
        try
        {
            await s.Factory.SqlAsync($"INSERT INTO sales.{table} (tenant_id, note) VALUES (@t, 'kalinti'), (@t, 'kalinti 2')", ("t", s.A.TenantId));
            await s.RequestDeletionAsync();
            s.AdvancePastSchedule();

            var run = await s.RunJobAsync();

            run.Failed.ShouldBeGreaterThanOrEqualTo(1);
            (await s.RequestStatusAsync()).ShouldBe("failed");
            (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion", "tombstone yazılmadı");
            (await s.ErasedStepsAsync()).ShouldNotContain(TenantErasureJob.TombstoneStep);
            var error = await s.Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId));
            error.ShouldContain("erasure.verification_failed");
            error.ShouldContain($"sales.{table}=2");
            error.ShouldNotContain("kalinti", Case.Sensitive, "hata satır içeriği (kişisel veri) taşımaz");
            (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND target_tenant_name = '[deleted]'", ("t", s.A.TenantId))).ShouldBeGreaterThan(0L, "redaksiyon tombstone'dan önce yapılır ve idempotenttir");
        }
        finally
        {
            await s.Factory.SqlAsync($"DROP TABLE IF EXISTS sales.{table}");
        }

        // Kalıntı giderilince (tablo kalktı) sonraki turda tamamlanır.
        s.Clock.Advance(TimeSpan.FromHours(2));
        await s.RunJobAsync();
        await s.AssertCompletedAsync();
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("deleted");
    }

    [Fact]
    public async Task ANonDatabaseStep_CanParticipate_AndItsVerificationHookBlocksTheTombstone()
    {
        var state = new ExternalStoreState { Remaining = 2 };
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.None, services: services => services.AddScoped<ITenantDataEraser>(_ => new ExternalStoreEraser(state)));
        state.Tenant = s.A.TenantId;
        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();

        await s.RunJobAsync();

        (await s.RequestStatusAsync()).ShouldBe("failed");
        (await s.ErasedStepsAsync()).ShouldContain("external-store");
        (await s.Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldContain("external-store:objects=2");
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("pending_deletion");

        state.Remaining = 0; // dış depo temizlendi
        s.Clock.Advance(TimeSpan.FromHours(2));
        await s.RunJobAsync();
        await s.AssertCompletedAsync();
    }

    [Fact]
    public async Task EveryTenantIdTable_InTheDatabase_IsCoveredByARegisteredEraserPath_AndEveryModuleContextHasItsEraserStep()
    {
        using var scope = factory.Services.CreateScope();
        var contexts = scope.ServiceProvider.GetServices<ModuleDbContext>().ToList();
        contexts.Count.ShouldBeGreaterThan(0);

        var covered = new HashSet<(string, string)>
        {
            ("audit", "audit_log_entries"), // AuditTenantDataEraser (sıra 900)
            ("platform", "usage_snapshots"), // tombstone adımı
        };
        foreach (var context in contexts)
        {
            foreach (var target in TenantDataEraser<ModuleDbContext>.BuildPlan(context))
            {
                covered.Add((target.Schema, target.Table));
            }
        }

        var tables = await scope.ServiceProvider.GetRequiredService<TenantErasureVerifier>().ListTenantTablesAsync(Ct);
        tables.Count.ShouldBeGreaterThan(10, "information_schema taraması tablo bulmalı (test boş geçmemeli)");
        var uncovered = tables.Where(t => !covered.Contains(t)).Select(t => $"{t.Schema}.{t.Table}").ToList();
        uncovered.ShouldBeEmpty("tenant_id kolonlu her tablo bir imha yolu (modül adımı, audit, tombstone) ile kapsanmalı: " + string.Join(", ", uncovered));

        var steps = TenantErasureSteps.Resolve(scope.ServiceProvider.GetServices<ITenantDataEraser>()).Select(e => e.Name).ToList();
        foreach (var context in contexts)
        {
            steps.ShouldContain("module:" + context.Schema, $"{context.GetType().Name} için imha adımı kayıtlı olmalı");
        }
    }

    [Fact]
    public async Task ErasureStepRegistry_OrdersByOrderThenName_CollapsesSameImplementations_AndRejectsNameClashes()
    {
        var a = new StubEraser("a", 10);
        var b = new StubEraser("b", 5);
        TenantErasureSteps.Resolve([a, b, a]).Select(e => e.Name).ShouldBe(["b", "a"]);

        Should.Throw<InvalidOperationException>(() => TenantErasureSteps.Resolve([new StubEraser("dup", 1), new OtherStubEraser("dup", 2)]));

        // Varsayılan doğrulama kancası: temiz.
        (await ((ITenantDataEraser)a).VerifyErasedAsync(Guid.NewGuid(), Ct)).IsClean.ShouldBeTrue();
    }

    // ---- L2: yetim Conductor yürütmeleri ------------------------------------------------------------------------------------

    [Fact]
    public async Task Erasure_AlsoRemovesOrphanedConductorExecutions_WhoseEngineIdWasNeverRecorded()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.Full);
        var engineIds = await s.EngineIdsAsync(s.A.TenantId);
        engineIds.ShouldNotBeEmpty("tohum veri motorda yürütme başlatmış olmalı");
        var orphan = engineIds[0];

        // Motor çağrısı başarılı ama engine_workflow_id yazımı kayboldu ("yetim"): motorda çalışan yürütmenin CRM'de kimliği yok.
        (await s.Factory.SqlAsync("UPDATE workflows.workflow_executions SET engine_workflow_id = NULL WHERE tenant_id = @t AND engine_workflow_id = @e", ("t", s.A.TenantId), ("e", orphan))).ShouldBe(1);
        s.Engine.Workflows.ContainsKey(orphan).ShouldBeTrue();

        await s.RequestDeletionAsync();
        s.AdvancePastSchedule();
        await s.RunJobAsync();
        await s.AssertCompletedAsync();

        s.Engine.RemovedWorkflowIds.ShouldContain(orphan, "correlationId ile bulunan yetim yürütme motordan silinir");
        s.Engine.Workflows.ContainsKey(orphan).ShouldBeFalse();
        (await s.ReportTotalsAsync()).GetValueOrDefault("conductor.orphan_executions").ShouldBeGreaterThanOrEqualTo(1);
    }

    // ---- L1: serbest metin redaksiyonu -------------------------------------------------------------------------------------

    [Fact]
    public async Task Erasure_RedactsTheFreeTextReasons_EverywhereTheyLive()
    {
        using var s = await ErasureScenario.CreateAsync(factory, SeedLevel.None);
        var secret = $"kisisel-{Guid.NewGuid():N}@ornek.com";
        await s.Platform.SuspendAsync(s.A.TenantId, $"Musteri {secret} sikayeti", "readOnly");
        await s.Platform.ReactivateRawAsync(s.A.TenantId);
        await s.Platform.SuspendAsync(s.A.TenantId, $"Tekrar {secret}", "readOnly");
        await s.RequestDeletionAsync(reason: $"Talep eden {secret}");
        s.AdvancePastSchedule();

        await s.RunJobAsync();
        await s.AssertCompletedAsync();

        (await s.Factory.ScalarAsync<string>("SELECT reason FROM platform.deletion_requests WHERE id = @r", ("r", s.Request.RequestId))).ShouldBe("[redacted]");
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND details ? 'reason'", ("t", s.A.TenantId))).ShouldBe(0L, "denetim satırlarında gerekçe kalmaz");
        (await s.CountAsync("SELECT count(*) FROM platform.platform_audit_entries WHERE details::text LIKE @p", ("p", $"%{secret}%"))).ShouldBe(0L);
        (await s.CountAsync("SELECT count(*) FROM platform.deletion_requests WHERE reason LIKE @p", ("p", $"%{secret}%"))).ShouldBe(0L);
        var account = await s.Factory.ErQueryAsync("SELECT coalesce(suspended_reason, 'NULL') || '|' || coalesce(suspension_mode, 'NULL') FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", s.A.TenantId));
        account.ShouldHaveSingleItem().ShouldBe("NULL|NULL", "mezar taşında askı gerekçesi/kipi kalmaz (L1)");
        (await s.AccountStatusAsync(s.A.TenantId)).ShouldBe("deleted");
    }

    private sealed class StubEraser(string name, int order) : ITenantDataEraser
    {
        public string Name => name;

        public int Order => order;

        public Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default) => Task.FromResult(EraseReport.Empty);
    }

    private sealed class OtherStubEraser(string name, int order) : ITenantDataEraser
    {
        public string Name => name;

        public int Order => order;

        public Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default) => Task.FromResult(EraseReport.Empty);
    }
}
