using System.Reflection;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Persistence;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.Entitlements;
using Sense.Crm.Shared.Infrastructure.Events;
using Sense.Crm.Shared.Infrastructure.Messaging;
using Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;
using Sense.Crm.Shared.Infrastructure.Observability;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Shared.Infrastructure.Persistence.Retention;

namespace Sense.Crm.Shared.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";
    private const int DbRetryCount = 3;
    private const string DatabaseConnectionMissing = "ConnectionStrings:Database is not configured.";

    /// <summary>Tüm host'ların (Api, Worker, Migrator) ortak altyapısı: ayarlar, bağlam, dispatcher, pipeline, cache, event bus.</summary>
    public static IServiceCollection AddCrmCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PagingOptions>().Bind(configuration.GetSection(ConfigurationSections.Paging)).ValidateOnStart();
        services.AddOptions<LocalizationOptions>().Bind(configuration.GetSection(ConfigurationSections.Localization)).ValidateOnStart();
        services.AddOptions<CachingOptions>().Bind(configuration.GetSection(ConfigurationSections.Caching)).ValidateOnStart();
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(ConfigurationSections.Outbox)).ValidateOnStart();
        services.AddOptions<DiagnosticsOptions>().Bind(configuration.GetSection(ConfigurationSections.Diagnostics)).ValidateOnStart();
        services.AddOptions<ProblemMappingOptions>().Bind(configuration.GetSection(ConfigurationSections.ProblemDetails)).ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.TryAddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());

        services.TryAddScoped<CurrentUserAccessor>();
        services.TryAddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserAccessor>());

        services.TryAddScoped<CorrelationIdContext>();
        services.TryAddScoped<ICorrelationIdContext>(sp => sp.GetRequiredService<CorrelationIdContext>());
        services.TryAddScoped<ICorrelationIdContextSetter>(sp => sp.GetRequiredService<CorrelationIdContext>());

        // Tüm IHttpClientFactory istemcileri correlation id'yi aşağı akış servislerine taşır.
        services.TryAddTransient<CorrelationIdPropagationHandler>();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdPropagationHandler>());

        services.TryAddScoped<IDispatcher, Dispatcher>();
        services.TryAddScoped<IModuleUnitOfWorkResolver, ModuleUnitOfWorkResolver>();

        // Pipeline sırası = kayıt sırası (dıştan içe).
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehaviour<,>), typeof(LoggingBehaviour<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehaviour<,>), typeof(ValidationBehaviour<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehaviour<,>), typeof(AuthorizationBehaviour<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehaviour<,>), typeof(UnitOfWorkBehaviour<,>)));
        // M7: plan/askı/limit zorlaması tek noktada; yetki ve doğrulamadan SONRA, UnitOfWork'ten SONRA (sert limit açık transaction içinde kilit alır).
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehaviour<,>), typeof(EntitlementBehaviour<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehaviour<,>), typeof(CachingBehaviour<,>)));

        // Platform modülü yüklü olmayan host/testlerde davranış değişmez: her şey açık, tam erişim, limitsiz (Platform Replace eder).
        services.TryAddScoped<ITenantEntitlements, UnlimitedEntitlements>();
        services.TryAddScoped<ILimitGuard, AllowAllLimitGuard>();
        services.TryAddScoped<IPlanCatalog, AllowAllPlanCatalog>();
        services.TryAddScoped<IPlatformAdminVerifier, DenyPlatformAdminVerifier>();
        services.TryAddScoped<IPlatformAuditSink, NoOpPlatformAuditSink>();

        services.TryAddSingleton<IEventBus, InProcessEventBus>();
        services.TryAddScoped<IIntegrationEventOutbox, IntegrationEventOutbox>();
        services.TryAddScoped<AuditTenantInterceptor>();
        services.TryAddScoped<AuditLogInterceptor>();

        // Redis isteğe bağlı: bağlantı dizesi verilirse HybridCache'in dağıtık (L2) katmanı olur, yoksa yalnız bellek içi.
        var redis = configuration.GetConnectionString(ConnectionStringNames.Redis);
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddStackExchangeRedisCache(o => o.Configuration = redis);
        }
        else if (!configuration.GetSection($"{ConfigurationSections.Caching}:{nameof(CachingOptions.PermissionExpirationMinutes)}").Exists())
        {
            // M9: paylaşımlı önbellek yok (tek örnek, yalnız bellek içi) → izin bayatlığı üst sınırı kısa (2 dk). Açık ayar bunu ezer.
            services.PostConfigure<CachingOptions>(o => o.PermissionExpirationMinutes = CachingDefaults.PermissionExpirationMinutesWithoutRedis);
        }

        services.AddHybridCache();
        services.AddOptions<Microsoft.Extensions.Caching.Hybrid.HybridCacheOptions>()
            .Configure<IOptions<CachingOptions>>((o, caching) =>
            {
                o.DefaultEntryOptions = new Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(caching.Value.DefaultExpirationMinutes),
                    LocalCacheExpiration = TimeSpan.FromSeconds(caching.Value.LocalExpirationSeconds),
                };
            });

        return services;
    }

    /// <summary>Bir modülün handler, validator, event handler ve UnitOfWork kayıtları (assembly taraması, Scrutor).</summary>
    public static IServiceCollection AddModuleHandlers(this IServiceCollection services, string moduleName, params Assembly[] assemblies)
    {
        services.Scan(scan => scan
            .FromAssemblies(assemblies)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IIntegrationEventHandler<>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IDomainEventHandler<>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());

        services.AddValidatorsFromAssemblies(assemblies, ServiceLifetime.Scoped, includeInternalTypes: true);

        foreach (var asm in assemblies)
        {
            ModuleUnitOfWorkResolver.Register(asm, moduleName);
        }

        EventTypeRegistry.RegisterAll(assemblies, typeof(Kernel.Domain.IDomainEvent));
        EventTypeRegistry.RegisterAll(assemblies, typeof(IIntegrationEvent));
        return services;
    }

    /// <summary>
    /// Modül DbContext'i: Npgsql + snake_case + kiracı/denetim interceptor'ları; şema başına migration geçmişi.
    /// Denetim kaydı (<see cref="AuditLogInterceptor"/>) her modülde varsayılan olarak açıktır ve aynı transaction'da
    /// ortak <c>audit</c> şemasına yazar (K14).
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, IConfiguration configuration, string schema)
        where TContext : ModuleDbContext
    {
        var connectionString = RequireConnectionString(configuration);

        services.AddDbContext<TContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema);
                npgsql.EnableRetryOnFailure(DbRetryCount);
            });
            options.UseSnakeCaseNamingConvention();

            // Sıra önemli: AuditTenantInterceptor (TenantId/soft-delete) önce, AuditLogInterceptor sonra çalışır.
            options.AddInterceptors(sp.GetRequiredService<AuditTenantInterceptor>(), sp.GetRequiredService<AuditLogInterceptor>());
        });

        services.AddScoped<IModuleUnitOfWork>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ModuleDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<OutboxProcessor<TContext>>();

        // KVKK imhası (M7): modülün tüm ITenantEntity tabloları için genel imha adımı; yeni modül bu çağrıyla otomatik kapsanır (unutulamaz).
        services.AddScoped<ITenantDataEraser, TenantDataEraser<TContext>>();
        return services;
    }

    /// <summary>
    /// Ortak denetim şemasının (<c>audit</c>) sahibi olan context: yalnız migration üretimi/uygulaması için kullanılır
    /// (Migrator ve testler). Modüller tabloya kendi context'leri üzerinden yazar/okur.
    /// </summary>
    public static IServiceCollection AddAuditStore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = RequireConnectionString(configuration);
        services.AddDbContext<AuditDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, AuditDbContext.SchemaName))
            .UseSnakeCaseNamingConvention());
        return services;
    }

    /// <summary>
    /// appsettings.json'daki boş şablon değeri ("") yanlışlıkla geçerli sayılmasın: Production'da bağlantı dizesi verilmediyse
    /// süreç açılışta durur (aksi hâlde ilk veritabanı erişiminde belirsiz bir hatayla çöker).
    /// </summary>
    private static string RequireConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Database);
        return string.IsNullOrWhiteSpace(connectionString) ? throw new InvalidOperationException(DatabaseConnectionMissing) : connectionString;
    }
}

