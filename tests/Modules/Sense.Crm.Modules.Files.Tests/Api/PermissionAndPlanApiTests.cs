using System.Net;
using Sense.Crm.Modules.Files.Tests.Domain;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Files.Tests.Api.FilesKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>
/// İzin matrisi (9 kayıt türü × rol × uç): erişim kararı <b>kaydın kendi izniyle</b> verilir — okuma izni yoksa liste 403, dosya kimliğiyle meta/indirme/önizleme 404 (varlık sızmaz);
/// yazma izni yoksa yükleme/yeniden adlandırma/silme 403; yalnız yazma: yükleme geçer, liste 403. Kapı modülü kapalıysa okuma dahil 403 <c>plan.module_disabled</c>;
/// salt okunur/askıda yazma 403 <c>tenant.suspended</c>, okuma çalışır.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PermissionAndPlanApiTests(CrmApiFactory factory)
{
    public static TheoryData<string> Types() => [.. RecordTypes];

    private sealed record Actor(string Label, HttpClient Client, bool CanRead, bool CanWrite);

    private async Task<(Org Org, Guid RecordId, List<Actor> Actors)> ArrangeAsync(string recordType)
    {
        var org = await factory.NewOrgAsync("Matris " + recordType);
        var recordId = await org.Admin.NewRecordAsync(recordType);
        var (read, write) = Permissions[recordType];
        var otherType = RecordTypes[(Array.IndexOf(RecordTypes, recordType) + 1) % RecordTypes.Length];
        var (otherRead, otherWrite) = Permissions[otherType];

        var standardRole = (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Standard").Id();
        var standard = await factory.AddMemberAsync(org.Admin, "Standart Uye", standardRole);

        var actors = new List<Actor>
        {
            new("administrator", org.Admin, true, true),
            new("standard", standard.Client, true, true),
            new("okur", (await factory.NewMemberAsync(org, "Okur", read)).Client, true, false),
            new("yazar", (await factory.NewMemberAsync(org, "Yazar", write)).Client, false, true),
            new("okur+yazar", (await factory.NewMemberAsync(org, "Tam", read, write)).Client, true, true),
            new("hicbiri", (await factory.NewMemberAsync(org, "Hicbiri")).Client, false, false),
            new("baska tur", (await factory.NewMemberAsync(org, "Baska Tur", otherRead, otherWrite)).Client, false, false),
        };
        return (org, recordId, actors);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task EveryRole_GetsTheDocumentedStatusOnEveryEndpoint(string recordType)
    {
        var (org, recordId, actors) = await ArrangeAsync(recordType);
        var sharedPdf = await org.Admin.UploadOkAsync(recordType, recordId, "ortak.pdf", SampleFiles.Pdf());
        var sharedPng = await org.Admin.UploadOkAsync(recordType, recordId, "ortak.png", SampleFiles.Png());

        foreach (var actor in actors)
        {
            var who = $"{recordType}/{actor.Label}";

            // Liste (okuma).
            using (var list = await actor.Client.GetAsync($"{FilesPath}?recordType={recordType}&recordId={recordId}", Ct))
            {
                if (actor.CanRead)
                {
                    list.StatusCode.ShouldBe(HttpStatusCode.OK, $"{who} liste");
                }
                else
                {
                    await list.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
                }
            }

            // Meta, indirme, önizleme (okuma; izinsizde 404).
            foreach (var (label, send) in new (string, Func<Task<HttpResponseMessage>>)[]
            {
                ("meta", () => actor.Client.GetAsync($"{FilesPath}/{sharedPdf.Id()}", Ct)),
                ("indirme", () => actor.Client.ContentAsync(sharedPdf.Id())),
                ("onizleme", () => actor.Client.ContentAsync(sharedPng.Id(), "inline")),
            })
            {
                using var response = await send();
                if (actor.CanRead)
                {
                    response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{who} {label}");
                }
                else
                {
                    await response.ProblemAsync(HttpStatusCode.NotFound, "not_found");
                }
            }

            // Yükleme (yazma).
            using (var upload = await actor.Client.UploadAsync(recordType, recordId, new UploadItem($"{actor.Label}.pdf".Replace('+', '-').Replace(' ', '-'), SampleFiles.Pdf())))
            {
                if (actor.CanWrite)
                {
                    upload.StatusCode.ShouldBe(HttpStatusCode.Created, $"{who} yükleme");
                }
                else
                {
                    await upload.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
                }
            }

            // Yeniden adlandırma ve silme (yazma; taze dosyalarla).
            var renameTarget = await org.Admin.UploadOkAsync(recordType, recordId, "adlandirilacak.pdf", SampleFiles.Pdf());
            using (var rename = await actor.Client.PatchAsync($"{FilesPath}/{renameTarget.Id()}", System.Net.Http.Json.JsonContent.Create(new { name = "yeni.pdf" }), Ct))
            {
                if (actor.CanWrite)
                {
                    rename.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{who} yeniden adlandırma");
                }
                else
                {
                    await rename.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
                }
            }

            var deleteTarget = await org.Admin.UploadOkAsync(recordType, recordId, "silinecek.pdf", SampleFiles.Pdf());
            using var delete = await actor.Client.DeleteAsync($"{FilesPath}/{deleteTarget.Id()}", Ct);
            if (actor.CanWrite)
            {
                delete.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{who} silme");
            }
            else
            {
                await delete.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }
        }
    }

    [Fact]
    public async Task MatrixTypesMatchThePlanTable_AndAreTheSamePermissionKeysTheModulesDefine()
    {
        // Tablo (test kopyası) ↔ gerçek izin kataloğu: her anahtar /permissions listesinde bulunur.
        var org = await factory.NewOrgAsync("Izin Tablosu");
        var catalog = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => p.Str("key")).ToHashSet(StringComparer.Ordinal);
        foreach (var (type, (read, write)) in Permissions)
        {
            catalog.ShouldContain(read, $"{type} okuma izni katalogda olmalı");
            catalog.ShouldContain(write, $"{type} yazma izni katalogda olmalı");
        }

        Permissions.Count.ShouldBe(RecordTypes.Length);
    }

    [Fact]
    public async Task FilePermissionsFollowTheRecord_NotAFilesPermission_SoThereIsNoCrmFilesPermissionKey()
    {
        var org = await factory.NewOrgAsync("Izin Katalogu");
        var catalog = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => p.Str("key")).ToList();
        catalog.Where(k => k.Contains("files", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletedRecord_HidesItsFiles_ListSaysRecordNotFound_MetaAndContentAre404()
    {
        var org = await factory.NewOrgAsync("Silinmis Kayit");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());

        await org.Admin.DeleteRecordAsync("account", account);

        using (var list = await org.Admin.GetAsync($"{FilesPath}?recordType=account&recordId={account}", Ct))
        {
            await list.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
        }

        using (var meta = await org.Admin.GetAsync($"{FilesPath}/{file.Id()}", Ct))
        {
            await meta.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        using (var content = await org.Admin.ContentAsync(file.Id()))
        {
            await content.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        using (var upload = await org.Admin.UploadAsync("account", account, new UploadItem("b.pdf", SampleFiles.Pdf())))
        {
            await upload.ProblemAsync(HttpStatusCode.NotFound, "file.record_not_found");
        }

        using var rename = await org.Admin.PatchAsync($"{FilesPath}/{file.Id()}", System.Net.Http.Json.JsonContent.Create(new { name = "x.pdf" }), Ct);
        await rename.ProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("order")]
    [InlineData("case")]
    [InlineData("campaign")]
    public async Task GatedModuleTypes_AreDisabledOnEveryEndpoint_WhenThePlanClosesTheModule_ReadsIncluded(string recordType)
    {
        var org = await factory.NewOrgAsync("Kapi " + recordType);
        var recordId = await org.Admin.NewRecordAsync(recordType);
        var file = await org.Admin.UploadOkAsync(recordType, recordId, "a.pdf", SampleFiles.Pdf());
        var png = await org.Admin.UploadOkAsync(recordType, recordId, "a.png", SampleFiles.Png());

        var platform = await factory.PlatformAdminAsync();
        var closed = await factory.EnsurePlanAsync("gateoff", null, AllModulesOff);
        await platform.PutSubscriptionAsync(org.TenantId, closed);

        var module = recordType is "quote" or "order" ? "commerce" : recordType == "case" ? "service" : "marketing";
        foreach (var send in new Func<Task<HttpResponseMessage>>[]
        {
            () => org.Admin.GetAsync($"{FilesPath}?recordType={recordType}&recordId={recordId}", Ct),
            () => org.Admin.UploadAsync(recordType, recordId, new UploadItem("b.pdf", SampleFiles.Pdf())),
            () => org.Admin.GetAsync($"{FilesPath}/{file.Id()}", Ct),
            () => org.Admin.ContentAsync(file.Id()),
            () => org.Admin.ContentAsync(png.Id(), "inline"),
            () => org.Admin.PatchAsync($"{FilesPath}/{file.Id()}", System.Net.Http.Json.JsonContent.Create(new { name = "x.pdf" }), Ct),
            () => org.Admin.DeleteAsync($"{FilesPath}/{file.Id()}", Ct),
        })
        {
            using var response = await send();
            var problem = await response.ProblemAsync(HttpStatusCode.Forbidden, "plan.module_disabled");
            problem.GetProperty("args").Str("module").ShouldBe(module);
        }

        // Çekirdek modüller etkilenmez.
        var account = await org.Admin.NewRecordAsync("account");
        await org.Admin.UploadOkAsync("account", account, "cekirdek.pdf", SampleFiles.Pdf());

        // Modül geri açılınca dosyalar erişilebilir.
        await platform.PutSubscriptionAsync(org.TenantId, await factory.EnsurePlanAsync("gateon", null, AllModulesOn));
        (await org.Admin.ContentAsync(file.Id())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadOnlyAndSuspendedTenants_CanListAndDownload_ButCannotUploadRenameOrDelete()
    {
        var org = await factory.NewOrgAsync("Askida Dosya");
        var account = await org.Admin.NewRecordAsync("account");
        var file = await org.Admin.UploadOkAsync("account", account, "a.pdf", SampleFiles.Pdf());
        var other = await org.Admin.UploadOkAsync("account", account, "b.pdf", SampleFiles.Pdf());
        var platform = await factory.PlatformAdminAsync();

        await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{org.TenantId}/suspend", new { reason = "test", mode = "readOnly" }, HttpStatusCode.NoContent);

        using (var upload = await org.Admin.UploadAsync("account", account, new UploadItem("c.pdf", SampleFiles.Pdf())))
        {
            var problem = await upload.ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
            problem.GetProperty("args").Str("reason").ShouldBe("suspended");
        }

        using (var rename = await org.Admin.PatchAsync($"{FilesPath}/{file.Id()}", System.Net.Http.Json.JsonContent.Create(new { name = "x.pdf" }), Ct))
        {
            await rename.ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        }

        using (var delete = await org.Admin.DeleteAsync($"{FilesPath}/{other.Id()}", Ct))
        {
            await delete.ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        }

        (await org.Admin.ListAsync("account", account)).GetProperty("totalCount").GetInt64().ShouldBe(2);
        (await org.Admin.GetJsonAsync($"{FilesPath}/{file.Id()}")).Str("name").ShouldBe("a.pdf");
        using (var content = await org.Admin.ContentAsync(file.Id()))
        {
            content.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await org.Admin.GetJsonAsync($"{FilesPath}/usage")).GetProperty("fileCount").GetInt64().ShouldBe(2);

        // Tam engel: her istek kesilir (okuma dahil).
        await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{org.TenantId}/reactivate", null, HttpStatusCode.NoContent);
        await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{org.TenantId}/suspend", new { reason = "test", mode = "blocked" }, HttpStatusCode.NoContent);
        using (var blockedList = await org.Admin.GetAsync($"{FilesPath}?recordType=account&recordId={account}", Ct))
        {
            await blockedList.ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        }

        using (var blockedContent = await org.Admin.ContentAsync(file.Id()))
        {
            await blockedContent.ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        }

        await platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{org.TenantId}/reactivate", null, HttpStatusCode.NoContent);
        await org.Admin.UploadOkAsync("account", account, "yeniden.pdf", SampleFiles.Pdf());
    }
}
