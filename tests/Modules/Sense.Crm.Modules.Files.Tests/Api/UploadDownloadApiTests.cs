using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Sense.Crm.Modules.Files.Tests.Domain;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Files.Tests.Api.FilesKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>Yükleme → liste → meta → indirme/önizleme → yeniden adlandırma → silme: sözleşme, başlıklar, aralıklı indirme, erişim günlüğü, denetim, outbox.</summary>
[Collection(ApiCollection.Name)]
public sealed partial class UploadDownloadApiTests(CrmApiFactory factory)
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9]{4}/[0-9a-f]{32}$")]
    private static partial Regex KeyPattern();

    [Fact]
    public async Task UploadPdf_Returns201WithTheFileDto_StoresItUnderTheStrictKey_AndPersistsTheCanonicalMetadata()
    {
        var org = await factory.NewOrgAsync("Yukleme A");
        var account = await org.Admin.NewRecordAsync("account");
        var bytes = SampleFiles.Pdf(200_000);

        using var response = await org.Admin.UploadAsync("account", account, new UploadItem("Sözleşme 2026.pdf", bytes, "application/pdf"));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
        json.GetProperty("failed").GetArrayLength().ShouldBe(0);
        var file = json.GetProperty("items")[0];

        response.Headers.Location!.ToString().ShouldBe($"/api/v1/files/{file.Id()}");
        file.Str("recordType").ShouldBe("account");
        file.GetProperty("recordId").GetGuid().ShouldBe(account);
        file.Str("name").ShouldBe("Sözleşme 2026.pdf");
        file.Str("extension").ShouldBe("pdf");
        file.Str("contentType").ShouldBe("application/pdf");
        file.GetProperty("sizeBytes").GetInt64().ShouldBe(bytes.Length);
        file.Str("sha256").ShouldBe(Sha256Hex(bytes));
        file.Str("state").ShouldBe("ready");
        file.GetProperty("uploadedByUserId").GetGuid().ShouldBe(org.AdminUserId);
        file.Str("uploadedByName").ShouldNotBeNullOrWhiteSpace();
        file.GetProperty("canPreview").GetBoolean().ShouldBeTrue();
        file.Has("updatedAt").ShouldBeFalse("yalnız yeniden adlandırılınca yazılır");
        DateTimeOffset.Parse(file.Str("uploadedAt")).ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));

        // Nesne anahtarı: {tenant}/{yyyy}/{fileId:N}; ad/tür/kayıt kimliği içermez; depoda tek nesne.
        var keys = factory.ObjectKeys(org.TenantId);
        keys.Count.ShouldBe(1);
        KeyPattern().IsMatch(keys[0]).ShouldBeTrue(keys[0]);
        keys[0].ShouldEndWith(file.Id().ToString("N"));
        keys[0].ShouldNotContain("pdf");
        keys[0].ShouldNotContain(account.ToString("D"));
        factory.Store().Snapshot(keys[0]).ShouldBe(bytes);

        (await factory.ScalarAsync<string>("SELECT content_type FROM files.attachments WHERE id = @id", ("id", file.Id()))).ShouldBe("application/pdf");
        (await factory.ScalarAsync<string>("SELECT scan_status FROM files.attachments WHERE id = @id", ("id", file.Id()))).ShouldBe("skipped");
        (await factory.ScalarAsync<string>("SELECT storage_key FROM files.attachments WHERE id = @id", ("id", file.Id()))).ShouldBe(keys[0]);
    }

    [Fact]
    public async Task UploadStoresTheCanonicalTypeFromTheSignature_NotTheClientDeclaration()
    {
        var org = await factory.NewOrgAsync("Kanonik Tur");
        var account = await org.Admin.NewRecordAsync("account");

        var png = await org.Admin.UploadOkAsync("account", account, "resim.png", SampleFiles.Png(), "application/octet-stream");
        png.Str("contentType").ShouldBe("image/png");

        var csv = await org.Admin.UploadOkAsync("account", account, "liste.csv", SampleFiles.CsvWithBom(), "application/vnd.ms-excel");
        csv.Str("contentType").ShouldBe("text/csv; charset=utf-8");
        csv.GetProperty("canPreview").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Download_ServesTheExactBytes_WithTheDocumentedSecurityHeaders()
    {
        var org = await factory.NewOrgAsync("Indirme A");
        var account = await org.Admin.NewRecordAsync("account");
        var bytes = SampleFiles.Pdf(50_000);
        var file = await org.Admin.UploadOkAsync("account", account, "Sözleşme ç.pdf", bytes);

        using var response = await org.Admin.ContentAsync(file.Id());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(bytes);
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/pdf");
        response.Content.Headers.ContentLength.ShouldBe(bytes.Length);
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.CacheControl.Private.ShouldBeTrue();
        response.Headers.CacheControl.Public.ShouldBeFalse();
        response.Headers.GetValues("Cross-Origin-Resource-Policy").ShouldContain("same-origin");
        response.Headers.GetValues("Accept-Ranges").ShouldContain("bytes");
        response.Headers.ETag!.Tag.ShouldBe($"\"{file.Str("sha256")}\"");

        var disposition = response.Content.Headers.ContentDisposition!;
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileNameStar.ShouldBe("Sözleşme ç.pdf");
        disposition.FileName.ShouldNotBeNull();
        disposition.FileName.ShouldNotContain("ö");
        var raw = string.Join(" ", response.Content.Headers.GetValues("Content-Disposition"));
        raw.ShouldContain("filename*=UTF-8''");
        raw.ShouldContain("S%C3%B6zle%C5%9Fme");
    }

    [Fact]
    public async Task Download_IfNoneMatch_Returns304WithoutABody_AndDoesNotLogAnAccess()
    {
        var org = await factory.NewOrgAsync("Etag A");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
        var etag = $"\"{file.Str("sha256")}\"";

        using var notModified = await org.Admin.ContentAsync(file.Id(), ifNoneMatch: etag);
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await notModified.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE file_id = @id", ("id", file.Id()))).ShouldBe(0);

        using var different = await org.Admin.ContentAsync(file.Id(), ifNoneMatch: "\"nope\"");
        different.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_FileNameWithQuotesAndControlCharacters_IsSanitizedAndCannotInjectHeaders()
    {
        var org = await factory.NewOrgAsync("Baslik Enjeksiyonu");
        var account = await org.Admin.NewRecordAsync("account");
        // CR/LF bir başlık değerinde düz yazılamaz; gerçekçi saldırı yolu RFC 5987 filename* ile yüzde kodlu CRLF'tir.
        var boundary = "crm-inject-" + Guid.NewGuid().ToString("N");
        var body = new MemoryStream();
        var head = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"x.pdf\"; filename*=UTF-8''rapor%22%0D%0AX-Injected%3A%201.pdf\r\n\r\n");
        body.Write(head);
        body.Write(SampleFiles.Pdf());
        body.Write(Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"));
        using var raw = new ByteArrayContent(body.ToArray());
        raw.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        using var uploaded = await org.Admin.PostAsync($"{FilesPath}?recordType=account&recordId={account}", raw, Ct);
        var uploadedBody = await uploaded.Content.ReadAsStringAsync(Ct);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, uploadedBody);
        var file = System.Text.Json.JsonDocument.Parse(uploadedBody).RootElement.GetProperty("items")[0];
        file.Str("name").ShouldBe("raporX-Injected 1.pdf", "CR/LF, tırnak ve iki nokta temizlenir");

        using var response = await org.Admin.ContentAsync(file.Id());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("X-Injected").ShouldBeFalse();
        string.Join(" ", response.Content.Headers.GetValues("Content-Disposition")).ShouldNotContain("\r");
    }

    [Theory]
    [InlineData("bytes=0-0", 0, 0)]
    [InlineData("bytes=10-19", 10, 19)]
    [InlineData("bytes=990-", 990, 999)]
    [InlineData("bytes=-10", 990, 999)]
    [InlineData("bytes=995-5000", 995, 999)]
    public async Task Download_SingleRange_Returns206WithTheExactSlice(string range, int start, int end)
    {
        var org = await factory.NewOrgAsync("Aralik " + start);
        var account = await org.Admin.NewRecordAsync("account");
        var bytes = SampleFiles.Pdf(1000);
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", bytes);

        using var response = await org.Admin.ContentAsync(file.Id(), range: range);
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().ShouldBe($"bytes {start}-{end}/1000");
        response.Content.Headers.ContentLength.ShouldBe(end - start + 1);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(bytes[start..(end + 1)]);
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=5000-6000")]
    [InlineData("bytes=9-3")]
    [InlineData("bytes=abc-")]
    public async Task Download_UnsatisfiableRange_Returns416WithTheLengthInContentRange(string range)
    {
        var org = await factory.NewOrgAsync("Aralik 416");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf(1000));

        using var response = await org.Admin.ContentAsync(file.Id(), range: range);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
        string.Join(",", response.Content.Headers.GetValues("Content-Range")).ShouldBe("bytes */1000");
    }

    [Fact]
    public async Task Download_MultipleRanges_ReturnsTheFullBodyWith200()
    {
        var org = await factory.NewOrgAsync("Coklu Aralik");
        var account = await org.Admin.NewRecordAsync("account");
        var bytes = SampleFiles.Pdf(1000);
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", bytes);

        using var response = await org.Admin.ContentAsync(file.Id(), range: "bytes=0-1,5-6");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(bytes);
    }

    [Fact]
    public async Task InlinePreview_IsOnlyForPreviewableTypes_WithSandboxedCspAndSameOriginFraming()
    {
        var org = await factory.NewOrgAsync("Onizleme");
        var account = await org.Admin.NewRecordAsync("account");
        var png = await org.Admin.UploadOkAsync("account", account, "resim.png", SampleFiles.Png());
        var pdf = await org.Admin.UploadOkAsync("account", account, "belge.pdf", SampleFiles.Pdf());

        using var image = await org.Admin.ContentAsync(png.Id(), "inline");
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        image.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        image.Headers.GetValues("X-Frame-Options").Single().ShouldBe("SAMEORIGIN");
        image.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'; sandbox");

        using var document = await org.Admin.ContentAsync(pdf.Id(), "inline");
        document.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; frame-ancestors 'self'");
        document.Headers.GetValues("X-Frame-Options").Single().ShouldBe("SAMEORIGIN");

        // Varsayılan (attachment) yanıtı çerçevelenemez.
        using var attachment = await org.Admin.ContentAsync(pdf.Id());
        attachment.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        attachment.Headers.Contains("Content-Security-Policy").ShouldBeFalse();
    }

    [Theory]
    [InlineData("liste.csv")]
    [InlineData("not.txt")]
    [InlineData("belge.docx")]
    [InlineData("sayfa.xlsx")]
    public async Task InlinePreview_OfNonPreviewableTypes_IsA400ValidationOnDisposition(string fileName)
    {
        var org = await factory.NewOrgAsync("Onizleme Yok " + fileName);
        var account = await org.Admin.NewRecordAsync("account");
        var bytes = fileName.EndsWith("docx", StringComparison.Ordinal) ? SampleFiles.Docx() : fileName.EndsWith("xlsx", StringComparison.Ordinal) ? SampleFiles.Xlsx() : SampleFiles.Text("a,b\n1,2\n");
        var file = await org.Admin.UploadOkAsync("account", account, fileName, bytes);

        using var response = await org.Admin.ContentAsync(file.Id(), "inline");
        var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").Has("disposition").ShouldBeTrue();

        using var bogus = await org.Admin.ContentAsync(file.Id(), "bogus");
        (await bogus.ProblemAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").Has("disposition").ShouldBeTrue();
    }

    [Fact]
    public async Task AccessLog_RecordsOneRowPerDownload_NotPerRangeContinuation_WithoutNameOrIp()
    {
        var org = await factory.NewOrgAsync("Erisim Gunlugu");
        var account = await org.Admin.NewRecordAsync("account");
        var png = await org.Admin.UploadOkAsync("account", account, "resim.png", SampleFiles.Png(4000));
        var id = png.Id();

        (await org.Admin.ContentAsync(id)).Dispose();
        (await org.Admin.ContentAsync(id, range: "bytes=0-99")).Dispose();
        (await org.Admin.ContentAsync(id, range: "bytes=100-199")).Dispose();
        (await org.Admin.ContentAsync(id, range: "bytes=200-")).Dispose();
        (await org.Admin.ContentAsync(id, "inline")).Dispose();

        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE file_id = @id", ("id", id))).ShouldBe(3, "tam indirme + aralığın ilk parçası + önizleme");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE file_id = @id AND action = 'download'", ("id", id))).ShouldBe(2);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE file_id = @id AND action = 'preview'", ("id", id))).ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE file_id = @id AND user_id = @u AND tenant_id = @t", ("id", id), ("u", org.AdminUserId), ("t", org.TenantId))).ShouldBe(3);

        var columns = await factory.ScalarAsync<string>("SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_schema = 'files' AND table_name = 'file_access_log'");
        columns.ShouldNotContain("ip");
        columns.ShouldNotContain("name");
    }

    [Fact]
    public async Task Rename_KeepsTheExtension_SanitizesTheName_AndStampsUpdatedAt()
    {
        var org = await factory.NewOrgAsync("Yeniden Adlandir");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "eski.pdf", SampleFiles.Pdf());
        var id = file.Id();

        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{FilesPath}/{id}", new { name = "  ../Yeni Ad.PDF " }, HttpStatusCode.NoContent);
        var meta = await org.Admin.GetJsonAsync($"{FilesPath}/{id}");
        meta.Str("name").ShouldBe("Yeni Ad.PDF");
        meta.Has("updatedAt").ShouldBeTrue();

        using var changed = await org.Admin.PatchAsync($"{FilesPath}/{id}", JsonContentOf(new { name = "Yeni.docx" }), Ct);
        await changed.ProblemAsync(HttpStatusCode.BadRequest, "file.extension_change_not_allowed");

        using var noExtension = await org.Admin.PatchAsync($"{FilesPath}/{id}", JsonContentOf(new { name = "uzantisiz" }), Ct);
        await noExtension.ProblemAsync(HttpStatusCode.BadRequest, "file.extension_change_not_allowed");

        using var doubleExtension = await org.Admin.PatchAsync($"{FilesPath}/{id}", JsonContentOf(new { name = "a.exe.pdf" }), Ct);
        await doubleExtension.ProblemAsync(HttpStatusCode.UnsupportedMediaType, "file.type_not_allowed");

        using var empty = await org.Admin.PatchAsync($"{FilesPath}/{id}", JsonContentOf(new { name = "../" }), Ct);
        await empty.ProblemAsync(HttpStatusCode.BadRequest, "file.name_invalid");

        using var blank = await org.Admin.PatchAsync($"{FilesPath}/{id}", JsonContentOf(new { name = "" }), Ct);
        (await blank.ProblemAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").Has("name").ShouldBeTrue();

        // Aynı kayıtta aynı ad olabilir (çakışma denetlenmez).
        await org.Admin.UploadOkAsync("account", account, "Yeni Ad.PDF", SampleFiles.Pdf());
    }

    private static System.Net.Http.Json.JsonContent JsonContentOf(object value) => System.Net.Http.Json.JsonContent.Create(value);

    [Fact]
    public async Task Delete_IsSoft_HidesTheFileEverywhere_KeepsTheObjectUntilPurge_AndRepeatedDeleteIs404()
    {
        var org = await factory.NewOrgAsync("Silme A");
        var account = await org.Admin.NewRecordAsync("account");
        var keep = await org.Admin.UploadOkAsync("account", account, "kalan.pdf", SampleFiles.Pdf());
        var gone = await org.Admin.UploadOkAsync("account", account, "silinen.pdf", SampleFiles.Pdf());

        await org.Admin.DeleteJsonAsync($"{FilesPath}/{gone.Id()}");

        (await org.Admin.GetJsonAsync($"{FilesPath}/{gone.Id()}", HttpStatusCode.NotFound)).Str("code").ShouldBe("not_found");
        using var content = await org.Admin.ContentAsync(gone.Id());
        await content.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        var list = await org.Admin.ListAsync("account", account);
        list.GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([keep.Id()]);
        list.GetProperty("totalCount").GetInt64().ShouldBe(1);

        (await factory.ScalarAsync<string>("SELECT state FROM files.attachments WHERE id = @id", ("id", gone.Id()))).ShouldBe("deleted");
        factory.ObjectKeys(org.TenantId).Count.ShouldBe(2, "nesne bekleme süresi boyunca fiziksel olarak durur");

        using var again = await org.Admin.DeleteAsync($"{FilesPath}/{gone.Id()}", Ct);
        await again.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var renameDeleted = await org.Admin.PatchAsync($"{FilesPath}/{gone.Id()}", JsonContentOf(new { name = "x.pdf" }), Ct);
        await renameDeleted.ProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task List_PagesSortsAndSearches_WithEscapedWildcards_AndAStableOrder()
    {
        var org = await factory.NewOrgAsync("Liste A");
        var account = await org.Admin.NewRecordAsync("account");
        var other = await org.Admin.NewRecordAsync("account");
        await org.Admin.UploadOkAsync("account", account, "b-orta.pdf", SampleFiles.Pdf(300));
        await org.Admin.UploadOkAsync("account", account, "a-kucuk.png", SampleFiles.Png(100));
        await org.Admin.UploadOkAsync("account", account, "c-buyuk.pdf", SampleFiles.Pdf(900));
        await org.Admin.UploadOkAsync("account", account, "100%_rapor.txt", SampleFiles.Text());
        await org.Admin.UploadOkAsync("account", other, "baska-kayit.pdf", SampleFiles.Pdf());

        var byDefault = await org.Admin.ListAsync("account", account);
        byDefault.GetProperty("totalCount").GetInt64().ShouldBe(4);
        byDefault.GetProperty("page").GetInt32().ShouldBe(1);
        byDefault.GetProperty("pageSize").GetInt32().ShouldBe(25);
        Names(byDefault).ShouldBe(["100%_rapor.txt", "c-buyuk.pdf", "a-kucuk.png", "b-orta.pdf"], "varsayılan -uploadedAt (en yeni önce)");

        Names(await org.Admin.ListAsync("account", account, "&sort=name")).ShouldBe(["100%_rapor.txt", "a-kucuk.png", "b-orta.pdf", "c-buyuk.pdf"]);
        Names(await org.Admin.ListAsync("account", account, "&sort=-sizeBytes")).First().ShouldBe("c-buyuk.pdf");
        Names(await org.Admin.ListAsync("account", account, "&sort=extension,name")).ShouldBe(["b-orta.pdf", "c-buyuk.pdf", "a-kucuk.png", "100%_rapor.txt"], "extension sırası pdf < png < txt");
        Names(await org.Admin.ListAsync("account", account, "&sort=bogus")).Count.ShouldBe(4, "bilinmeyen alan yok sayılır");

        var page2 = await org.Admin.ListAsync("account", account, "&sort=name&page=2&pageSize=3");
        Names(page2).ShouldBe(["c-buyuk.pdf"]);
        page2.GetProperty("totalCount").GetInt64().ShouldBe(4);
        page2.GetProperty("page").GetInt32().ShouldBe(2);
        page2.GetProperty("pageSize").GetInt32().ShouldBe(3);

        Names(await org.Admin.ListAsync("account", account, "&q=KUCUK")).ShouldBe(["a-kucuk.png"], "ILIKE büyük/küçük harf duyarsız");
        Names(await org.Admin.ListAsync("account", account, "&q=100%25")).ShouldBe(["100%_rapor.txt"], "% joker değil düz metin");
        Names(await org.Admin.ListAsync("account", account, "&q=%25")).ShouldBe(["100%_rapor.txt"]);
        Names(await org.Admin.ListAsync("account", account, "&q=_")).ShouldBe(["100%_rapor.txt"], "_ joker değil düz metin");
        Names(await org.Admin.ListAsync("account", account, "&q=yok-boyle-bir-ad")).ShouldBeEmpty();
    }

    private static List<string> Names(System.Text.Json.JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.Str("name")).ToList();

    [Fact]
    public async Task List_WithMissingOrInvalidRecordTypeOrId_IsA400Validation()
    {
        var org = await factory.NewOrgAsync("Liste Dogrulama");
        var id = Guid.NewGuid();
        foreach (var url in new[]
        {
            $"{FilesPath}",
            $"{FilesPath}?recordId={id}",
            $"{FilesPath}?recordType=account",
            $"{FilesPath}?recordType=bogus&recordId={id}",
            $"{FilesPath}?recordType=account&recordId=not-a-guid",
            $"{FilesPath}?recordType=account&recordId={Guid.Empty}",
        })
        {
            using var response = await org.Admin.GetAsync(url, Ct);
            await response.ProblemAsync(HttpStatusCode.BadRequest, "validation");
        }

        using var missing = await org.Admin.GetAsync($"{FilesPath}?recordType=account&recordId={Guid.NewGuid()}", Ct);
        await missing.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
    }

    [Fact]
    public async Task MultipleFiles_AllValid_Returns201WithEveryItem_AndMixedResultsReturn200WithFailed()
    {
        var org = await factory.NewOrgAsync("Coklu Dosya");
        var account = await org.Admin.NewRecordAsync("account");

        using var all = await org.Admin.UploadAsync("account", account, new UploadItem("bir.pdf", SampleFiles.Pdf()), new UploadItem("iki.png", SampleFiles.Png()), new UploadItem("uc.txt", SampleFiles.Text()));
        all.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = System.Text.Json.JsonDocument.Parse(await all.Content.ReadAsStringAsync(Ct)).RootElement;
        created.GetProperty("items").EnumerateArray().Select(i => i.Str("name")).ShouldBe(["bir.pdf", "iki.png", "uc.txt"]);
        all.Headers.Location.ShouldBeNull("Location yalnız tek dosyada");

        using var mixed = await org.Admin.UploadAsync("account", account, new UploadItem("iyi.pdf", SampleFiles.Pdf()), new UploadItem("rapor.exe", SampleFiles.Exe()), new UploadItem("sahte.pdf", SampleFiles.Exe()));
        var body = await mixed.Content.ReadAsStringAsync(Ct);
        mixed.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
        json.GetProperty("items").GetArrayLength().ShouldBe(1);
        var failed = json.GetProperty("failed").EnumerateArray().ToList();
        failed.Count.ShouldBe(2);
        failed[0].Str("fileName").ShouldBe("rapor.exe");
        failed[0].Str("code").ShouldBe("file.type_not_allowed");
        failed[0].GetProperty("args").Str("extension").ShouldBe("exe");
        failed[1].Str("code").ShouldBe("file.content_mismatch");

        using var none = await org.Admin.UploadAsync("account", account, new UploadItem("bir.exe", SampleFiles.Exe()), new UploadItem("iki.bat", SampleFiles.Exe()));
        await none.ProblemAsync(HttpStatusCode.UnsupportedMediaType, "file.type_not_allowed");
    }

    [Fact]
    public async Task Limits_AreExposedToAnyAuthenticatedUser_AndUsageOnlyToSettingsManagers()
    {
        var org = await factory.NewOrgAsync("Sinirlar");
        var limits = await org.Admin.GetJsonAsync($"{FilesPath}/limits");
        limits.GetProperty("maxFileBytes").GetInt64().ShouldBe(25L * 1024 * 1024);
        limits.GetProperty("maxFilesPerRequest").GetInt32().ShouldBe(10);
        limits.GetProperty("allowedExtensions").EnumerateArray().Select(e => e.GetString()).ShouldContain("pdf");
        limits.GetProperty("allowedExtensions").EnumerateArray().Select(e => e.GetString()).ShouldNotContain("svg");
        limits.GetProperty("previewableExtensions").EnumerateArray().Select(e => e.GetString()).OrderBy(e => e).ShouldBe(["gif", "jpeg", "jpg", "pdf", "png", "webp"]);

        var nobody = await factory.NewMemberAsync(org, "Yetkisiz Uye");
        (await nobody.Client.GetJsonAsync($"{FilesPath}/limits")).GetProperty("maxFileBytes").GetInt64().ShouldBeGreaterThan(0);
        using var usage = await nobody.Client.GetAsync($"{FilesPath}/usage", Ct);
        await usage.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        var account = await org.Admin.NewRecordAsync("account");
        var lead = await org.Admin.NewRecordAsync("lead");
        await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf(1000));
        await org.Admin.UploadOkAsync("account", account, "b.pdf", SampleFiles.Pdf(2000));
        await org.Admin.UploadOkAsync("lead", lead, "c.png", SampleFiles.Png(500));

        var current = await org.Admin.GetJsonAsync($"{FilesPath}/usage");
        current.GetProperty("usedBytes").GetInt64().ShouldBe(3500);
        current.GetProperty("fileCount").GetInt64().ShouldBe(3);
        current.Has("maxBytes").ShouldBeFalse("sınırsız planda yazılmaz");
        current.GetProperty("quarantinedCount").GetInt64().ShouldBe(0);
        current.GetProperty("missingCount").GetInt64().ShouldBe(0);
        var byType = current.GetProperty("byRecordType").EnumerateArray().ToDictionary(e => e.Str("recordType"));
        byType["account"].GetProperty("fileCount").GetInt64().ShouldBe(2);
        byType["account"].GetProperty("sizeBytes").GetInt64().ShouldBe(3000);
        byType["lead"].GetProperty("sizeBytes").GetInt64().ShouldBe(500);
        current.Has("asOf").ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_WritesAFileAttachedOutboxRowWithoutTheFileName_AndAnAuditEntryWithMaskedSecrets()
    {
        var org = await factory.NewOrgAsync("Denetim Outbox");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "GIZLI-SOZLESME-ADI.pdf", SampleFiles.Pdf(777));
        await org.Admin.PatchAsync($"{FilesPath}/{file.Id()}", JsonContentOf(new { name = "YENI-ADI.pdf" }), Ct);

        var payload = await factory.ScalarAsync<string>("SELECT payload::text FROM files.outbox_messages WHERE tenant_id = @t AND type LIKE '%FileAttached%'", ("t", org.TenantId));
        var json = System.Text.Json.JsonDocument.Parse(payload).RootElement;
        json.GetProperty("fileId").GetGuid().ShouldBe(file.Id());
        json.GetProperty("recordId").GetGuid().ShouldBe(account);
        json.Str("recordType").ShouldBe("account");
        json.GetProperty("sizeBytes").GetInt64().ShouldBe(777);
        json.Str("contentType").ShouldBe("application/pdf");
        json.GetProperty("actorUserId").GetGuid().ShouldBe(org.AdminUserId);
        json.Has("name").ShouldBeFalse();
        json.Has("fileName").ShouldBeFalse();
        payload.ShouldNotContain("GIZLI");
        payload.ShouldNotContain(".pdf\"");

        var audits = await factory.ScalarAsync<string>(
            "SELECT string_agg(action || ':' || changes::text, '|' ORDER BY occurred_at) FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type = 'FileAttachment' AND entity_id = @id",
            ("t", org.TenantId), ("id", file.Id().ToString()));
        audits.ShouldContain("created:");
        audits.ShouldContain("updated:");
        audits.ShouldContain("GIZLI-SOZLESME-ADI.pdf");
        audits.ShouldContain("YENI-ADI.pdf");
        audits.ShouldNotContain(org.TenantId.ToString("D"), Case.Insensitive);
        audits.ShouldNotContain(Sha256Hex(SampleFiles.Pdf(777)));
    }

    [Fact]
    public async Task Temp_Turkish_UnicodeFileNames_RoundTripThroughTheMultipartFilenameStar()
    {
        var org = await factory.NewOrgAsync("Unicode Ad");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "İğdır Şubesi – Ağustos ✓.pdf", SampleFiles.Pdf());
        file.Str("name").ShouldBe("İğdır Şubesi – Ağustos ✓.pdf");
        Encoding.UTF8.GetByteCount(file.Str("name")).ShouldBeGreaterThan(file.Str("name").Length);
    }
}
