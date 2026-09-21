using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Sales.Domain.Accounts;
using Sense.Crm.Modules.Sales.Domain.Contacts;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Shared.Web.ErrorHandling;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Sense.Crm.Spikes.M9h.Perf;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Spikes.M9h.Tests;

[CollectionDefinition(Name)]
public sealed class HostCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "h0-api";
}

/// <summary>
/// Q6 (+Q3 HTTP tarafı) — GERÇEK API host'u (CrmApiFactory: gerçek Program.cs, gerçek Sales uçları/handler'ları/depoları, gerçek GlobalExceptionHandler) üzerinde,
/// ürün kodu DEĞİŞTİRİLMEDEN: (a) <c>ConfigureDbContext&lt;SalesDbContext&gt;</c> + <c>ReplaceService&lt;IModelCustomizer&gt;</c> ile RecordScope filtresi, (b) <c>IStartupFilter</c> ile
/// <c>RecordScopeMiddleware</c> eşdeğeri (JWT 'sub' → anlık görüntü). Ölçülen: mevcut uçlar filtreyle kendiliğinden 404'e döner mi, gövde "yok" ile bayt bayt aynı mı, 403 eşlemesi için ne gerekir.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class Q6_IdorHostTests(CrmApiFactory factory) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Spike "çözümleyici": kullanıcı → anlık görüntü (gerçekte Identity çözümleyicisi).</summary>
    internal static readonly ConcurrentDictionary<Guid, RecordScopeSnapshot> Scopes = new();

    private sealed record Tenant(HttpClient Admin, Guid AdminId, HttpClient Member, Guid MemberId, HttpClient Member2, Guid Member2Id);

    private Tenant _t = null!;
    private WebApplicationFactoryHandle _host = null!;

    private sealed record WebApplicationFactoryHandle(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Plain, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithHandler);

    public async ValueTask InitializeAsync()
    {
        static void Common(IServiceCollection s)
        {
            s.ConfigureDbContext<SalesDbContext>(o => o.ReplaceService<IModelCustomizer, ScopeModelCustomizer>().AddInterceptors(new SpikeWriteInterceptor()));
            s.AddSingleton<IStartupFilter, ScopeStartupFilter>();
        }

        // Tek host (JWT imza anahtarı host başına üretilir → istemciler host'lar arası taşınamaz). Eşleme, statik bayrakla açılıp kapatılır.
        var plain = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            Common(s);
            s.RemoveAll<IExceptionHandler>();
            s.AddExceptionHandler<RecordReadOnlyExceptionHandler>(); // GlobalExceptionHandler'dan ÖNCE
            s.AddExceptionHandler<GlobalExceptionHandler>();
        }));
        _host = new WebApplicationFactoryHandle(plain, plain);

        // Kiracı: yönetici + iki üye (özel rol: crm.* okuma/yazma)
        var admin = plain.CreateClient();
        var auth = await admin.SignUpAsync("H0 Org " + Guid.NewGuid().ToString("N")[..6], UniqueEmail("admin"));
        admin.WithToken(auth.AccessToken);
        var me = await GetAsync(admin, $"{Base}/me");
        var adminId = me.GetProperty("user").GetProperty("id").GetGuid();

        var roleResponse = await admin.PostAsJsonAsync($"{Base}/organization/roles", new
        {
            name = "H0 Sales " + Guid.NewGuid().ToString("N")[..6],
            permissions = new[] { "crm.accounts.read", "crm.accounts.write", "crm.contacts.read", "crm.contacts.write", "crm.leads.read", "crm.leads.write", "crm.deals.read", "crm.deals.write" },
        }, Ct);
        var roleBody = await roleResponse.Content.ReadAsStringAsync(Ct);
        roleResponse.StatusCode.ShouldBe(HttpStatusCode.Created, roleBody);
        var roleId = JsonDocument.Parse(roleBody).RootElement.GetProperty("id").GetGuid();

        var m1 = await AddMemberVia(plain, admin, "Member One", roleId);
        var m2 = await AddMemberVia(plain, admin, "Member Two", roleId);
        _t = new Tenant(admin, adminId, m1.Client, m1.UserId, m2.Client, m2.UserId);

        Scopes[adminId] = RecordScopeSnapshot.System;
        Scopes[m1.UserId] = World.Own(m1.UserId);
        // Member2: "herkes okur, ben yazarım" (organization/own)
        Scopes[m2.UserId] = new RecordScopeSnapshot(m2.UserId, false, new Dictionary<string, ResourceScope>
        {
            ["deal"] = new(true, [], false, [m2.UserId], false),
            ["account"] = new(true, [], false, [m2.UserId], false),
            ["contact"] = new(true, [], false, [m2.UserId], false),
            ["lead"] = new(true, [], false, [m2.UserId], false),
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<NewMember> AddMemberVia(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, HttpClient admin, string name, Guid roleId)
    {
        var email = UniqueEmail("member");
        var added = await admin.PostAsJsonAsync($"{Base}/organization/members", new { email, displayName = name, roleId }, Ct);
        var body = await added.Content.ReadAsStringAsync(Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        var json = JsonDocument.Parse(body).RootElement;
        var userId = json.GetProperty("userId").GetGuid();
        var client = host.CreateClient();
        var first = await client.LoginAsync(email, json.GetProperty("temporaryPassword").GetString()!);
        client.WithToken(first.AccessToken);
        var changed = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = json.GetProperty("temporaryPassword").GetString(), newPassword = DefaultPassword }, Ct);
        changed.EnsureSuccessStatusCode();
        var auth = (await changed.Content.ReadFromJsonAsync<AuthResponse>(Ct))!;
        client.WithToken(auth.AccessToken);
        return new NewMember(userId, email, client, auth);
    }

    private static async Task<JsonElement> GetAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url, Ct);
        var b = await r.Content.ReadAsStringAsync(Ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, b);
        return JsonDocument.Parse(b).RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await c.PostAsJsonAsync(url, body, Ct);
        var b = await r.Content.ReadAsStringAsync(Ct);
        r.StatusCode.ShouldBe(expected, b);
        return JsonDocument.Parse(b).RootElement.Clone();
    }

    /// <summary>Kimlik + traceId normalize edilmiş 404 gövdesi/başlıkları (ayırt edilemezlik karşılaştırması için).</summary>
    private static async Task<(int Status, string Body, string Headers)> ProbeAsync(HttpClient c, HttpMethod method, string path, Guid id, object? body = null)
    {
        var req = new HttpRequestMessage(method, path.Replace("{id}", id.ToString(), StringComparison.Ordinal));
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        var resp = await c.SendAsync(req, Ct);
        var text = await resp.Content.ReadAsStringAsync(Ct);
        text = Regex.Replace(text, id.ToString(), "{id}", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"#\"");
        var headers = string.Join(";", resp.Headers.Concat(resp.Content.Headers)
            .Where(h => h.Key is not ("Date" or "Server" or "X-Correlation-Id" or "traceparent" or "Request-Id" or "X-Request-ID" or "X-Trace-Id"))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        headers = Regex.Replace(headers, id.ToString(), "{id}", RegexOptions.IgnoreCase);
        headers = Regex.Replace(headers, @"X-Correlation-Id=[^;]*;?", string.Empty);
        return ((int)resp.StatusCode, text, headers);
    }

    private async Task<(Guid AdminAccount, Guid AdminDeal, Guid MemberDeal)> SeedAsync()
    {
        var account = await PostAsync(_t.Admin, $"{Base}/accounts", new { name = "Admin Account" });
        var adminDeal = await PostAsync(_t.Admin, $"{Base}/deals", new { name = "Admin Deal", accountId = account.GetProperty("id").GetGuid() });
        var memberAccount = await PostAsync(_t.Member, $"{Base}/accounts", new { name = "Member Account" });
        var memberDeal = await PostAsync(_t.Member, $"{Base}/deals", new { name = "Member Deal", accountId = memberAccount.GetProperty("id").GetGuid() });
        return (account.GetProperty("id").GetGuid(), adminDeal.GetProperty("id").GetGuid(), memberDeal.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ExistingEndpoints_ReturnScopedLists_AndHiddenRecordIs404_WithBodyIdenticalToMissing()
    {
        var (adminAccount, adminDeal, memberDeal) = await SeedAsync();

        // Liste + sayaç: üye yalnız kendi fırsatını görür; sahip süzgeci ile görünmeyeni istemek boş döner.
        var memberList = await GetAsync(_t.Member, $"{Base}/deals");
        memberList.GetProperty("totalCount").GetInt32().ShouldBe(1);
        memberList.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(memberDeal);
        (await GetAsync(_t.Member, $"{Base}/deals?ownerUserId={_t.AdminId}")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await GetAsync(_t.Admin, $"{Base}/deals")).GetProperty("totalCount").GetInt32().ShouldBe(2);
        (await GetAsync(_t.Member, $"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(1);

        // Kanban panosu ve ilişkili liste de kendiliğinden kapsamlı
        var board = await GetAsync(_t.Member, $"{Base}/deals/board");
        board.GetRawText().ShouldContain(memberDeal.ToString(), Case.Insensitive);
        board.GetRawText().ShouldNotContain(adminDeal.ToString(), Case.Insensitive);

        // IDOR: görünmeyen kimlik ile GET/PUT/DELETE == hiç var olmayan kimlik (gövde+başlık bayt bayt; yalnız kimlik/traceId normalize)
        var missing = Guid.NewGuid();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"{Base}/deals/{{id}}", null),
                     (HttpMethod.Put, $"{Base}/deals/{{id}}", new { name = "hijack", accountId = adminAccount }),
                     (HttpMethod.Delete, $"{Base}/deals/{{id}}", null),
                     (HttpMethod.Get, $"{Base}/accounts/{{id}}", null),
                     (HttpMethod.Delete, $"{Base}/accounts/{{id}}", null),
                 })
        {
            var hiddenId = path.Contains("/accounts/", StringComparison.Ordinal) ? adminAccount : adminDeal;
            var hidden = await ProbeAsync(_t.Member, method, path, hiddenId, body);
            var absent = await ProbeAsync(_t.Member, method, path, missing, body);
            Evidence.Log($"[Q6 IDOR {method} {path}] status={hidden.Status}/{absent.Status}\nbody(hidden)={hidden.Body}\nheaders(hidden)={hidden.Headers}", "q6.txt");
            hidden.Status.ShouldBe(404, $"{method} {path}");
            hidden.Body.ShouldBe(absent.Body, $"{method} {path}: gövde ayırt edilemez olmalı");
            hidden.Headers.ShouldBe(absent.Headers, $"{method} {path}: başlıklar ayırt edilemez olmalı");
        }

        // Veritabanı değişmedi: yönetici kayıtları hâlâ sağlam
        (await GetAsync(_t.Admin, $"{Base}/deals/{adminDeal}")).GetProperty("name").GetString().ShouldBe("Admin Deal");
        (await GetAsync(_t.Admin, $"{Base}/accounts/{adminAccount}")).GetProperty("name").GetString().ShouldBe("Admin Account");
    }

    [Fact]
    public async Task HiddenRelatedRecords_CreateOnHiddenAccountIsNotFound_AndVisibleDealWithHiddenAccountDoesNotCrash()
    {
        var (adminAccount, _, _) = await SeedAsync();

        // Üye görünmeyen firmaya fırsat açamaz (ilişkili kayıt yok gibi davranır)
        var r = await _t.Member.PostAsJsonAsync($"{Base}/deals", new { name = "sneaky", accountId = adminAccount }, Ct);
        var body = await r.Content.ReadAsStringAsync(Ct);
        Evidence.Log($"[Q6 create-on-hidden-account] {(int)r.StatusCode} {body}", "q6.txt");
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);

        // Yönetici, üyeye fırsat atar ama firma yöneticinin (üyeye görünmez): liste 500 olmamalı; firma adı ne oluyor — ölçülür.
        var assigned = await PostAsync(_t.Admin, $"{Base}/deals", new { name = "Assigned to member", accountId = adminAccount, ownerUserId = _t.MemberId });
        var list = await _t.Member.GetAsync($"{Base}/deals?pageSize=50", Ct);
        var listBody = await list.Content.ReadAsStringAsync(Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, listBody);
        var row = JsonDocument.Parse(listBody).RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == assigned.GetProperty("id").GetGuid());
        Evidence.Log($"[Q6 visible deal / hidden account] accountName present={row.TryGetProperty("accountName", out var an)} value={(row.TryGetProperty("accountName", out _) ? an.GetRawText() : "<absent>")}", "q6.txt");
    }

    [Fact]
    public async Task UnresolvedUser_IsDenied_And_ResolverFailure_Is503_NeverUnrestricted()
    {
        await SeedAsync();
        var stranger = _host.Plain.CreateClient();
        var auth = await stranger.SignUpAsync("Stranger " + Guid.NewGuid().ToString("N")[..6], UniqueEmail("stranger"));
        stranger.WithToken(auth.AccessToken);
        // Bu kullanıcı için çözümleyici kaydı yok → Deny anlık görüntüsü (kimliksiz/çözümsüz = hiçbir şey)
        (await GetAsync(stranger, $"{Base}/deals")).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // Çözümleyici arızası: fail-closed 503 (asla "sınırsız")
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/deals");
        req.Headers.Add("X-Spike-Resolver-Fail", "1");
        req.Headers.Authorization = _t.Member.DefaultRequestHeaders.Authorization;
        var resp = await _t.Member.SendAsync(req, Ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task WriteOutsideWriteScope_WithoutExceptionMapping_SurfacesAs500_ProvingAHandlerMappingIsRequired()
    {
        var (_, adminDeal, _) = await SeedAsync();
        RecordReadOnlyExceptionHandler.Enabled = false;
        var m2 = _t.Member2;

        // Üye2 okuyabilir (organization) ama yazamaz: PUT görünür kaydı değiştirmeye çalışır → interceptor RecordReadOnlyException fırlatır.
        var adminAccountId = (await GetAsync(_t.Admin, $"{Base}/deals/{adminDeal}")).GetProperty("accountId").GetGuid();
        (await GetAsync(m2, $"{Base}/deals/{adminDeal}")).GetProperty("name").GetString().ShouldBe("Admin Deal");
        var resp = await m2.PutAsJsonAsync($"{Base}/deals/{adminDeal}", new { name = "changed by read-only user", accountId = adminAccountId }, Ct);
        var body = await resp.Content.ReadAsStringAsync(Ct);
        Evidence.Log($"[Q6 read-only write, NO mapping] {(int)resp.StatusCode} {body}", "q6.txt");

        // MEVCUT GlobalExceptionHandler bilinmeyen istisnayı 500 'internal_error' yapar → 403 record.read_only için Map'e bir kol eklenmeli.
        resp.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await GetAsync(_t.Admin, $"{Base}/deals/{adminDeal}")).GetProperty("name").GetString().ShouldBe("Admin Deal", "yazma engellendi, kayıt değişmedi");
    }

    [Fact]
    public async Task WriteOutsideWriteScope_WithOneLineHandlerMapping_Is403_RecordReadOnly_AndRowUnchanged()
    {
        var (_, adminDeal, _) = await SeedAsync();
        RecordReadOnlyExceptionHandler.Enabled = true;
        var m2 = _t.Member2;
        var adminAccountId = (await GetAsync(_t.Admin, $"{Base}/deals/{adminDeal}")).GetProperty("accountId").GetGuid();

        var resp = await m2.PutAsJsonAsync($"{Base}/deals/{adminDeal}", new { name = "changed by read-only user", accountId = adminAccountId }, Ct);
        var body = await resp.Content.ReadAsStringAsync(Ct);
        Evidence.Log($"[Q6 read-only write, WITH mapping] {(int)resp.StatusCode} {body}", "q6.txt");
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
        resp.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("record.read_only");

        (await GetAsync(_t.Admin, $"{Base}/deals/{adminDeal}")).GetProperty("name").GetString().ShouldBe("Admin Deal");

        // Kendi kaydını yazabilir (yazma kümesinde)
        var own = await PostAsync(_t.Member2, $"{Base}/accounts", new { name = "M2 account" });
        (await m2.PutAsJsonAsync($"{Base}/accounts/{own.GetProperty("id").GetGuid()}", new { name = "M2 account (edited)" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}

/// <summary>Spike <c>RecordScopeMiddleware</c>: kimlik JWT 'sub'ından (doğrulama sonraki auth ara katmanındadır), kapsam Scopes sözlüğünden; bilinmeyen = Deny; başlık ile çözümleyici arızası = 503.</summary>
internal sealed class ScopeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nxt) =>
        {
            if (ctx.Request.Headers.ContainsKey("X-Spike-Resolver-Fail"))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 503, Title = "access.scope_unavailable" }, (JsonSerializerOptions?)null, "application/problem+json", ctx.RequestAborted);
                return;
            }

            var snapshot = SubjectOf(ctx) is { } sub && Q6_IdorHostTests.Scopes.TryGetValue(sub, out var s) ? s : RecordScopeSnapshot.Deny;
            using (RecordScopeContext.Use(snapshot))
            {
                await nxt(ctx);
            }
        });
        next(app);
    };

    private static Guid? SubjectOf(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = header["Bearer ".Length..].Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("sub", out var sub) && Guid.TryParse(sub.GetString(), out var g) ? g : null;
    }
}