/// <summary>İstek tipinin assembly'sinden modül adını, oradan da modülün UnitOfWork'ünü bulur.</summary>
public sealed class ModuleUnitOfWorkResolver(IServiceProvider provider) : IModuleUnitOfWorkResolver
{
    private static readonly Dictionary<Assembly, string> ModuleByAssembly = [];

    public static void Register(Assembly assembly, string moduleName) => ModuleByAssembly[assembly] = moduleName;

    private const string ModuleAssemblyPrefix = "Sense.Crm.Modules.";

    /// <summary>
    /// Assembly'nin ait olduğu modül adı: <c>AddModuleHandlers</c> ile kaydedilmişse o, değilse <c>Sense.Crm.Modules.{Ad}.{Katman}</c>
    /// adından türetilen küçük harfli ad (Worker Application assembly'lerini kaydetmez). Plan kapısı/limit modülü bundan türer.
    /// </summary>
    public static string? ModuleOf(Assembly assembly)
    {
        if (ModuleByAssembly.TryGetValue(assembly, out var module))
        {
            return module;
        }

        var name = assembly.GetName().Name;
        if (name is null || !name.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[ModuleAssemblyPrefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0 ? rest : rest[..dot]).ToLowerInvariant();
    }

    public IUnitOfWork? Resolve(Type requestType)
    {
        if (!ModuleByAssembly.TryGetValue(requestType.Assembly, out var module))
        {
            return null;
        }

        return provider.GetServices<IModuleUnitOfWork>()
            .FirstOrDefault(u => string.Equals(u.ModuleName, module, StringComparison.OrdinalIgnoreCase));
    }
}
