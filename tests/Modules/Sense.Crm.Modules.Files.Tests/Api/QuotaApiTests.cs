using System.Net;
using Sense.Crm.Modules.Files.Tests.Domain;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Files.Tests.Api.FilesKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>
/// Depolama kotası (M7 <c>maxStorageMb</c>): sert, kesin; <c>0</c> = hiç yükleme yok, <c>null</c> = sınırsız (sıfır sayım sorgusu); silme anında düşer; eşzamanlı yüklemelerle aşılamaz;
/// plan düşürme dosyayı silmez; istisna anahtarı esastır; ölçüm/anlık görüntü/finans CSV'si.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuotaApiTests(CrmApiFactory factory)
{
    private const int MiB = 1024 * 1024;

    private async Task<(Org Org, Guid Account, HttpClient Platform)> ArrangeAsync(string name, int? maxStorageMb, FilesHost? host = null)
    {
        var app = host?.App ?? factory;
        var org = await app.NewOrgAsync(name);
        var account = await org.Admin.NewRecordAsync("account");
        var platform = await app.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(org.TenantId, await factory.EnsurePlanAsync("quota", maxStorageMb));
        return (org, account, platform);
    }

    [Fact]
    public async Task OneMebibyteQuota_TheExactTotalPasses_OneByteMoreIs402WithTheDocumentedArgs()
    {
        var (org, account, _) = await ArrangeAsync("Kota 1MB", 1);
        await org.Admin.UploadOkAsync("account", account, "yari.pdf", SampleFiles.Pdf(MiB / 2));
        await org.Admin.UploadOkAsync("account", account, "diger-yari.pdf", SampleFiles.Pdf(MiB / 2));

        using (var over = await org.Admin.UploadAsync("account", account, new UploadItem("bir-bayt.pdf", SampleFiles.Pdf(17))))
        {
            var problem = await over.ProblemAsync(HttpStatusCode.PaymentRequired, "file.quota_exceeded");
            var args = problem.GetProperty("args");
            args.GetProperty("maxBytes").GetInt64().ShouldBe(MiB);
            args.GetProperty("usedBytes").GetInt64().ShouldBe(MiB);
            args.GetProperty("requestedBytes").GetInt64().ShouldBe(17);
        }

        factory.ObjectKeys(org.TenantId).Count.ShouldBe(2, "reddedilen yükleme depoya yazılmadı");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(2);

        // Tam sınırdaki yükleme ayrıca tek dosya olarak da geçer (kalan 0 → 1 bayt bile reddedilir).
        using var oneByte = await org.Admin.UploadAsync("account", account, new UploadItem("x.txt", SampleFiles.Text("a")));
        await oneByte.ProblemAsync(HttpStatusCode.PaymentRequired, "file.quota_exceeded");
    }

    [Fact]
    public async Task ZeroQuota_RejectsEveryUpload_WhileListDownloadAndDeleteStayOpen()
    {
        var (org, account, platform) = await ArrangeAsync("Kota 0", null);
        var file = await org.Admin.UploadOkAsync("account", account, "once.pdf", SampleFiles.Pdf(2000));

        var downgrade = await platform.PutSubscriptionAsync(org.TenantId, await factory.EnsurePlanAsync("zero", 0));
        var overLimit = downgrade.GetProperty("overLimit").EnumerateArray().Single(o => o.Str("limit") == "storage");
        overLimit.Str("module").ShouldBe("files");
        overLimit.GetProperty("max").GetInt64().ShouldBe(0);
        overLimit.GetProperty("used").GetInt64().ShouldBe(2000);

        using (var upload = await org.Admin.UploadAsync("account", account, new UploadItem("yeni.pdf", SampleFiles.Pdf())))
        {
            var problem = await upload.ProblemAsync(HttpStatusCode.PaymentRequired, "file.quota_exceeded");
            problem.GetProperty("args").GetProperty("maxBytes").GetInt64().ShouldBe(0);
        }

        // Plan düşürme mevcut dosyayı silmez: liste/indirme açık.
        (await org.Admin.ListAsync("account", account)).GetProperty("totalCount").GetInt64().ShouldBe(1);
        using (var content = await org.Admin.ContentAsync(file.Id()))
        {
            content.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var subscription = await org.Admin.GetJsonAsync($"{Base}/subscription");
        subscription.GetProperty("limits").GetProperty("maxStorageMb").GetInt32().ShouldBe(0);
        subscription.GetProperty("overLimit").EnumerateArray().Any(o => o.Str("limit") == "storage").ShouldBeTrue();

        await org.Admin.DeleteJsonAsync($"{FilesPath}/{file.Id()}");
        (await org.Admin.GetJsonAsync($"{FilesPath}/usage")).GetProperty("usedBytes").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task UnlimitedQuota_RunsZeroUsageQueries_AndAFiniteQuotaQueriesOncePerUpload()
    {
        await using var host = FilesHost.Create(factory);
        var unlimited = await host.App.NewOrgAsync("Kota Sinirsiz");
        var account = await unlimited.Admin.NewRecordAsync("account");
        var before = host.UsageQueries;
        await unlimited.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
        await unlimited.Admin.UploadOkAsync("account", account, "b.pdf", SampleFiles.Pdf());
        host.UsageQueries.ShouldBe(before, "sınırsız (null) kotada sayım hiç yapılmaz");

        var platform = await host.App.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(unlimited.TenantId, await factory.EnsurePlanAsync("finite", 100));
        var afterPlanChange = host.UsageQueries;
        await unlimited.Admin.UploadOkAsync("account", account, "c.pdf", SampleFiles.Pdf());
        await unlimited.Admin.UploadOkAsync("account", account, "d.pdf", SampleFiles.Pdf());
        (host.UsageQueries - afterPlanChange).ShouldBe(2, "sonlu kotada yükleme başına bir kesin sayım");
    }

    [Fact]
    public async Task Deleting_FreesTheQuotaImmediately_AndThePhysicalPurgeDoesNotChangeItAgain()
    {
        var (org, account, _) = await ArrangeAsync("Kota Silme", 1);
        var big = await org.Admin.UploadOkAsync("account", account, "buyuk.pdf", SampleFiles.Pdf(MiB));
        using (var full = await org.Admin.UploadAsync("account", account, new UploadItem("sigmaz.pdf", SampleFiles.Pdf(1000))))
        {
            await full.ProblemAsync(HttpStatusCode.PaymentRequired, "file.quota_exceeded");
        }

        await org.Admin.DeleteJsonAsync($"{FilesPath}/{big.Id()}");
        (await org.Admin.GetJsonAsync($"{FilesPath}/usage")).GetProperty("usedBytes").GetInt64().ShouldBe(0, "yumuşak silinen kotaya sayılmaz");
        await org.Admin.UploadOkAsync("account", account, "sigar.pdf", SampleFiles.Pdf(MiB));
    }

    [Fact]
    public async Task ConcurrentUploadsCannotExceedTheQuota_ExactlyTheFittingOnesSucceed()
    {
        await using var host = FilesHost.Create(factory,
            ("Files:RateLimiting:ConcurrentUploadsPerTenant", "16"),
            ("Files:RateLimiting:MaxConcurrentUploads", "32"),
            ("Files:RateLimiting:Upload", "1000"));
        var (org, account, _) = await ArrangeAsync("Kota Yaris", 5, host);

        // Kotanın %40'ı (2 MiB) boyutunda 8 eşzamanlı yükleme → tam 2 başarı (2 × 2 = 4 ≤ 5; 3.'sü 6 > 5).
        var uploads = Enumerable.Range(0, 8)
            .Select(i => Task.Run(async () =>
            {
                using var response = await org.Admin.UploadAsync("account", account, new UploadItem($"paralel-{i}.pdf", SampleFiles.Pdf(2 * MiB)));
                return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Ct));
            }, Ct))
            .ToList();
        var results = await Task.WhenAll(uploads);

        results.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(2, string.Join("\n", results.Select(r => $"{(int)r.StatusCode}")));
        results.Count(r => r.StatusCode == HttpStatusCode.PaymentRequired).ShouldBe(6);
        results.Where(r => r.StatusCode == HttpStatusCode.PaymentRequired).ShouldAllBe(r => r.Body.Contains("file.quota_exceeded", StringComparison.Ordinal));

        (await factory.ScalarAsync<long>("SELECT COALESCE(sum(size_bytes), 0) FROM files.attachments WHERE tenant_id = @t AND state <> 'deleted'", ("t", org.TenantId))).ShouldBe(4L * MiB);
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(2);
    }

    [Fact]
    public async Task MultiFileUpload_TheSecondFileThatCrossesTheQuotaIsReportedInFailed()
    {
        var (org, account, _) = await ArrangeAsync("Kota Coklu", 1);

        using var response = await org.Admin.UploadAsync("account", account,
            new UploadItem("sigar.pdf", SampleFiles.Pdf(700_000)), new UploadItem("sigmaz.pdf", SampleFiles.Pdf(700_000)));
        var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("items").GetArrayLength().ShouldBe(1);
        var failed = json.GetProperty("failed").EnumerateArray().Single();
        failed.Str("fileName").ShouldBe("sigmaz.pdf");
        failed.Str("code").ShouldBe("file.quota_exceeded");
        failed.GetProperty("args").GetProperty("maxBytes").GetInt64().ShouldBe(MiB);
    }

    [Fact]
    public async Task Overrides_AbsentKeyKeepsThePlan_NullMeansUnlimited_AValueReplacesIt()
    {
        var (org, account, platform) = await ArrangeAsync("Kota Istisna", 1);
        var plan = await factory.EnsurePlanAsync("ovr", 1);
        await platform.PutSubscriptionAsync(org.TenantId, plan);

        // Anahtar yok → plan (1 MiB).
        using (var tooBig = await org.Admin.UploadAsync("account", account, new UploadItem("iki.pdf", SampleFiles.Pdf(2 * MiB))))
        {
            await tooBig.ProblemAsync(HttpStatusCode.PaymentRequired, "file.quota_exceeded");
        }

        // Değer → plan yerine 4 MiB.
        await platform.PutSubscriptionAsync(org.TenantId, plan, overrides: new { maxStorageMb = 4 });
        await org.Admin.UploadOkAsync("account", account, "iki.pdf", SampleFiles.Pdf(2 * MiB));
        (await platform.GetJsonAsync($"{PlatformBase}/organizations/{org.TenantId}")).GetProperty("limits").GetProperty("maxStorageMb").GetInt32().ShouldBe(4);

        // Bağımsız anahtar (kısmi): yalnız maxUsers verilirse depolama kotası plana döner.
        await platform.PutSubscriptionAsync(org.TenantId, plan, overrides: new { maxUsers = 5 });
        (await platform.GetJsonAsync($"{PlatformBase}/organizations/{org.TenantId}")).GetProperty("limits").GetProperty("maxStorageMb").GetInt32().ShouldBe(1);

        // null → açıkça sınırsız.
        await platform.PutSubscriptionAsync(org.TenantId, plan, overrides: new { maxStorageMb = (int?)null });
        await org.Admin.UploadOkAsync("account", account, "sinirsiz.pdf", SampleFiles.Pdf(3 * MiB));
        var detail = await platform.GetJsonAsync($"{PlatformBase}/organizations/{org.TenantId}");
        detail.GetProperty("limits").Has("maxStorageMb").ShouldBeFalse("sınırsız = anahtar yok");
        (await org.Admin.GetJsonAsync($"{Base}/subscription")).GetProperty("limits").Has("maxStorageMb").ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_048_577)]
    public async Task OverridesOutOfRange_AreA400ValidationOnTheField(int value)
    {
        var (org, _, platform) = await ArrangeAsync("Kota Aralik " + value, 5);
        var response = await platform.PutAsync($"{PlatformBase}/organizations/{org.TenantId}/subscription",
            System.Net.Http.Json.JsonContent.Create(new { planCode = "internal", overrides = new { maxStorageMb = value } }), Ct);
        var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").Has("overrides.maxStorageMb").ShouldBeTrue(problem.ToString());
    }

    [Fact]
    public async Task Subscription_ShowsFiniteLimitStorageUsageAndFileCount_AndTheUsageEndpointStaysExact()
    {
        var (org, account, _) = await ArrangeAsync("Abonelik Depolama", 25);
        await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf(1000));
        await org.Admin.UploadOkAsync("account", account, "b.png", SampleFiles.Png(500));
        var deleted = await org.Admin.UploadOkAsync("account", account, "c.pdf", SampleFiles.Pdf(300));
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{deleted.Id()}");

        var subscription = await org.Admin.GetJsonAsync($"{Base}/subscription");
        subscription.GetProperty("limits").GetProperty("maxStorageMb").GetInt32().ShouldBe(25);
        subscription.GetProperty("usage").GetProperty("storageBytes").GetInt64().ShouldBe(1500, "yumuşak silinen sayılmaz");
        subscription.GetProperty("usage").GetProperty("fileCount").GetInt64().ShouldBe(2);
        subscription.GetProperty("overLimit").GetArrayLength().ShouldBe(0);

        var usage = await org.Admin.GetJsonAsync($"{FilesPath}/usage");
        usage.GetProperty("maxBytes").GetInt64().ShouldBe(25L * MiB);
        usage.GetProperty("usedBytes").GetInt64().ShouldBe(1500);
    }

    [Fact]
    public async Task UsageReporter_FeedsTheSnapshotAndTheFinanceCsv_ExcludingSoftDeletedAndOtherTenants()
    {
        var (org, account, platform) = await ArrangeAsync("Olcum Dosya", null);
        var other = await factory.NewOrgAsync("Olcum Baska");
        var otherAccount = await other.Admin.NewRecordAsync("account");
        await other.Admin.UploadOkAsync("account", otherAccount, "baska.pdf", SampleFiles.Pdf(9999));

        await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf(1000));
        await org.Admin.UploadOkAsync("account", account, "b.pdf", SampleFiles.Pdf(2000));
        var gone = await org.Admin.UploadOkAsync("account", account, "c.pdf", SampleFiles.Pdf(4000));
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{gone.Id()}");

        var refreshed = await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{org.TenantId}/usage/refresh", null, HttpStatusCode.OK);
        var today = refreshed.GetProperty("items").EnumerateArray().Last();
        var metrics = today.GetProperty("metrics");
        metrics.GetProperty("files.storage_bytes").GetInt64().ShouldBe(3000);
        metrics.GetProperty("files.files").GetInt64().ShouldBe(2);
        metrics.Has("files.records").ShouldBeFalse("kayıt limiti değil: files.records yoktur");

        var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var response = await platform.GetAsync($"{PlatformBase}/usage/export?from={from}&to={to}", Ct);
        var csv = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, csv);
        var lines = csv.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var header = lines[0].Split(',');
        header.ShouldContain("files.files");
        header.ShouldContain("files.storage_bytes");
        var row = lines.Skip(1).Select(l => l.Split(',')).Single(c => c[Array.IndexOf(header, "tenantId")] == org.TenantId.ToString("D"));
        row[Array.IndexOf(header, "files.storage_bytes")].ShouldBe("3000");
        row[Array.IndexOf(header, "files.files")].ShouldBe("2");
    }

    [Fact]
    public void PlanCatalogValidator_RejectsNegativeAndAbsurdStorageLimits_AndAcceptsZeroNullAndTheUpperBound()
    {
        static PlatformOptions With(int? storage) => new()
        {
            Plans = [new PlanDefinition { Code = "p_test", Name = "Test", Limits = new PlanLimitsDefinition { MaxStorageMb = storage } }],
        };

        PlanCatalogValidator.ValidatePlans(With(null)).ShouldBeEmpty();
        PlanCatalogValidator.ValidatePlans(With(0)).ShouldBeEmpty();
        PlanCatalogValidator.ValidatePlans(With(1_048_576)).ShouldBeEmpty();
        PlanCatalogValidator.ValidatePlans(With(-1)).ShouldContain(e => e.Contains("maxStorageMb", StringComparison.Ordinal));
        PlanCatalogValidator.ValidatePlans(With(1_048_577)).ShouldContain(e => e.Contains("maxStorageMb", StringComparison.Ordinal));
    }
}
