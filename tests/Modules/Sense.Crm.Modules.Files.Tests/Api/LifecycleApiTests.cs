using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Infrastructure.Jobs;
using Sense.Crm.Modules.Files.Tests.Domain;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Files.Tests.Api.FilesKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>Yaşam döngüsü işleri (sahte saatle): temizlik, uzlaştırma (kayıp/yetim/güvenlik supabı/kayıt-yok süpürmesi) ve KVKK nesne imhası.</summary>
[Collection(ApiCollection.Name)]
public sealed class LifecycleApiTests(CrmApiFactory factory)
{
    private static TestClock NewClock()
    {
        var now = DateTimeOffset.UtcNow;
        return new TestClock(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero));
    }

    private static async Task<PurgeRunResult> PurgeAsync(FilesHost host)
    {
        using var scope = host.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilesPurgeJob>().RunOnceAsync(Ct);
    }

    private static async Task<ReconcileRunResult> ReconcileAsync(FilesHost host, Guid? tenant = null, bool dryRun = false)
    {
        using var scope = host.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilesReconciliationJob>().RunAsync(new ReconcileRunOptions(dryRun, tenant), Ct);
    }

    private async Task<string> StateAsync(Guid id) => await factory.ScalarAsync<string>("SELECT state FROM files.attachments WHERE id = @id", ("id", id));

    private async Task<bool> RowExistsAsync(Guid id) => await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE id = @id", ("id", id)) == 1;

    // ---- Temizlik -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Purge_LeavesRecentlyDeletedFilesAlone_AndRemovesObjectAndRowAfterTheRetention()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var org = await host.App.NewOrgAsync("Temizlik A");
        var account = await org.Admin.NewRecordAsync("account");
        var keep = await org.Admin.UploadOkAsync("account", account, "kalan.pdf", SampleFiles.Pdf());
        var doomed1 = await org.Admin.UploadOkAsync("account", account, "silinen1.pdf", SampleFiles.Pdf());
        var doomed2 = await org.Admin.UploadOkAsync("account", account, "silinen2.pdf", SampleFiles.Pdf());
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{doomed1.Id()}");
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{doomed2.Id()}");
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(3);

        clock.Advance(TimeSpan.FromDays(6));
        await PurgeAsync(host);
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(3, "eşik altı: dokunulmaz");
        (await RowExistsAsync(doomed1.Id())).ShouldBeTrue();

        clock.Advance(TimeSpan.FromDays(2));
        var run = await PurgeAsync(host);
        run.Purged.ShouldBeGreaterThanOrEqualTo(2);
        run.Failed.ShouldBe(0);
        (await RowExistsAsync(doomed1.Id())).ShouldBeFalse();
        (await RowExistsAsync(doomed2.Id())).ShouldBeFalse();
        (await RowExistsAsync(keep.Id())).ShouldBeTrue();
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(1);
        host.App.ObjectKeys(org.TenantId).Single().ShouldEndWith(keep.Id().ToString("N"));

        // İdempotent.
        var again = await PurgeAsync(host);
        again.Purged.ShouldBe(0);
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(1);

        // Denetim izi audit'te kalır (fiziksel silme sonrası).
        (await factory.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type = 'FileAttachment' AND entity_id = @id", ("t", org.TenantId), ("id", doomed1.Id().ToString()))).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Purge_ObjectDeletionFailureKeepsTheRow_AndTheNextRoundRetries()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var org = await host.App.NewOrgAsync("Temizlik Hata");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{file.Id()}");
        clock.Advance(TimeSpan.FromDays(8));

        host.Faults.FailDelete = true;
        var failed = await PurgeAsync(host);
        failed.Failed.ShouldBeGreaterThanOrEqualTo(1);
        (await RowExistsAsync(file.Id())).ShouldBeTrue("nesne silinemediyse satır bırakılır");
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(1);

        host.Faults.FailDelete = false;
        await PurgeAsync(host);
        (await RowExistsAsync(file.Id())).ShouldBeFalse();
        host.App.ObjectKeys(org.TenantId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Purge_DoesNotTouchAnotherTenantsRecentlyDeletedFile_AndTrimsTheAccessLogByRetention()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var old = await host.App.NewOrgAsync("Temizlik Eski");
        var recent = await host.App.NewOrgAsync("Temizlik Yeni");
        var oldAccount = await old.Admin.NewRecordAsync("account");
        var recentAccount = await recent.Admin.NewRecordAsync("account");
        var oldFile = await old.Admin.UploadOkAsync("account", oldAccount, "eski.pdf", SampleFiles.Pdf());
        await old.Admin.DeleteJsonAsync($"{FilesPath}/{oldFile.Id()}");

        clock.Advance(TimeSpan.FromDays(9));
        var recentFile = await recent.Admin.UploadOkAsync("account", recentAccount, "yeni.pdf", SampleFiles.Pdf());
        await recent.Admin.DeleteJsonAsync($"{FilesPath}/{recentFile.Id()}");

        // Erişim günlüğü: biri eşik (365 gün) dışında, biri içinde.
        var kept = await recent.Admin.UploadOkAsync("account", recentAccount, "log.pdf", SampleFiles.Pdf());
        (await recent.Admin.ContentAsync(kept.Id())).Dispose();
        (await recent.Admin.ContentAsync(kept.Id())).Dispose();
        await factory.SqlAsync(
            "UPDATE files.file_access_log SET occurred_at = @at WHERE id = (SELECT id FROM files.file_access_log WHERE tenant_id = @t ORDER BY id LIMIT 1)",
            ("at", DateTime.UtcNow.AddDays(-400)), ("t", recent.TenantId));

        await PurgeAsync(host);

        (await RowExistsAsync(oldFile.Id())).ShouldBeFalse("9 gün önce silinen temizlendi");
        (await RowExistsAsync(recentFile.Id())).ShouldBeTrue("saat ilerletildikten sonra silinen henüz eşiği doldurmadı");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE tenant_id = @t", ("t", recent.TenantId))).ShouldBe(1, "365 günden eski erişim satırı temizlendi");
    }

    // ---- Uzlaştırma -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_RowWithoutObjectBecomesMissing_DownloadIs410_ItStillCountsAndCanBeDeleted_AndRecoversWhenTheObjectReturns()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var org = await host.App.NewOrgAsync("Uzlastirma Kayip");
        var account = await org.Admin.NewRecordAsync("account");
        var bytes = SampleFiles.Pdf(3000);
        var file = await org.Admin.UploadOkAsync("account", account, "kayip.pdf", bytes);
        var key = host.App.ObjectKeys(org.TenantId).Single();

        host.App.Store().RemoveRaw(key).ShouldBeTrue();
        var run = await ReconcileAsync(host, org.TenantId);
        run.Tenants.Single().MarkedMissing.ShouldBe(1);
        (await StateAsync(file.Id())).ShouldBe("missing");

        (await org.Admin.GetJsonAsync($"{FilesPath}/{file.Id()}")).Str("state").ShouldBe("missing");
        using (var content = await org.Admin.ContentAsync(file.Id()))
        {
            await content.ProblemAsync(HttpStatusCode.Gone, "file.content_missing");
        }

        var usage = await org.Admin.GetJsonAsync($"{FilesPath}/usage");
        usage.GetProperty("missingCount").GetInt64().ShouldBe(1);
        usage.GetProperty("usedBytes").GetInt64().ShouldBe(3000, "missing kotaya sayılır");

        // Nesne geri gelir (geri yükleme) → ready.
        host.App.Store().SeedRaw(key, bytes, clock.GetUtcNow());
        var recovered = await ReconcileAsync(host, org.TenantId);
        recovered.Tenants.Single().RestoredReady.ShouldBe(1);
        (await StateAsync(file.Id())).ShouldBe("ready");
        using (var content = await org.Admin.ContentAsync(file.Id()))
        {
            content.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // missing dosya silinebilir.
        host.App.Store().RemoveRaw(key);
        await ReconcileAsync(host, org.TenantId);
        (await StateAsync(file.Id())).ShouldBe("missing");
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{file.Id()}");
    }

    [Fact]
    public async Task Reconcile_SizeMismatch_MarksTheFileMissing()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var org = await host.App.NewOrgAsync("Uzlastirma Boyut");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf(1000));
        host.App.Store().SeedRaw(host.App.ObjectKeys(org.TenantId).Single(), SampleFiles.Pdf(999), clock.GetUtcNow());

        var run = await ReconcileAsync(host, org.TenantId);
        run.Tenants.Single().SizeMismatch.ShouldBe(1);
        (await StateAsync(file.Id())).ShouldBe("missing");
    }

    [Fact]
    public async Task Reconcile_OrphanObjects_AreKeptInsideTheGraceWindow_AndDeletedAfterIt_ForeignKeysAndUnknownTenantPrefixesAreNeverDeleted()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock, ("Files:Reconcile:MaxOrphanDeleteFraction", "1"));
        var org = await host.App.NewOrgAsync("Uzlastirma Yetim");
        var account = await org.Admin.NewRecordAsync("account");
        for (var i = 0; i < 25; i++)
        {
            await org.Admin.UploadOkAsync("account", account, $"iyi-{i}.pdf", SampleFiles.Pdf(64));
        }

        var orphanKey = ObjectKey.For(org.TenantId, DateTime.UtcNow.Year, Guid.CreateVersion7()).ToString();
        var foreignKey = ObjectKey.TenantPrefix(org.TenantId) + "elle-koyulmus-yabanci-nesne";
        var unknownTenantKey = ObjectKey.For(Guid.NewGuid(), DateTime.UtcNow.Year, Guid.CreateVersion7()).ToString();
        host.App.Store().SeedRaw(orphanKey, [1, 2, 3], clock.GetUtcNow());
        host.App.Store().SeedRaw(foreignKey, [1, 2, 3], clock.GetUtcNow().AddDays(-30));
        host.App.Store().SeedRaw(unknownTenantKey, [1, 2, 3], clock.GetUtcNow().AddDays(-30));

        // Grace içinde (yükleme–commit penceresi): yetim korunur.
        var early = await ReconcileAsync(host, org.TenantId);
        early.Tenants.Single().OrphansFound.ShouldBe(0);
        host.App.Store().Snapshot(orphanKey).ShouldNotBeNull();

        clock.Advance(TimeSpan.FromHours(25));
        var late = await ReconcileAsync(host);
        var report = late.Tenants.Single(t => t.TenantId == org.TenantId);
        report.OrphansFound.ShouldBe(1);
        report.OrphansDeleted.ShouldBe(1);
        report.ForeignObjectsSkipped.ShouldBe(1);
        host.App.Store().Snapshot(orphanKey).ShouldBeNull("grace sonrası yetim silindi");
        host.App.Store().Snapshot(foreignKey).ShouldNotBeNull("ayrıştırılamayan nesne asla silinmez");
        host.App.Store().Snapshot(unknownTenantKey).ShouldNotBeNull("bilinmeyen kiracı öneki asla silinmez");
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(26, "25 gerçek dosya + yabancı nesne");
    }

    [Fact]
    public async Task Reconcile_SafetyValve_TripsOnTheCountOrTheFraction_AndDeletesNothing_DryRunWritesNothing()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var org = await host.App.NewOrgAsync("Uzlastirma Supap");
        var account = await org.Admin.NewRecordAsync("account");
        for (var i = 0; i < 10; i++)
        {
            await org.Admin.UploadOkAsync("account", account, $"iyi-{i}.pdf", SampleFiles.Pdf(64));
        }

        // 5 yetim / 15 nesne = %33 > %5 (varsayılan oran): hiçbir şey silinmez.
        var orphans = Enumerable.Range(0, 5).Select(_ => ObjectKey.For(org.TenantId, DateTime.UtcNow.Year, Guid.CreateVersion7()).ToString()).ToList();
        foreach (var key in orphans)
        {
            host.App.Store().SeedRaw(key, [9], clock.GetUtcNow().AddDays(-3));
        }

        clock.Advance(TimeSpan.FromHours(1));
        var tripped = await ReconcileAsync(host, org.TenantId);
        tripped.Tenants.Single().OrphanGuardTripped.ShouldBeTrue();
        tripped.Tenants.Single().OrphansDeleted.ShouldBe(0);
        orphans.ShouldAllBe(k => host.App.Store().Snapshot(k) != null);

        // --dry-run: yazmaz (durum, silme yok), yalnız rapor.
        var file = (await org.Admin.ListAsync("account", account)).GetProperty("items")[0];
        host.App.Store().RemoveRaw(host.App.ObjectKeys(org.TenantId).First(k => k.EndsWith(file.Id().ToString("N"), StringComparison.Ordinal)));
        var dry = await ReconcileAsync(host, org.TenantId, dryRun: true);
        dry.DryRun.ShouldBeTrue();
        dry.Tenants.Single().MarkedMissing.ShouldBe(1, "rapor eksik nesneyi görür");
        (await StateAsync(file.Id())).ShouldBe("ready", "dry-run satırı değiştirmez");

        // Mutlak sayı sınırı: oran serbest, MaxOrphanDeletePerRun = 2 → 3 yetim silinmez.
        await using var strict = FilesHost.Create(factory, NewClock(), ("Files:Reconcile:MaxOrphanDeleteFraction", "1"), ("Files:Reconcile:MaxOrphanDeletePerRun", "2"));
        var org2 = await strict.App.NewOrgAsync("Uzlastirma Sayi");
        for (var i = 0; i < 3; i++)
        {
            strict.App.Store().SeedRaw(ObjectKey.For(org2.TenantId, DateTime.UtcNow.Year, Guid.CreateVersion7()).ToString(), [1], DateTimeOffset.UtcNow.AddDays(-3));
        }

        var byCount = await ReconcileAsync(strict, org2.TenantId);
        byCount.Tenants.Single().OrphanGuardTripped.ShouldBeTrue();
        strict.App.ObjectKeys(org2.TenantId).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Reconcile_RecordMissingSweep_MarksAfterTheRecordIsDeleted_ClearsWhenItReturns_AndSoftDeletesAfterTheGrace()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var org = await host.App.NewOrgAsync("Uzlastirma Sup");
        var account = await org.Admin.NewRecordAsync("account");
        var lead = await org.Admin.NewRecordAsync("lead");
        var accountFile = await org.Admin.UploadOkAsync("account", account, "firma.pdf", SampleFiles.Pdf());
        var leadFile = await org.Admin.UploadOkAsync("lead", lead, "aday.pdf", SampleFiles.Pdf());

        await org.Admin.DeleteRecordAsync("account", account);
        var first = await ReconcileAsync(host, org.TenantId);
        first.Tenants.Single().RecordMissingMarked.ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE id = @id AND record_missing_since IS NOT NULL", ("id", accountFile.Id()))).ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE id = @id AND record_missing_since IS NOT NULL", ("id", leadFile.Id()))).ShouldBe(0);

        // Kayıt geri gelir (geri yükleme): işaret kalkar.
        await factory.SqlAsync("UPDATE sales.accounts SET is_deleted = false, deleted_at = NULL WHERE id = @id", ("id", account));
        var restored = await ReconcileAsync(host, org.TenantId);
        restored.Tenants.Single().RecordMissingCleared.ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE id = @id AND record_missing_since IS NOT NULL", ("id", accountFile.Id()))).ShouldBe(0);

        // Kayıt yeniden silinir; grace (30 gün) dolunca dosya yumuşak silinir (sistem kullanıcısıyla) ve FileDeleted yayınlanır.
        await org.Admin.DeleteRecordAsync("account", account);
        await ReconcileAsync(host, org.TenantId);
        clock.Advance(TimeSpan.FromDays(31));
        var swept = await ReconcileAsync(host, org.TenantId);
        swept.Tenants.Single().SweptDeleted.ShouldBe(1);
        (await StateAsync(accountFile.Id())).ShouldBe("deleted");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE id = @id AND deleted_by_user_id IS NULL AND deleted_at IS NOT NULL", ("id", accountFile.Id()))).ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.outbox_messages WHERE tenant_id = @t AND type LIKE '%FileDeleted%' AND actor_user_id IS NULL", ("t", org.TenantId))).ShouldBe(1);
        (await StateAsync(leadFile.Id())).ShouldBe("ready");
    }

    // ---- KVKK imhası ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantErasure_DeletesEveryObjectOfTheTenantWithAVerification_LeavesTheOtherTenantsBytesIdentical_AndClearsTheTables()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock, ("Files:RateLimiting:Upload", "1000"));
        var platform = await host.App.PlatformAdminAsync();
        var a = await host.App.NewOrgAsync("Imha A");
        var b = await host.App.NewOrgAsync("Imha B");

        // A: 9 kayıt türünde dosya + yumuşak silinen + erişim günlüğü.
        var aFiles = new List<Guid>();
        foreach (var type in RecordTypes)
        {
            var record = await a.Admin.NewRecordAsync(type);
            aFiles.Add((await a.Admin.UploadOkAsync(type, record, $"{type}.pdf", SampleFiles.Pdf(500))).Id());
        }

        await a.Admin.DeleteJsonAsync($"{FilesPath}/{aFiles[0]}");
        (await a.Admin.ContentAsync(aFiles[1])).Dispose();

        // A önekinde satırsız nesneler: yetimler + 1000'den fazla nesne (toplu silme sayfalaması).
        for (var i = 0; i < 1_100; i++)
        {
            host.App.Store().SeedRaw(ObjectKey.For(a.TenantId, DateTime.UtcNow.Year, Guid.CreateVersion7()).ToString(), [1, 2, 3], clock.GetUtcNow());
        }

        host.App.Store().SeedRaw(ObjectKey.TenantPrefix(a.TenantId) + "yabanci", [7], clock.GetUtcNow());

        var bAccount = await b.Admin.NewRecordAsync("account");
        var bBytes = SampleFiles.Pdf(2222);
        var bFile = await b.Admin.UploadOkAsync("account", bAccount, "b.pdf", bBytes);
        var bKeysBefore = host.App.ObjectKeys(b.TenantId).ToDictionary(k => k, k => Convert.ToHexString(SHA256.HashData(host.App.Store().Snapshot(k)!)));
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", b.TenantId))).ShouldBe(1);
        host.App.ObjectKeys(a.TenantId).Count.ShouldBeGreaterThan(1_100);

        // Talep → bekleme süresi → imha işi.
        var request = await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{a.TenantId}/deletion-request", new { reason = "KVKK", retentionDays = 7 }, HttpStatusCode.OK);
        clock.SetUtcNow(DateTimeOffset.Parse(request.Str("scheduledFor")).AddMinutes(1));
        using (var scope = host.App.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<TenantErasureJob>().RunOnceAsync(Ct);
            run.Failed.ShouldBe(0);
        }

        // A: önekte 0 nesne (doğrulama adımı), tablolar boş, rapora nesne sayısı girdi.
        host.App.ObjectKeys(a.TenantId).ShouldBeEmpty();
        foreach (var table in new[] { "files.attachments", "files.file_access_log", "files.outbox_messages" })
        {
            (await factory.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(0, table);
        }

        var report = await factory.ScalarAsync<string>("SELECT report::text FROM platform.deletion_requests WHERE tenant_id = @t ORDER BY requested_at DESC LIMIT 1", ("t", a.TenantId));
        report.ShouldContain("files-objects");
        report.ShouldContain("files.objects");
        var erasedSteps = await factory.ScalarAsync<string>("SELECT array_to_string(erased_steps, ',') FROM platform.deletion_requests WHERE tenant_id = @t ORDER BY requested_at DESC LIMIT 1", ("t", a.TenantId));
        var steps = erasedSteps.Split(',').ToList();
        steps.IndexOf("files-objects").ShouldBeGreaterThan(-1);
        steps.IndexOf("files-objects").ShouldBeLessThan(steps.IndexOf("module:files"), "nesneler satırlardan önce (sıra 95 < 100)");

        // B: bayt bayt aynı.
        var bKeysAfter = host.App.ObjectKeys(b.TenantId).ToDictionary(k => k, k => Convert.ToHexString(SHA256.HashData(host.App.Store().Snapshot(k)!)));
        bKeysAfter.ShouldBe(bKeysBefore);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", b.TenantId))).ShouldBe(1);
        using var stillThere = await b.Admin.ContentAsync(bFile.Id());
        (await stillThere.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(bBytes);
    }

    [Fact]
    public async Task TenantErasure_StepFailsWhenObjectsRemain_AndSucceedsOnRetry_ReplayAfterARestoreDeletesRestoredObjectsAgain()
    {
        var clock = NewClock();
        await using var host = FilesHost.Create(factory, clock);
        var platform = await host.App.PlatformAdminAsync();
        var a = await host.App.NewOrgAsync("Imha Yeniden");
        var account = await a.Admin.NewRecordAsync("account");
        await a.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
        var key = host.App.ObjectKeys(a.TenantId).Single();

        // Depo listeleme arızası: adım hata verir (failed), satırlar silinmez; arıza kalkınca yeniden deneme tamamlanır.
        host.Faults.FailList = true;
        var request = await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{a.TenantId}/deletion-request", new { reason = "KVKK", retentionDays = 7 }, HttpStatusCode.OK);
        clock.SetUtcNow(DateTimeOffset.Parse(request.Str("scheduledFor")).AddMinutes(1));
        using (var scope = host.App.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<TenantErasureJob>().RunOnceAsync(Ct);
            run.Failed.ShouldBeGreaterThanOrEqualTo(1);
        }

        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(1, "başarısız adım satırlara dokunmadı");

        host.Faults.FailList = false;
        clock.Advance(TimeSpan.FromHours(6));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var scope = host.App.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TenantErasureJob>().RunOnceAsync(Ct);
            if (host.App.ObjectKeys(a.TenantId).Count == 0)
            {
                break;
            }

            clock.Advance(TimeSpan.FromHours(6));
        }

        host.App.ObjectKeys(a.TenantId).ShouldBeEmpty();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(0);

        // Geri yükleme sonrası nesne geri gelirse (yedek): erase-deleted-tenants yeniden oynatması nesneleri yeniden siler.
        host.App.Store().SeedRaw(key, [1, 2, 3], clock.GetUtcNow());
        using (var scope = host.App.Services.CreateScope())
        {
            var replay = scope.ServiceProvider.GetRequiredService<DeletedTenantsReplay>();
            await replay.RunAsync(Ct);
        }

        host.App.ObjectKeys(a.TenantId).ShouldBeEmpty("yeniden oynatma geri yüklenen nesneleri yeniden siler");
    }
}
