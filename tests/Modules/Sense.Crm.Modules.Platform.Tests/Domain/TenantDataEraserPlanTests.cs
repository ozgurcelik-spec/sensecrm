using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;
using Sense.Crm.Shared.Infrastructure.Persistence.Retention;
using Sense.Crm.Shared.Kernel.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

/// <summary>
/// KVKK imha planı (<c>TenantDataEraser&lt;TContext&gt;.BuildPlan</c>), veritabanısız: her modül DbContext'inin çevrimdışı EF modelinden plan üretilir.
/// Modelin her <see cref="ITenantEntity"/> tablosu plandadır (yeni tablo/modül unutulamaz), <c>audit_log_entries</c> plan dışıdır (ayrı adım),
/// modül outbox'ı plandadır ve çocuk tablolar FK'sıyla başvurdukları ana tablolardan <b>önce</b> silinir.
/// </summary>
public sealed class TenantDataEraserPlanTests
{
    private static readonly string[] ExpectedContexts =
    [
        "IdentityDbContext", "SalesDbContext", "ActivitiesDbContext", "WorkflowsDbContext", "MarketingDbContext", "CommerceDbContext", "ServiceDbContext", "PlatformDbContext",
    ];

    public static IEnumerable<object[]> ModuleContexts() => ContextTypes().Select(t => new object[] { t.FullName! });

    [Fact]
    public void EveryModuleContext_IsDiscovered()
    {
        var names = ContextTypes().Select(t => t.Name).ToList();

        foreach (var expected in ExpectedContexts)
        {
            names.ShouldContain(expected);
        }
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void Plan_ContainsEveryTenantTableOfTheModel_AndTheOutbox_ButNotTheAuditTable(string contextTypeName)
    {
        using var context = CreateContext(contextTypeName);
        var plan = BuildPlan(context);

        var expected = ModelTenantTables(context);
        var planned = plan.Select(t => (t.Schema, t.Table)).ToList();

        planned.Distinct().Count().ShouldBe(planned.Count, "plan aynı tabloyu iki kez içeriyor");
        foreach (var table in expected)
        {
            planned.ShouldContain(table, $"{table.Schema}.{table.Table} plan dışı: bu tablo imha edilmez");
        }

        // Plan = modelin kiracı tabloları + yalnız modülün outbox'ı (audit ve inbox yok).
        planned.Except(expected).ShouldBe([(context.Schema, "outbox_messages")], "plan beklenmeyen tablo içeriyor");
        plan.Any(t => t.Table == AuditLogTables.TableName || t.Schema == AuditLogTables.Schema).ShouldBeFalse("audit_log_entries AuditTenantDataEraser'ındır, genel adımın değil");
        plan.Any(t => t.Table == "inbox_messages").ShouldBeFalse("inbox_messages kiracı anahtarı taşımaz");

        var outbox = plan.Single(t => t.Table == "outbox_messages");
        outbox.Schema.ShouldBe(context.Schema);
        outbox.TenantColumn.ShouldBe("tenant_id");
        plan[^1].ShouldBe(outbox, "outbox iş tablolarından sonra silinir");
        plan.ShouldAllBe(t => t.TenantColumn == "tenant_id");
    }

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void Plan_DeletesChildTablesBeforeTheirForeignKeyPrincipals(string contextTypeName)
    {
        using var context = CreateContext(contextTypeName);
        var plan = BuildPlan(context);
        var position = plan.Select((t, index) => (Key: (t.Schema, t.Table), Index: index)).ToDictionary(x => x.Key, x => x.Index);

        foreach (var dependent in context.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.GetTableName() is not null))
        {
            var dependentKey = KeyOf(context, dependent);
            foreach (var foreignKey in dependent.GetForeignKeys())
            {
                var principalKey = KeyOf(context, foreignKey.PrincipalEntityType);
                if (dependentKey == principalKey || !position.TryGetValue(dependentKey, out var child) || !position.TryGetValue(principalKey, out var parent))
                {
                    continue;
                }

                child.ShouldBeLessThan(parent, $"{dependentKey.Table}, ona başvurulan {principalKey.Table} tablosundan önce silinmeli");
            }
        }
    }