/// <summary>Spike <c>RecordScopeWriteInterceptor</c> (plan D9): değişen/silinen kapsamlı kaydın ÖZGÜN sahibi yazma kümesinde değilse fırlatır.</summary>
internal sealed class SpikeWriteInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    private static void Check(DbContext? context)
    {
        if (context is null || !RecordScopeContext.IsSet)
        {
            return;
        }

        var snapshot = RecordScopeContext.Snapshot;
        foreach (EntityEntry entry in context.ChangeTracker.Entries().Where(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            var resource = entry.Entity switch
            {
                Deal => "deal",
                Account => "account",
                Contact => "contact",
                Lead => "lead",
                _ => null,
            };
            if (resource is null)
            {
                continue;
            }

            var owner = (Guid)entry.OriginalValues["OwnerUserId"]!;
            var scope = snapshot.Get(resource);
            if (!(scope.WriteAll || scope.WriteOwners.Contains(owner)))
            {
                throw new RecordReadOnlyException(resource);
            }
        }
    }
}

/// <summary>GlobalExceptionHandler.Map'e eklenecek tek kolun eşdeğeri (gerçek kartta: <c>RecordReadOnlyException =&gt; (Forbidden, "record.read_only", …, 403)</c>).</summary>
internal sealed class RecordReadOnlyExceptionHandler : IExceptionHandler
{
    /// <summary>Testlerin "eşleme yok" (mevcut durum) ve "eşleme var" karşılaştırmasını aynı host'ta yapması için.</summary>
    public static volatile bool Enabled;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!Enabled || exception is not RecordReadOnlyException ex)
        {
            return false;
        }

        var problem = new ProblemDetails { Status = 403, Title = "Forbidden", Type = "record.read_only", Instance = httpContext.Request.Path };
        problem.Extensions["code"] = "record.read_only";
        problem.Extensions["args"] = new Dictionary<string, string> { ["resource"] = ex.Resource };
        httpContext.Response.StatusCode = 403;
        await httpContext.Response.WriteAsJsonAsync(problem, (JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        return true;
    }
}
