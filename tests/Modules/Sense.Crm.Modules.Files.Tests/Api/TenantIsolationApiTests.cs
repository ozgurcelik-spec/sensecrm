using System.Net;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Tests.Domain;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Files.Tests.Api.FilesKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>Kiracı izolasyonu (zorunlu): başka kiracının dosyası/kaydı her zaman <c>not_found</c> (varolmayan kimlikle aynı yanıt); kurcalanmış satır başka kiracının nesnesini okutamaz.</summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationApiTests(CrmApiFactory factory)
{
    private static async Task<(int Status, string Code, string Title, string Detail)> ShapeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
        return ((int)response.StatusCode, json.Str("code"), json.Str("title"), json.Str("detail"));
    }

    [Fact]
    public async Task AnotherTenantsFile_IsNotFoundOnEveryEndpoint_WithTheSameAnswerAsAnUnknownId()
    {
        var a = await factory.NewOrgAsync("Izolasyon A");
        var b = await factory.NewOrgAsync("Izolasyon B");
        var bAccount = await b.Admin.NewRecordAsync("account");
        var bFile = await b.Admin.UploadOkAsync("account", bAccount, "gizli.pdf", SampleFiles.Pdf(1234));
        var unknown = Guid.NewGuid();

        foreach (var (label, send) in new (string, Func<Guid, Task<HttpResponseMessage>>)[]
        {
            ("meta", id => a.Admin.GetAsync($"{FilesPath}/{id}", Ct)),
            ("indirme", id => a.Admin.ContentAsync(id)),
            ("onizleme", id => a.Admin.ContentAsync(id, "inline")),
            ("yeniden adlandirma", id => a.Admin.PatchAsync($"{FilesPath}/{id}", System.Net.Http.Json.JsonContent.Create(new { name = "x.pdf" }), Ct)),
            ("silme", id => a.Admin.DeleteAsync($"{FilesPath}/{id}", Ct)),
        })
        {
            using var foreign = await send(bFile.Id());
            using var missing = await send(unknown);
            var foreignShape = await ShapeAsync(foreign);
            foreignShape.Status.ShouldBe(404, label);
            foreignShape.Code.ShouldBe("not_found", label);
            foreignShape.ShouldBe(await ShapeAsync(missing), $"{label}: başka kiracı ile olmayan kimlik ayırt edilemez olmalı");
        }

        // B'nin dosyası dokunulmadan duruyor.
        (await b.Admin.GetJsonAsync($"{FilesPath}/{bFile.Id()}")).Str("name").ShouldBe("gizli.pdf");
        using var stillThere = await b.Admin.ContentAsync(bFile.Id());
        stillThere.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnotherTenantsRecordId_IsRecordNotFound_ForListAndUpload()
    {
        var a = await factory.NewOrgAsync("Izolasyon Kayit A");
        var b = await factory.NewOrgAsync("Izolasyon Kayit B");
        foreach (var type in RecordTypes.Where(t => t is "account" or "lead" or "activity" or "campaign"))
        {
            var bRecord = await b.Admin.NewRecordAsync(type);
            await b.Admin.UploadOkAsync(type, bRecord, "b.pdf", SampleFiles.Pdf());

            using var list = await a.Admin.GetAsync($"{FilesPath}?recordType={type}&recordId={bRecord}", Ct);
            await list.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
            using var upload = await a.Admin.UploadAsync(type, bRecord, new UploadItem("a.pdf", SampleFiles.Pdf()));
            await upload.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
        }

        factory.ObjectKeys(a.TenantId).ShouldBeEmpty("A'nın önekine hiçbir şey yazılmadı");
    }

    [Fact]
    public async Task ListsUsageAndLimits_OnlyShowTheOwnTenant()
    {
        var a = await factory.NewOrgAsync("Izolasyon Liste A");
        var b = await factory.NewOrgAsync("Izolasyon Liste B");
        var aAccount = await a.Admin.NewRecordAsync("account");
        var bAccount = await b.Admin.NewRecordAsync("account");
        await a.Admin.UploadOkAsync("account", aAccount, "a.pdf", SampleFiles.Pdf(1000));
        await b.Admin.UploadOkAsync("account", bAccount, "b1.pdf", SampleFiles.Pdf(2000));
        await b.Admin.UploadOkAsync("account", bAccount, "b2.pdf", SampleFiles.Pdf(3000));

        (await a.Admin.ListAsync("account", aAccount)).GetProperty("items").EnumerateArray().Select(i => i.Str("name")).ShouldBe(["a.pdf"]);
        var usageA = await a.Admin.GetJsonAsync($"{FilesPath}/usage");
        usageA.GetProperty("usedBytes").GetInt64().ShouldBe(1000);
        usageA.GetProperty("fileCount").GetInt64().ShouldBe(1);
        (await b.Admin.GetJsonAsync($"{FilesPath}/usage")).GetProperty("usedBytes").GetInt64().ShouldBe(5000);
    }

    [Fact]
    public async Task ATamperedRowPointingAtAnotherTenantsObject_CannotServeItsBytes_NotFoundNotServerError_WithASecurityLog()
    {
        await using var host = FilesHost.Create(factory);
        var a = await host.App.NewOrgAsync("Kurcalama A");
        var b = await host.App.NewOrgAsync("Kurcalama B");
        var aAccount = await a.Admin.NewRecordAsync("account");
        var bAccount = await b.Admin.NewRecordAsync("account");
        var bBytes = SampleFiles.Pdf(4321);
        var bFile = await b.Admin.UploadOkAsync("account", bAccount, "b-gizli.pdf", bBytes);
        var bKey = host.App.ObjectKeys(b.TenantId).Single();

        // A'nın satırı B önekli anahtara bağlanır (el yazımı satır; B'nin satırından farklı bir kimlik → benzersizlik ihlali yok, anahtar B'nin satırınınkiyle çakışmasın diye
        // B'nin nesnesinin bir kopyası B önekinde başka fileId ile tohumlanır).
        var forgedFileId = Guid.CreateVersion7();
        var forgedKey = ObjectKey.For(b.TenantId, 2026, forgedFileId).ToString();
        host.App.Store().SeedRaw(forgedKey, bBytes);
        await InsertRowAsync(a, aAccount, forgedFileId, forgedKey, bBytes);

        using var response = await a.Admin.ContentAsync(forgedFileId);
        await response.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("%PDF");
        host.Logs.Lines.Any(l => l.Contains("Security:", StringComparison.Ordinal) && l.Contains("does not match tenant", StringComparison.Ordinal)).ShouldBeTrue();
        host.Logs.Lines.Where(l => l.Contains(forgedKey, StringComparison.OrdinalIgnoreCase) || l.Contains("b-gizli", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty("anahtar/ad günlüğe yazılmaz");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM files.file_access_log WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(0);

        // Anahtar kendi önekinde ama başka fileId taşıyorsa da reddedilir (anahtar ↔ satır kimliği uyuşmazlığı).
        var mismatchedId = Guid.CreateVersion7();
        var ownKeyOtherId = ObjectKey.For(a.TenantId, 2026, Guid.CreateVersion7()).ToString();
        host.App.Store().SeedRaw(ownKeyOtherId, bBytes);
        await InsertRowAsync(a, aAccount, mismatchedId, ownKeyOtherId, bBytes);
        using var second = await a.Admin.ContentAsync(mismatchedId);
        await second.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Bozuk anahtar biçimi de aynı şekilde reddedilir.
        var garbageId = Guid.CreateVersion7();
        await InsertRowAsync(a, aAccount, garbageId, "../../etc/passwd", bBytes);
        using var third = await a.Admin.ContentAsync(garbageId);
        await third.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        // B'nin gerçek dosyası etkilenmez.
        using var real = await b.Admin.ContentAsync(bFile.Id());
        (await real.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(bBytes);
        _ = bKey;
    }

    private async Task InsertRowAsync(Org org, Guid recordId, Guid id, string storageKey, byte[] bytes) =>
        await factory.SqlAsync(
            """
            INSERT INTO files.attachments
                (id, tenant_id, record_type, record_id, name, extension, content_type, size_bytes, sha256, storage_key, state, scan_status, uploaded_by_user_id, uploaded_at, created_at)
            VALUES (@id, @t, 'account', @r, 'forged.pdf', 'pdf', 'application/pdf', @size, @sha, @key, 'ready', 'skipped', @u, now(), now())
            """,
            ("id", id), ("t", org.TenantId), ("r", recordId), ("size", (long)bytes.Length), ("sha", Sha256Hex(bytes)), ("key", storageKey), ("u", org.AdminUserId));
}