    [Fact]
    public void Commerce_LinesComeBeforeTheirDocuments()
    {
        var plan = PlanTables("CommerceDbContext");

        plan.IndexOf("quote_lines").ShouldBeGreaterThanOrEqualTo(0);
        plan.IndexOf("quote_lines").ShouldBeLessThan(plan.IndexOf("quotes"));
        plan.IndexOf("sales_order_lines").ShouldBeGreaterThanOrEqualTo(0);
        plan.IndexOf("sales_order_lines").ShouldBeLessThan(plan.IndexOf("sales_orders"));
    }

    [Fact]
    public void Identity_MembershipsComeBeforeRoles_AndTheGlobalAccountTablesAreNotPlanned()
    {
        var plan = PlanTables("IdentityDbContext");

        plan.IndexOf("memberships").ShouldBeGreaterThanOrEqualTo(0);
        plan.IndexOf("roles").ShouldBeGreaterThanOrEqualTo(0);
        plan.IndexOf("memberships").ShouldBeLessThan(plan.IndexOf("roles"));

        // Küresel hesap tabloları genel adımda değil, IdentityAccountEraser/IdentityTenantEraser'da işlenir (başka kiracıda üyeliği olan hesap silinmemeli).
        plan.ShouldNotContain("users");
        plan.ShouldNotContain("tenants");
        plan.ShouldNotContain("refresh_tokens");
    }

    [Fact]
    public void Platform_PlansOnlyItsOutbox_BecauseItsTablesAreGlobal()
    {
        PlanTables("PlatformDbContext").ShouldBe(["outbox_messages"]);
    }

    // ---- Yardımcılar -------------------------------------------------------------------------------------------------

    private static List<string> PlanTables(string contextName)
    {
        using var context = CreateContext(ContextTypes().Single(t => t.Name == contextName).FullName!);
        return BuildPlan(context).Select(t => t.Table).ToList();
    }

    private static List<EraseTarget> BuildPlan(ModuleDbContext context)
    {
        var eraser = typeof(TenantDataEraser<>).MakeGenericType(context.GetType());
        var method = eraser.GetMethod(nameof(TenantDataEraser<ModuleDbContext>.BuildPlan), BindingFlags.Public | BindingFlags.Static)!;
        return ((IReadOnlyList<EraseTarget>)method.Invoke(null, [context])!).ToList();
    }

    private static List<(string Schema, string Table)> ModelTenantTables(ModuleDbContext context) =>
        context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.ClrType != typeof(AuditLogEntry) && typeof(ITenantEntity).IsAssignableFrom(e.ClrType) && e.GetTableName() is not null)
            .Select(e => KeyOf(context, e))
            .Distinct()
            .ToList();

    private static (string Schema, string Table) KeyOf(DbContext context, IEntityType entity) =>
        (entity.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public", entity.GetTableName() ?? string.Empty);

    private static List<Type> ContextTypes() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Sense.Crm.Modules.*.Infrastructure.dll")
            .Select(Assembly.LoadFrom)
            .SelectMany(LoadableTypes)
            .Where(t => !t.IsAbstract && typeof(ModuleDbContext).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static ModuleDbContext CreateContext(string contextTypeName)
    {
        var type = ContextTypes().Single(t => t.FullName == contextTypeName);
        var method = typeof(TenantDataEraserPlanTests).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
        return (ModuleDbContext)method.Invoke(null, null)!;
    }

    private static TContext Create<TContext>()
        where TContext : ModuleDbContext
    {
        // Model üretimi bağlantı açmaz; bağlantı dizesi yalnız sağlayıcıyı seçmek içindir (TenantQueryFilterConventionTests ile aynı desen).
        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql("Host=localhost").UseSnakeCaseNamingConvention().Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options, (ITenantContext)new TenantContext())!;
    }
}
