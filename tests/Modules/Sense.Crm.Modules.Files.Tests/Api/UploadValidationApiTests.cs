using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Tests.Domain;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Files.Tests.Api.FilesKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>Yükleme güvenlik/doğrulama kuralları: ad, tür allow-list, içerik imzası, boyut sınırları, tarayıcı, depo hatası, geçici dosya, hız/eşzamanlılık sınırları, günlük.</summary>
[Collection(ApiCollection.Name)]
public sealed class UploadValidationApiTests(CrmApiFactory factory)
{
    private async Task<(Org Org, Guid Account)> OrgWithAccountAsync(string name, Sense.Crm.Modules.Files.Tests.Api.FilesHost? host = null)
    {
        var org = await (host?.App ?? factory).NewOrgAsync(name);
        return (org, await org.Admin.NewRecordAsync("account"));
    }

    // ---- Ad ve tür ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PathTraversalInTheFileName_IsStrippedToItsLeaf_AndNeverReachesTheStorageKey()
    {
        var (org, account) = await OrgWithAccountAsync("Ad Gecisi");
        var file = await org.Admin.UploadOkAsync("account", account, "../../etc/passwd.pdf", SampleFiles.Pdf());
        file.Str("name").ShouldBe("passwd.pdf");
        var keys = factory.ObjectKeys(org.TenantId);
        keys.Single().ShouldNotContain("passwd");
        keys.Single().ShouldNotContain("etc");

        var windows = await org.Admin.UploadOkAsync("account", account, "..\\..\\Windows\\rapor.pdf", SampleFiles.Pdf());
        windows.Str("name").ShouldBe("rapor.pdf");
    }

