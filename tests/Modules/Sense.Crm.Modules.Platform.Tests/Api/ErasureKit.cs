using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Tests.Shared.Fixtures;
using Sense.Crm.Tests.Shared.Workflows;
using Shouldly;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>Kiracıya ait bir tablo: EF modelinden yansımayla üretilir (yeni tablo/modül otomatik kapsanır).</summary>
internal sealed record ErasureTable(string Schema, string Table, string TenantColumn, bool IsOutbox)
{
    public string Name => $"{Schema}.{Table}";
}

/// <summary>Testte açılmış üye: kimlik, e-posta ve oturum açmış istemci.</summary>
internal sealed record ErasureMember(Guid UserId, string Email, HttpClient Client);

/// <summary>Silme talebinin yanıtı.</summary>
internal sealed record ErasureRequestInfo(Guid RequestId, DateTimeOffset ScheduledFor);

/// <summary>Senaryo veri düzeyi: <c>None</c> yalnız kiracılar/üyeler, <c>Light</c> Sales/Activities çekirdeği, <c>Full</c> yedi iş modülünün tamamı.</summary>
internal enum SeedLevel
{
    None,
    Light,
    Full,
}

/// <summary>
/// İmha adımı gözlemcisi: <see cref="ProbeEraser"/> örneklerinin çağrı sayısını tutar ve sınırlı sayıda hata fırlatır (yarım kalma/yeniden deneme testleri).
/// Yalnız <see cref="Tenant"/> kiracısı için etkindir; diğer kiracıların imhasına (aynı DB'deki başka testlerin talepleri) dokunmaz.
/// </summary>
internal sealed class ErasureProbe
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private int _failuresLeft;

    public Guid Tenant { get; set; }

    /// <summary>Kalan hata sayısı (<c>int.MaxValue</c> = hep hata).</summary>
    public int FailuresLeft
    {
        get => Volatile.Read(ref _failuresLeft);
        set => Volatile.Write(ref _failuresLeft, value);
    }

    public int Calls(string step) => _calls.GetValueOrDefault(step);

    internal void Record(string step) => _calls.AddOrUpdate(step, 1, (_, count) => count + 1);

    internal bool TryConsumeFailure()
    {
        while (true)
        {
            var current = FailuresLeft;
            if (current <= 0)
            {
                return false;
            }

            var next = current == int.MaxValue ? current : current - 1;
            if (Interlocked.CompareExchange(ref _failuresLeft, next, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>Test imha adımı: çağrıyı sayar; <paramref name="canFail"/> ise probe izin verdikçe fırlatır.</summary>
internal sealed class ProbeEraser(string name, int order, ErasureProbe probe, bool canFail) : ITenantDataEraser
{
    public string Name => name;

    public int Order => order;

    public Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        if (tenantId != probe.Tenant)
        {
            return Task.FromResult(EraseReport.Empty);
        }

        probe.Record(name);
        if (canFail && probe.TryConsumeFailure())
        {
            throw new InvalidOperationException("Simulated erase failure");
        }

        return Task.FromResult(new EraseReport(new Dictionary<string, long> { ["probe.rows"] = 1 }));
    }
}

/// <summary>İmha testlerinin ortak yardımcıları (yansıma ile tablo keşfi, üye açma, SQL sorguları).</summary>
internal static class ErasureKit
{
    /// <summary>Beklenen imha adımları (işin sırasıyla): erased_steps bunların üst kümesi olmalıdır.</summary>
    public static readonly string[] ExpectedSteps =
    [
        "identity-accounts",
        "workflows-conductor",
        "module:activities",
        "module:commerce",
        "module:identity",
        "module:marketing",
        "module:platform",
        "module:sales",
        "module:service",
        "module:workflows",
        "identity-tenant",
        "audit",
        TenantErasureJob.TombstoneStep,
    ];

    public static readonly string[] BusinessSchemas = ["sales", "activities", "workflows", "marketing", "commerce", "service"];

    public static string ErStr(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static Guid ErGuid(this JsonElement element, string property) => element.GetProperty(property).GetGuid();

    /// <summary>
    /// Host'un tüm modül DbContext'lerinin EF modelinden <b>her</b> <see cref="ITenantEntity"/> tablosunu (denetim hariç), modül outbox'larını ve
    /// ortak <c>audit.audit_log_entries</c> tablosunu üretir. Yansımayla üretildiği için yeni tablolar otomatik kapsanır.
    /// </summary>
    public static IReadOnlyList<ErasureTable> TenantTables(WebApplicationFactory<Program> host)
    {
        var tables = new Dictionary<string, ErasureTable>(StringComparer.Ordinal);
        using var scope = host.Services.CreateScope();
        foreach (var context in scope.ServiceProvider.GetServices<ModuleDbContext>())
        {
            foreach (var entity in context.Model.GetEntityTypes())
            {
                if (entity.IsOwned() || entity.ClrType == typeof(AuditLogEntry) || !typeof(ITenantEntity).IsAssignableFrom(entity.ClrType) || entity.GetTableName() is not { } table)
                {
                    continue;
                }

                var schema = entity.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public";
                var column = entity.FindProperty(nameof(ITenantEntity.TenantId))?.GetColumnName(StoreObjectIdentifier.Table(table, entity.GetSchema()));
                if (column is not null)
                {
                    tables[$"{schema}.{table}"] = new ErasureTable(schema, table, column, IsOutbox: false);
                }
            }

            if (context.Model.FindEntityType(typeof(OutboxMessage)) is { } outbox && outbox.GetTableName() is { } outboxTable)
            {
                var schema = outbox.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public";
                var column = outbox.FindProperty(nameof(OutboxMessage.TenantId))?.GetColumnName(StoreObjectIdentifier.Table(outboxTable, outbox.GetSchema()));
                if (column is not null)
                {
                    tables[$"{schema}.{outboxTable}"] = new ErasureTable(schema, outboxTable, column, IsOutbox: true);
                }
            }
        }

        tables[$"{AuditLogTables.Schema}.{AuditLogTables.TableName}"] = new ErasureTable(AuditLogTables.Schema, AuditLogTables.TableName, "tenant_id", IsOutbox: false);
        return tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Sözlüğü sıralı "anahtar=değer" satırlarına çevirir (fark iletisi okunur olsun diye).</summary>
    public static string Format(IEnumerable<KeyValuePair<string, long>> counts) =>
        string.Join('\n', counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"));

    /// <summary>Tek sütunlu (metin) sorgunun tüm satırları.</summary>
    public static async Task<List<string?>> ErQueryAsync(this CrmApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var rows = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        }

        return rows;
    }

    /// <summary>Yönetici istemcisiyle yeni hesaplı üye ekler ve ilk giriş + parola değişimini <paramref name="host"/> üzerinden tamamlar (geçici parola zorlaması).</summary>
    public static async Task<ErasureMember> ErasureAddMemberAsync(WebApplicationFactory<Program> host, HttpClient admin, string displayName, Guid roleId)
    {
        var email = UniqueEmail("member");
        var added = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/members", new { email, displayName, roleId }, HttpStatusCode.Created);
        var client = host.CreateClient();
        await client.ActivateAsync(email, added.ErStr("temporaryPassword"));
        return new ErasureMember(added.ErGuid("userId"), email, client);
    }

    /// <summary>Yönetici, e-postası verilen mevcut hesabı organizasyona davet eder; hesap sahibi (<paramref name="invitee"/>) daveti kabul eder.</summary>
    public static async Task InviteAndAcceptAsync(HttpClient admin, string email, Guid roleId, HttpClient invitee, Guid organizationId)
    {
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/members", new { email, roleId }, HttpStatusCode.Created);
        var invitations = await invitee.GetJsonAsync($"{Base}/me/invitations");
        var invitation = invitations.EnumerateArray().Single(i => i.ErGuid("organizationId") == organizationId);
        (await invitee.PostAsync($"{Base}/me/invitations/{invitation.ErGuid("id")}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public static async Task<Guid> StandardRoleAsync(HttpClient admin) =>
        (await admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.ErStr("name") == "Standard").ErGuid("id");

    /// <summary>
    /// Bir tablonun kiracı satırlarını yeni kimliklerle (<c>id</c> = <c>gen_random_uuid()</c>, <c>tenant_id</c> = hedef) çoğaltır: yedekten geri yükleme benzetimi.
    /// Kaynak satır yoksa 0 döner.
    /// </summary>
    public static async Task<int> CloneTenantRowsAsync(this CrmApiFactory factory, string schema, string table, Guid fromTenant, Guid toTenant, int limit)
    {
        var columns = (await factory.ErQueryAsync(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY ordinal_position",
            ("s", schema), ("t", table))).Select(c => c!).ToList();
        var names = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var values = string.Join(", ", columns.Select(c => c switch { "id" => "gen_random_uuid()", "tenant_id" => "@to", _ => $"\"{c}\"" }));
        return await factory.SqlAsync(
            $"INSERT INTO \"{schema}\".\"{table}\" ({names}) SELECT {values} FROM \"{schema}\".\"{table}\" WHERE tenant_id = @from LIMIT {limit}",
            ("from", fromTenant), ("to", toTenant));
    }

    public static void AddProbeErasers(this IServiceCollection services, ErasureProbe probe)
    {
        services.AddScoped<ITenantDataEraser>(_ => new ProbeEraser("test-before", 60, probe, canFail: false));
        services.AddScoped<ITenantDataEraser>(_ => new ProbeEraser("test-flaky", 700, probe, canFail: true));
    }
}

/// <summary>
/// İki kiracılı (A silinecek, B kalacak) imha senaryosu: sahte saatli ayrı bir API host'u (aynı veritabanı, <see cref="TimeProvider"/> = <see cref="TestClock"/>),
/// platform yöneticisi, A'ya özel üye, A ve B'nin <b>ortak</b> hesabı ve A'da üye olan platform yöneticisi. Veri gerçek HTTP uçlarıyla üretilir.
/// Tek host kullanılır: varlık önbelleği (silme bekleyen kiracı) ve sahte motor aynı süreçte tutarlı kalır.
/// </summary>
internal sealed class ErasureScenario : IDisposable
{
    private ErasureScenario(CrmApiFactory factory, WebApplicationFactory<Program> host, TestClock clock, string tag)
    {
        Factory = factory;
        Host = host;
        Clock = clock;
        Tag = tag;
    }

    public CrmApiFactory Factory { get; }

    public WebApplicationFactory<Program> Host { get; }

    public TestClock Clock { get; }

    /// <summary>Senaryoya özgü rastgele işaret: tüm tohum verinin adında geçer (kişisel veri sızıntısı denetimi için).</summary>
    public string Tag { get; }

    public FakeWorkflowEngine Engine => Host.Services.GetRequiredService<FakeWorkflowEngine>();

    public HttpClient Platform { get; private set; } = null!;

    public Guid PlatformAdminUserId { get; private set; }

    public string PlatformAdminEmail { get; private set; } = string.Empty;

    public TestOrg A { get; private set; } = null!;

    public TestOrg B { get; private set; } = null!;

    /// <summary>Yalnız A'da üye olan ikinci hesap.</summary>
    public ErasureMember AMember { get; private set; } = null!;

    /// <summary>Hem A'da hem B'de üye olan hesap.</summary>
    public ErasureMember Shared { get; private set; } = null!;

    public IReadOnlyList<ErasureTable> Tables { get; private set; } = [];

    /// <summary>B kiracısının imhadan önceki tablo başına satır sayıları.</summary>
    public IReadOnlyDictionary<string, long> BBefore { get; private set; } = new Dictionary<string, long>();

    public ErasureRequestInfo Request { get; private set; } = null!;

    /// <summary>Kişisel veri/ad işaretleri: imha raporunda ve mezar taşında bulunmamalıdır.</summary>
    public IReadOnlyList<string> PersonalMarkers =>
        [Tag, A.AdminEmail, AMember.Email, Shared.Email, PlatformAdminEmail, "@example.com"];

    public static async Task<ErasureScenario> CreateAsync(
        CrmApiFactory factory,
        SeedLevel seed = SeedLevel.Light,
        IReadOnlyDictionary<string, string>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        // Jeton doğrulaması gerçek saatle yapılır: sahte saat gerçek saatte başlar, imha zamanlaması için sonra ileri alınır.
        var now = DateTimeOffset.UtcNow;
        var clock = new TestClock(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero));
        var host = factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(collection =>
            {
                collection.RemoveAll<TimeProvider>();
                collection.AddSingleton<TimeProvider>(clock);
                services?.Invoke(collection);
            });
        });

        var scenario = new ErasureScenario(factory, host, clock, "Ers" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await scenario.InitializeAsync(seed);
            return scenario;
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    public void Dispose() => Host.Dispose();

    // ---- Kurulum -----------------------------------------------------------------------------------------------------

    private async Task InitializeAsync(SeedLevel seed)
    {
        Platform = await Host.PlatformAdminAsync();
        var me = (await Platform.GetJsonAsync($"{Base}/me")).GetProperty("user");
        PlatformAdminUserId = me.ErGuid("id");
        PlatformAdminEmail = me.ErStr("email");

        A = await Host.NewOrgAsync($"Erasure A {Tag}");
        B = await Host.NewOrgAsync($"Erasure B {Tag}");
        var roleA = await ErasureKit.StandardRoleAsync(A.Admin);
        var roleB = await ErasureKit.StandardRoleAsync(B.Admin);

        AMember = await ErasureKit.ErasureAddMemberAsync(Host, A.Admin, $"A Uyesi {Tag}", roleA);
        Shared = await ErasureKit.ErasureAddMemberAsync(Host, A.Admin, $"Ortak Uye {Tag}", roleA);
        await ErasureKit.InviteAndAcceptAsync(B.Admin, Shared.Email, roleB, Shared.Client, B.TenantId);
        await ErasureKit.InviteAndAcceptAsync(A.Admin, PlatformAdminEmail, roleA, Platform, A.TenantId);

        await SeedAsync(A, seed);
        await SeedAsync(B, seed);
        await SettleAsync();

        Tables = ErasureKit.TenantTables(Host);
        BBefore = await CountsAsync(B.TenantId);
    }

    private async Task SeedAsync(TestOrg org, SeedLevel level)
    {
        if (level == SeedLevel.None)
        {
            return;
        }

        var admin = org.Admin;
        var tag = $"{Tag} {(org == A ? "A" : "B")}";
        var created = HttpStatusCode.Created;
        var noContent = HttpStatusCode.NoContent;

        if (level == SeedLevel.Full)
        {
            var approverRole = (await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/roles", new { name = $"Onaycilar {tag}", permissions = new[] { "crm.approvals.decide", "crm.deals.read" } }, created)).ErGuid("id");
            var salesRole = (await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/roles", new { name = $"Satis {tag}", permissions = new[] { "crm.leads.read", "crm.activities.read" } }, created)).ErGuid("id");
            await ErasureKit.ErasureAddMemberAsync(Host, admin, $"Onayci {tag}", approverRole);
            await ErasureKit.ErasureAddMemberAsync(Host, admin, $"Satisci {tag}", salesRole);
            await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/workflows/rules", new { name = $"Buyuk firsat onayi {tag}", kind = "dealApproval", @params = new { minAmount = 1000, approverRoleId = approverRole } }, created);
            await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/workflows/rules", new { name = $"Lead atama {tag}", kind = "leadAssignment", @params = new { assigneeRoleId = salesRole } }, created);
        }

        // Sales: hesap/kişi/lead/fırsat + yumuşak silinenler.
        var account = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/accounts", new { name = $"Firma {tag} Ana" }, created);
        var doomedAccount = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/accounts", new { name = $"Firma {tag} Silinecek" }, created);
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/contacts", new { firstName = "Ada", lastName = $"Soyad {tag}", accountId = account.ErGuid("id") }, created);
        var doomedContact = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/contacts", new { firstName = "Sil", lastName = $"Silinecek {tag}" }, created);
        var lead1 = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { firstName = "Lea", lastName = $"Aday Bir {tag}", company = $"Sirket {tag}", source = "web" }, created);
        var lead2 = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { firstName = "Leo", lastName = $"Aday Iki {tag}", company = $"Sirket {tag}", source = "web" }, created);
        var deal = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/deals", new { name = $"Firsat {tag}", accountId = account.ErGuid("id"), amount = 90_000m }, created);
        await admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/accounts/{doomedAccount.ErGuid("id")}", null, noContent);
        await admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/contacts/{doomedContact.ErGuid("id")}", null, noContent);

        // Activities.
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/activities", new { type = "task", subject = $"Gorev {tag}" }, created);
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/activities", new { type = "note", subject = $"Not {tag}", description = $"Not govdesi {tag}" }, created);

        if (level != SeedLevel.Full)
        {
            return;
        }

        // Workflows: kazanılan fırsat onay yürütmesi (HUMAN görevinde bekler) + lead atama yürütmeleri (rule'lar yukarıda açıldı).
        var wonStage = (await admin.GetJsonAsync($"{Base}/pipelines")).EnumerateArray()
            .Single(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("stages").EnumerateArray()
            .Single(s => s.ErStr("kind") == "won").ErGuid("id");
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/deals/{deal.ErGuid("id")}/stage", new { stageId = wonStage }, noContent);

        // Marketing: kampanya + üyeler.
        var campaign = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/campaigns", new { name = $"Kampanya {tag}", type = "email" }, created);
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/campaigns/{campaign.ErGuid("id")}/members", new { memberType = "lead", memberIds = new[] { lead1.ErGuid("id"), lead2.ErGuid("id") } }, HttpStatusCode.OK);

        // Commerce: ürün/teklif/sipariş (kalemli) + yumuşak silinenler.
        var product = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/products", new { name = $"Urun {tag}", unitPrice = 100m, taxRate = 20m }, created);
        var doomedProduct = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/products", new { name = $"Urun {tag} Silinecek", unitPrice = 50m, taxRate = 20m }, created);
        var line = new { productId = product.ErGuid("id"), description = $"Lisans {tag}", quantity = 2m, unitPrice = 100m, discountPercent = 0m, taxRate = 20m };
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/quotes", new { subject = $"Teklif {tag}", accountId = account.ErGuid("id"), lines = new[] { line } }, created);
        var draftQuote = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/quotes", new { subject = $"Taslak {tag}", accountId = account.ErGuid("id"), lines = new[] { line } }, created);
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/orders", new { subject = $"Siparis {tag}", accountId = account.ErGuid("id"), lines = new[] { line } }, created);
        await admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/quotes/{draftQuote.ErGuid("id")}", null, noContent);
        await admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/products/{doomedProduct.ErGuid("id")}", null, noContent);

        // Service: talep + yorumlar + olay satırları.
        var supportCase = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/cases", new { subject = $"Destek {tag}" }, created);
        var caseId = supportCase.ErGuid("id");
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/cases/{caseId}/comments", new { visibility = "internal", body = $"Dahili not {tag}" }, created);
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/cases/{caseId}/comments", new { visibility = "public", body = $"Musteri yaniti {tag}" }, created);
        await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/cases/{caseId}/status", new { status = "resolved", resolutionNote = $"Cozuldu {tag}" }, noContent);
    }

    /// <summary>Outbox'ları ve sahte motoru (Worker taklidi) boşaltır: olay zincirleri (Workflows yürütmeleri, Platform hesapları) tamamlanır.</summary>
    public async Task SettleAsync()
    {
        for (var round = 0; round < 3; round++)
        {
            await Host.DrainOutboxesAsync();
            await Engine.DrainAsync();
        }
    }

    // ---- Eylemler ----------------------------------------------------------------------------------------------------

    public async Task<ErasureRequestInfo> RequestDeletionAsync(int? retentionDays = 7, string reason = "KVKK silme talebi")
    {
        var body = await Platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{A.TenantId}/deletion-request", new { reason, retentionDays }, HttpStatusCode.OK);
        Request = new ErasureRequestInfo(body.ErGuid("requestId"), body.GetProperty("scheduledFor").GetDateTimeOffset());
        return Request;
    }

    public Task<JsonElement> CancelDeletionAsync(HttpStatusCode expected = HttpStatusCode.NoContent) =>
        Platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{A.TenantId}/deletion-request/cancel", null, expected);

    /// <summary>Sahte saati bekleme süresinin bir dakika ötesine alır (talep iş kuyruğunda).</summary>
    public void AdvancePastSchedule() => Clock.SetUtcNow(Request.ScheduledFor.AddMinutes(1));

    public Task<TenantErasureRun> RunJobAsync() => Host.Services.GetRequiredService<TenantErasureJob>().RunOnceAsync(Ct);

    /// <summary>Platform yöneticisinin A dışındaki üyeliklerini siler: hesap yalnız A'da üye kalır (platform yöneticisi koruması gerçekten sınanır).</summary>
    public Task<int> DetachPlatformAdminFromOtherOrganizationsAsync() =>
        Factory.SqlAsync("DELETE FROM identity.memberships WHERE user_id = @u AND tenant_id <> @a", ("u", PlatformAdminUserId), ("a", A.TenantId));

    // ---- Sorgular ----------------------------------------------------------------------------------------------------

    /// <summary>Kiracının tablo başına satır sayıları (ham SQL; yumuşak silinenler dahil). <paramref name="ignoreErasedEvent"/>: <c>TenantErased</c> outbox satırı (kanıt olarak kalır) sayılmaz.</summary>
    public async Task<Dictionary<string, long>> CountsAsync(Guid tenantId, bool ignoreErasedEvent = false)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in Tables)
        {
            var filter = ignoreErasedEvent && table.IsOutbox ? " AND type NOT LIKE '%TenantErased'" : string.Empty;
            counts[table.Name] = await Factory.ScalarAsync<long>(
                $"SELECT count(*) FROM \"{table.Schema}\".\"{table.Table}\" WHERE \"{table.TenantColumn}\" = @t{filter}", ("t", tenantId));
        }

        return counts;
    }

    public Task<string> AccountStatusAsync(Guid tenantId) =>
        Factory.ScalarAsync<string>("SELECT status FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId));

    public Task<string> RequestStatusAsync() =>
        Factory.ScalarAsync<string>("SELECT status FROM platform.deletion_requests WHERE id = @r", ("r", Request.RequestId));

    /// <summary>Talep <c>completed</c> olmalı; değilse iletiye son hata ve tamamlanan adımlar eklenir (tanı için).</summary>
    public async Task AssertCompletedAsync()
    {
        var status = await RequestStatusAsync();
        var error = await Factory.ScalarAsync<string>("SELECT last_error FROM platform.deletion_requests WHERE id = @r", ("r", Request.RequestId));
        status.ShouldBe("completed", $"last_error: {error}; steps: {string.Join(',', await ErasedStepsAsync())}");
    }

    public Task<int> RequestAttemptsAsync() =>
        Factory.ScalarAsync<int>("SELECT attempts FROM platform.deletion_requests WHERE id = @r", ("r", Request.RequestId));

    public async Task<string[]> ErasedStepsAsync() =>
        await Factory.ScalarAsync<string[]>("SELECT erased_steps FROM platform.deletion_requests WHERE id = @r", ("r", Request.RequestId)) ?? [];

    public async Task<JsonElement> ReportAsync()
    {
        var text = await Factory.ScalarAsync<string>("SELECT report::text FROM platform.deletion_requests WHERE id = @r", ("r", Request.RequestId));
        text.ShouldNotBeNullOrWhiteSpace();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>İmha raporundaki tablo başına toplam (tüm adımlar üzerinden).</summary>
    public async Task<Dictionary<string, long>> ReportTotalsAsync()
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var step in (await ReportAsync()).EnumerateObject())
        {
            foreach (var table in step.Value.EnumerateObject())
            {
                totals[table.Name] = totals.GetValueOrDefault(table.Name) + table.Value.GetInt64();
            }
        }

        return totals;
    }

    public Task<long> CountAsync(string sql, params (string Name, object? Value)[] parameters) => Factory.ScalarAsync<long>(sql, parameters);

    /// <summary>Kiracının Conductor motor kimlikleri (DB'deki yürütmelerden).</summary>
    public async Task<List<string>> EngineIdsAsync(Guid tenantId) =>
        (await Factory.ErQueryAsync("SELECT engine_workflow_id FROM workflows.workflow_executions WHERE tenant_id = @t AND engine_workflow_id IS NOT NULL", ("t", tenantId))).Select(i => i!).ToList();

    /// <summary>Bir satırın JSON gösterimi: "dokunulmadı" karşılaştırması için.</summary>
    public Task<string> RowJsonAsync(string table, string keyColumn, object key) =>
        Factory.ScalarAsync<string>($"SELECT row_to_json(t)::text FROM {table} t WHERE t.{keyColumn} = @k", ("k", key));

    /// <summary>Yeni bir organizasyon açar ve platform hesabını (outbox olayıyla) oluşturur.</summary>
    public async Task<TestOrg> NewOrgAsync(string name)
    {
        var org = await Host.NewOrgAsync(name);
        await SettleAsync();
        return org;
    }
}