    [Theory]
    [InlineData("rapor.exe", "exe")]
    [InlineData("betik.bat", "bat")]
    [InlineData("sayfa.html", "html")]
    [InlineData("cizim.svg", "svg")]
    [InlineData("arsiv.zip", "zip")]
    [InlineData("gorsel.heic", "heic")]
    [InlineData("video.mp4", "mp4")]
    [InlineData("posta.eml", "eml")]
    [InlineData("uzantisiz", "")]
    [InlineData("sadece.EXE", "exe")]
    public async Task ExtensionsOutsideTheAllowList_Return415WithTheExtension(string fileName, string extension)
    {
        var (org, account) = await OrgWithAccountAsync("Uzanti " + fileName);
        using var response = await org.Admin.UploadAsync("account", account, new UploadItem(fileName, SampleFiles.Pdf()));
        var problem = await response.ProblemAsync(HttpStatusCode.UnsupportedMediaType, "file.type_not_allowed");
        problem.GetProperty("args").Str("extension").ShouldBe(extension);
        factory.ObjectKeys(org.TenantId).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a.exe.pdf")]
    [InlineData("a.EXE.docx")]
    [InlineData("a.html.png")]
    [InlineData("a.js.txt")]
    [InlineData("a.ps1.csv")]
    public async Task DangerousDoubleExtensions_AreRejected(string fileName)
    {
        var (org, account) = await OrgWithAccountAsync("Cift Uzanti " + fileName);
        var bytes = fileName.EndsWith("docx", StringComparison.Ordinal) ? SampleFiles.Docx() : fileName.EndsWith("png", StringComparison.Ordinal) ? SampleFiles.Png() : fileName.EndsWith("pdf", StringComparison.Ordinal) ? SampleFiles.Pdf() : SampleFiles.Text();
        using var response = await org.Admin.UploadAsync("account", account, new UploadItem(fileName, bytes));
        await response.ProblemAsync(HttpStatusCode.UnsupportedMediaType, "file.type_not_allowed");
    }

    public static TheoryData<string, byte[], string?> ContentMismatches() => new()
    {
        { "sahte.pdf", SampleFiles.Exe(), null },
        { "resim.png", SampleFiles.Jpeg(), null },
        { "not.txt", SampleFiles.Html(), null },
        { "gorsel.png", SampleFiles.Svg(), null },
        { "belge.docx", SampleFiles.PlainZip(), null },
        { "makro.docx", SampleFiles.Docx(withMacro: true), null },
        { "gomulu.docx", SampleFiles.Docx(withExecutable: true), null },
        { "makro.xlsx", SampleFiles.Xlsx(withMacro: true), null },
        { "iyi.pdf", SampleFiles.Pdf(), "text/html" },
        { "iyi2.pdf", SampleFiles.Pdf(), "image/svg+xml" },
        { "iyi3.png", SampleFiles.Png(), "application/pdf" },
    };

    [Theory]
    [MemberData(nameof(ContentMismatches))]
    public async Task ContentThatDoesNotMatchTheExtensionFamily_Or_ADangerousDeclaredType_Returns422(string fileName, byte[] bytes, string? declared)
    {
        var (org, account) = await OrgWithAccountAsync("Icerik " + fileName);
        using var response = await org.Admin.UploadAsync("account", account, new UploadItem(fileName, bytes, declared));
        await response.ProblemAsync((HttpStatusCode)422, "file.content_mismatch");
        factory.ObjectKeys(org.TenantId).ShouldBeEmpty();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(0);
    }

    [Fact]
    public async Task EmptyFiles_NamesThatSanitizeToNothing_AndMalformedRequests_AreRejectedWith400()
    {
        var (org, account) = await OrgWithAccountAsync("Bozuk Istekler");

        using (var empty = await org.Admin.UploadAsync("account", account, new UploadItem("bos.pdf", [])))
        {
            await empty.ProblemAsync(HttpStatusCode.BadRequest, "file.empty");
        }

        using (var noName = await org.Admin.UploadAsync("account", account, new UploadItem("../", SampleFiles.Pdf())))
        {
            await noName.ProblemAsync(HttpStatusCode.BadRequest, "file.name_invalid");
        }

        using (var wrongField = await org.Admin.UploadAsync("account", account, new UploadItem("a.pdf", SampleFiles.Pdf(), FieldName: "document")))
        {
            await wrongField.ProblemAsync(HttpStatusCode.BadRequest, "file.upload_invalid");
        }

        using (var notMultipart = await org.Admin.PostAsync($"{FilesPath}?recordType=account&recordId={account}", System.Net.Http.Json.JsonContent.Create(new { file = "x" }), Ct))
        {
            await notMultipart.ProblemAsync(HttpStatusCode.BadRequest, "file.upload_invalid");
        }

        using (var noParts = await org.Admin.PostAsync($"{FilesPath}?recordType=account&recordId={account}", new MultipartFormDataContent(), Ct))
        {
            await noParts.ProblemAsync(HttpStatusCode.BadRequest, "file.upload_invalid");
        }

        using (var broken = new ByteArrayContent(Encoding.ASCII.GetBytes("--x\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.pdf\"\r\n\r\nabc")))
        {
            broken.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=x");
            using var response = await org.Admin.PostAsync($"{FilesPath}?recordType=account&recordId={account}", broken, Ct);
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType, HttpStatusCode.UnprocessableEntity);
        }

        using (var badQuery = await org.Admin.PostAsync($"{FilesPath}?recordType=nope&recordId={account}", FilesKit.Multipart(new UploadItem("a.pdf", SampleFiles.Pdf())), Ct))
        {
            await badQuery.ProblemAsync(HttpStatusCode.BadRequest, "validation");
        }

        using (var noRecord = await org.Admin.PostAsync($"{FilesPath}?recordType=account&recordId={Guid.NewGuid()}", FilesKit.Multipart(new UploadItem("a.pdf", SampleFiles.Pdf())), Ct))
        {
            await noRecord.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
        }

        factory.ObjectKeys(org.TenantId).ShouldBeEmpty();
    }

    // ---- Boyut sınırları ------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileSizeLimit_ExactlyTheLimitPasses_OneByteMoreIs413_AlsoWhenTheLengthIsUnknown_AndNothingIsStored()
    {
        await using var host = FilesHost.Create(factory, ("Files:Upload:MaxFileMb", "1"), ("Files:Upload:MaxFilesPerRequest", "3"), ("Files:Upload:MaxRequestMb", "3"));
        var (org, account) = await OrgWithAccountAsync("Boyut Siniri", host);
        const int limit = 1024 * 1024;

        var exact = await org.Admin.UploadOkAsync("account", account, "tam.pdf", SampleFiles.Pdf(limit));
        exact.GetProperty("sizeBytes").GetInt64().ShouldBe(limit);

        using (var over = await org.Admin.UploadAsync("account", account, new UploadItem("fazla.pdf", SampleFiles.Pdf(limit + 1))))
        {
            var problem = await over.ProblemAsync((HttpStatusCode)413, "file.too_large");
            problem.GetProperty("args").GetProperty("maxBytes").GetInt64().ShouldBe(limit);
        }

        // Content-Length yokken (chunked) de sayaç çalışır.
        using (var chunked = new UnknownLengthContent(FilesKit.Multipart(new UploadItem("fazla2.pdf", SampleFiles.Pdf(limit + 1)))))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{FilesPath}?recordType=account&recordId={account}") { Content = chunked };
            using var response = await org.Admin.SendAsync(request, Ct);
            await response.ProblemAsync((HttpStatusCode)413, "file.too_large");
        }

        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(1, "yalnız sınırdaki dosya kaldı");
        host.TempFileCount.ShouldBe(0, "reddedilen yüklemelerin geçici dosyası da silinir");
    }

    [Fact]
    public async Task ContentLengthAboveMaxRequestMb_IsRejectedWith413_WithoutTouchingTheScannerOrStorage()
    {
        await using var host = FilesHost.Create(factory, ("Files:Upload:MaxFileMb", "1"), ("Files:Upload:MaxFilesPerRequest", "2"), ("Files:Upload:MaxRequestMb", "2"));
        var (org, account) = await OrgWithAccountAsync("Istek Siniri", host);

        using var response = await org.Admin.UploadAsync("account", account,
            new UploadItem("a.pdf", SampleFiles.Pdf(1_000_000)), new UploadItem("b.pdf", SampleFiles.Pdf(1_000_000)), new UploadItem("c.pdf", SampleFiles.Pdf(1_000_000)));
        await response.ProblemAsync((HttpStatusCode)413, "file.too_large");
        host.Scanner.Calls.ShouldBe(0);
        host.Faults.Puts.ShouldBe(0);
        host.TempFileCount.ShouldBe(0);
    }

    [Fact]
    public async Task MoreThanMaxFilesPerRequest_MarksTheExcessAsTooManyFiles()
    {
        await using var host = FilesHost.Create(factory, ("Files:Upload:MaxFilesPerRequest", "2"), ("Files:Upload:MaxRequestMb", "50"));
        var (org, account) = await OrgWithAccountAsync("Cok Dosya", host);

        using var response = await org.Admin.UploadAsync("account", account,
            new UploadItem("a.pdf", SampleFiles.Pdf()), new UploadItem("b.pdf", SampleFiles.Pdf()), new UploadItem("c.pdf", SampleFiles.Pdf()));
        var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("items").GetArrayLength().ShouldBe(2);
        var failed = json.GetProperty("failed").EnumerateArray().Single();
        failed.Str("code").ShouldBe("file.too_many_files");
        failed.GetProperty("args").GetProperty("max").GetInt32().ShouldBe(2);
    }

    // ---- Erken red ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EarlyRejections_HappenBeforeAnyByteIsProcessed_NoScannerNoStorageNoTempFile()
    {
        await using var host = FilesHost.Create(factory);
        var org = await host.App.NewOrgAsync("Erken Red");
        var account = await org.Admin.NewRecordAsync("account");
        var readOnly = await factory.NewMemberAsync(org, "Salt Okur", "crm.accounts.read");
        var payload = new UploadItem("a.pdf", SampleFiles.Pdf(500_000));

        // izin yok (yazma yok) → 403; kayıt yok → 404; tür geçersiz → 400; bilinmeyen kayıt izni yok.
        using (var forbidden = await readOnly.Client.UploadAsync("account", account, payload))
        {
            await forbidden.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var missing = await org.Admin.UploadAsync("account", Guid.NewGuid(), payload))
        {
            await missing.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
        }

        using (var invalid = await org.Admin.UploadAsync("nope", account, payload))
        {
            await invalid.ProblemAsync(HttpStatusCode.BadRequest, "validation");
        }

        host.Scanner.Calls.ShouldBe(0);
        host.Faults.Puts.ShouldBe(0);
        host.TempFileCount.ShouldBe(0);
        host.App.ObjectKeys(org.TenantId).ShouldBeEmpty();
    }

    [Fact]
    public async Task TempFiles_AreDeletedOnEveryPath_Success_Rejection_AndStorageFailure()
    {
        await using var host = FilesHost.Create(factory);
        var (org, account) = await OrgWithAccountAsync("Gecici Dosya", host);

        await org.Admin.UploadOkAsync("account", account, "iyi.pdf", SampleFiles.Pdf(100_000));
        host.TempFileCount.ShouldBe(0, "başarı");

        (await org.Admin.UploadAsync("account", account, new UploadItem("sahte.pdf", SampleFiles.Exe(100_000)))).Dispose();
        host.TempFileCount.ShouldBe(0, "imza uyuşmazlığı");

        host.Faults.FailPut = true;
        (await org.Admin.UploadAsync("account", account, new UploadItem("depo.pdf", SampleFiles.Pdf(100_000)))).Dispose();
        host.TempFileCount.ShouldBe(0, "depo hatası");
    }

    // ---- Tarayıcı -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InfectedUpload_IsRejectedBeforeAnyByteIsStored_WhenOnInfectedIsReject()
    {
        await using var host = FilesHost.Create(factory);
        host.Scanner.Result = () => ScanResult.Infected("Eicar-Test");
        var (org, account) = await OrgWithAccountAsync("Zararli Red", host);

        using var response = await org.Admin.UploadAsync("account", account, new UploadItem("zararli.pdf", SampleFiles.Pdf()));
        await response.ProblemAsync((HttpStatusCode)422, "file.infected");
        host.Scanner.Calls.ShouldBe(1);
        host.Faults.Puts.ShouldBe(0, "bayt depoya hiç yazılmaz");
        host.App.ObjectKeys(org.TenantId).ShouldBeEmpty();
        (await host.App.CountRowsAsync(factory, org.TenantId)).ShouldBe(0);
    }

    [Fact]
    public async Task InfectedUpload_InQuarantineMode_IsStoredButQuarantined_CannotBeDownloaded_CountsTowardQuota_AndCanBeDeleted()
    {
        await using var host = FilesHost.Create(factory, ("Files:Scanner:OnInfected", "quarantine"));
        host.Scanner.Result = () => ScanResult.Infected("Eicar-Test");
        var (org, account) = await OrgWithAccountAsync("Karantina", host);

        var file = await org.Admin.UploadOkAsync("account", account, "supheli.pdf", SampleFiles.Pdf(2000));
        file.Str("state").ShouldBe("quarantined");
        file.GetProperty("canPreview").GetBoolean().ShouldBeFalse();
        (await factory.ScalarAsync<string>("SELECT scan_status FROM files.attachments WHERE id = @id", ("id", file.Id()))).ShouldBe("infected");
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(1);

        using var content = await org.Admin.ContentAsync(file.Id());
        await content.ProblemAsync(HttpStatusCode.Conflict, "file.quarantined");
        var usage = await org.Admin.GetJsonAsync($"{FilesPath}/usage");
        usage.GetProperty("usedBytes").GetInt64().ShouldBe(2000, "karantinadaki dosya kotaya sayılır");
        usage.GetProperty("quarantinedCount").GetInt64().ShouldBe(1);

        await org.Admin.DeleteJsonAsync($"{FilesPath}/{file.Id()}");
        (await org.Admin.GetJsonAsync($"{FilesPath}/usage")).GetProperty("usedBytes").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task ScannerFailure_IsFailOpenByDefault_AndFailClosedWhenConfigured()
    {
        await using var open = FilesHost.Create(factory);
        open.Scanner.Result = () => ScanResult.Error;
        var (orgOpen, accountOpen) = await OrgWithAccountAsync("Tarayici Acik", open);
        var ok = await orgOpen.Admin.UploadOkAsync("account", accountOpen, "a.pdf", SampleFiles.Pdf());
        ok.Str("state").ShouldBe("ready");
        (await factory.ScalarAsync<string>("SELECT scan_status FROM files.attachments WHERE id = @id", ("id", ok.Id()))).ShouldBe("skipped");

        await using var closed = FilesHost.Create(factory, ("Files:Scanner:FailMode", "closed"));
        closed.Scanner.Result = () => ScanResult.Error;
        var (orgClosed, accountClosed) = await OrgWithAccountAsync("Tarayici Kapali", closed);
        using var response = await orgClosed.Admin.UploadAsync("account", accountClosed, new UploadItem("a.pdf", SampleFiles.Pdf()));
        await response.ProblemAsync(HttpStatusCode.ServiceUnavailable, "file.scan_unavailable");
        closed.Faults.Puts.ShouldBe(0);
    }

    [Fact]
    public async Task CleanScan_IsRecordedAsClean()
    {
        await using var host = FilesHost.Create(factory);
        var (org, account) = await OrgWithAccountAsync("Tarama Temiz", host);
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
        (await factory.ScalarAsync<string>("SELECT scan_status FROM files.attachments WHERE id = @id", ("id", file.Id()))).ShouldBe("clean");
        host.Scanner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ScannerRunsOnlyAfterTheSignatureCheck()
    {
        await using var host = FilesHost.Create(factory);
        var (org, account) = await OrgWithAccountAsync("Tarama Sirasi", host);
        (await org.Admin.UploadAsync("account", account, new UploadItem("sahte.pdf", SampleFiles.Exe()))).Dispose();
        host.Scanner.Calls.ShouldBe(0);
    }

    // ---- Depo hataları --------------------------------------------------------------------------------------------

    [Fact]
    public async Task StorageOutage_Returns503_AndLeavesNoRow()
    {
        await using var host = FilesHost.Create(factory);
        host.Faults.FailPut = true;
        var (org, account) = await OrgWithAccountAsync("Depo Arizasi", host);

        using var response = await org.Admin.UploadAsync("account", account, new UploadItem("a.pdf", SampleFiles.Pdf()));
        await response.ProblemAsync(HttpStatusCode.ServiceUnavailable, "file.storage_unavailable");
        (await host.App.CountRowsAsync(factory, org.TenantId)).ShouldBe(0);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.outbox_messages WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(0);

        // Kurtarma: depo dönünce aynı yükleme başarılı olur.
        host.Faults.FailPut = false;
        await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
    }

    [Fact]
    public async Task StorageReadOutage_OnDownload_Returns503()
    {
        await using var host = FilesHost.Create(factory);
        var (org, account) = await OrgWithAccountAsync("Depo Okuma", host);
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());

        host.Faults.FailGet = true;
        using var response = await org.Admin.ContentAsync(file.Id());
        await response.ProblemAsync(HttpStatusCode.ServiceUnavailable, "file.storage_unavailable");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE file_id = @id", ("id", file.Id()))).ShouldBe(0);
    }

    [Fact]
    public async Task CommitFailureAfterTheUpload_DeletesTheObject_AndAFailedCompensationLeavesAnOrphanForReconciliation()
    {
        await using var host = FilesHost.Create(factory);
        var (org, account) = await OrgWithAccountAsync("Commit Hatasi", host);

        // Veritabanı yazımını tetikleyiciyle başarısız kıl (yalnız bu ada).
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await factory.SqlAsync($"""
            CREATE FUNCTION files.boom_{suffix}() RETURNS trigger AS $$ BEGIN IF NEW.name = 'boom-{suffix}.pdf' THEN RAISE EXCEPTION 'simulated commit failure'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER boom_{suffix} BEFORE INSERT ON files.attachments FOR EACH ROW EXECUTE FUNCTION files.boom_{suffix}();
            """);
        try
        {
            using var failed = await org.Admin.UploadAsync("account", account, new UploadItem($"boom-{suffix}.pdf", SampleFiles.Pdf()));
            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            host.App.ObjectKeys(org.TenantId).ShouldBeEmpty("telafi silmesi nesneyi geri aldı");
            (await host.App.CountRowsAsync(factory, org.TenantId)).ShouldBe(0);

            // Telafi silmesi de başarısızsa nesne yetim kalır (uzlaştırma siler).
            host.Faults.FailDelete = true;
            using var orphaned = await org.Admin.UploadAsync("account", account, new UploadItem($"boom-{suffix}.pdf", SampleFiles.Pdf()));
            orphaned.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            host.App.ObjectKeys(org.TenantId).Count.ShouldBe(1, "yetim nesne");
            (await host.App.CountRowsAsync(factory, org.TenantId)).ShouldBe(0);
        }
        finally
        {
            await factory.SqlAsync($"DROP TRIGGER boom_{suffix} ON files.attachments; DROP FUNCTION files.boom_{suffix}();");
        }
    }

    // ---- Hız ve eşzamanlılık sınırları ---------------------------------------------------------------------------

    [Fact]
    public async Task UploadRateLimit_IsPerUploadedPart_Returning429AfterThePermitsAreSpent()
    {
        await using var host = FilesHost.Create(factory, ("Files:RateLimiting:Upload", "2"));
        var (org, account) = await OrgWithAccountAsync("Yukleme Hizi", host);

        await org.Admin.UploadOkAsync("account", account, "1.pdf", SampleFiles.Pdf());
        await org.Admin.UploadOkAsync("account", account, "2.pdf", SampleFiles.Pdf());
        using var limited = await org.Admin.UploadAsync("account", account, new UploadItem("3.pdf", SampleFiles.Pdf()));
        await limited.ProblemAsync((HttpStatusCode)429, "general.rate_limit_exceeded");
        host.App.ObjectKeys(org.TenantId).Count.ShouldBe(2);
    }

    [Fact]
    public async Task DownloadRateLimit_Returns429AfterThePermitsAreSpent()
    {
        await using var host = FilesHost.Create(factory, ("Files:RateLimiting:Download", "2"));
        var (org, account) = await OrgWithAccountAsync("Indirme Hizi", host);
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());

        (await org.Admin.ContentAsync(file.Id())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.ContentAsync(file.Id())).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var limited = await org.Admin.ContentAsync(file.Id());
        await limited.ProblemAsync((HttpStatusCode)429, "general.rate_limit_exceeded");
    }

    [Fact]
    public async Task ConcurrentUploadsPerTenant_AreLimited_TheExcessGets429_AndTheSlotIsReleasedAfterwards()
    {
        await using var host = FilesHost.Create(factory, ("Files:RateLimiting:ConcurrentUploadsPerTenant", "1"));
        var (org, account) = await OrgWithAccountAsync("Es Zamanli", host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Faults.PutGate = gate.Task;

        // Birinci yükleme depo yazımında (slot tutuluyor) bekler; ikinci yükleme aynı kiracıdan → 429.
        var first = org.Admin.UploadAsync("account", account, new UploadItem("yavas.pdf", SampleFiles.Pdf(200_000)));
        await host.Faults.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using (var second = await org.Admin.UploadAsync("account", account, new UploadItem("hizli.pdf", SampleFiles.Pdf())))
        {
            await second.ProblemAsync((HttpStatusCode)429, "general.rate_limit_exceeded");
        }

        gate.SetResult();
        using var firstResponse = await first;
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Slot serbest bırakıldı.
        host.Faults.PutGate = null;
        await org.Admin.UploadOkAsync("account", account, "sonra.pdf", SampleFiles.Pdf());
    }

    // ---- Günlük ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileNames_NeverAppearInTheLogs_NotForRejectedNorForAcceptedUploads()
    {
        await using var host = FilesHost.Create(factory);
        var (org, account) = await OrgWithAccountAsync("Gunluk Sizintisi", host);

        (await org.Admin.UploadAsync("account", account, new UploadItem("SECRET-INVOICE-9876.exe", SampleFiles.Exe()))).Dispose();
        (await org.Admin.UploadAsync("account", account, new UploadItem("SECRET-FAKE-1122.pdf", SampleFiles.Exe()))).Dispose();
        var file = await org.Admin.UploadOkAsync("account", account, "SECRET-CONTRACT-5432.pdf", SampleFiles.Pdf());
        (await org.Admin.ContentAsync(file.Id())).Dispose();
        await org.Admin.PatchAsync($"{FilesPath}/{file.Id()}", System.Net.Http.Json.JsonContent.Create(new { name = "SECRET-RENAMED-7788.pdf" }), Ct);
        await org.Admin.DeleteJsonAsync($"{FilesPath}/{file.Id()}");

        host.Logs.Lines.Count.ShouldBeGreaterThan(0, "günlük havuzu boş: test boş geçmemeli");
        host.Logs.Lines.Where(l => l.Contains("SECRET-", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty("dosya adı günlüğe yazılmamalı");
        host.Logs.Lines.Where(l => l.Contains(FilesKit.Sha256Hex(SampleFiles.Pdf()), StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();
    }

    // ---- Yardımcı içerik türleri ----------------------------------------------------------------------------------

    /// <summary>Uzunluğu bilinmeyen (chunked) gövde: <c>Content-Length</c> yok.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public UnknownLengthContent(HttpContent inner)
        {
            _inner = inner;
            Headers.ContentType = inner.Headers.ContentType;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Gövdenin ilk yarısını yazar, <paramref name="gate"/> açılana kadar bekler (sunucu tarafında slotu tutmak için).</summary>
    private sealed class GatedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Task _gate;
        private readonly TaskCompletionSource _started;

        public GatedContent(HttpContent inner, Task gate, TaskCompletionSource started)
        {
            _inner = inner;
            _gate = gate;
            _started = started;
            Headers.ContentType = inner.Headers.ContentType;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var all = await _inner.ReadAsByteArrayAsync();
            var half = all.Length / 2;
            await stream.WriteAsync(all.AsMemory(0, half));
            await stream.FlushAsync();
            _started.TrySetResult();
            await _gate;
            await stream.WriteAsync(all.AsMemory(half));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal static class FilesHostExtensions
{
    /// <summary>Kiracının <c>files.attachments</c> satır sayısı (her durum).</summary>
    public static Task<long> CountRowsAsync(this Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _, CrmApiFactory factory, Guid tenantId) =>
        factory.ScalarAsync<long>("SELECT count(*) FROM files.attachments WHERE tenant_id = @t", ("t", tenantId));
}
