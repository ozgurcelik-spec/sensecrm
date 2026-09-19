using System.Reflection;
using Crm.Modules.Commerce.Application;
using Crm.Modules.Commerce.Application.Orders;
using Crm.Modules.Commerce.Application.Quotes;
using Crm.Modules.Commerce.Contracts;
using Crm.Modules.Commerce.Domain;
using Crm.Modules.Commerce.Infrastructure.Persistence;
using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Commerce.Api;

/// <summary>
/// Commerce modülü kompozisyon kökü (docs/plan/m6a-ticaret.md): ürün kataloğu, kalemli teklifler (durum makinesi, süre dolumu),
/// satış siparişleri (doğrudan veya tekliften tek transaction'da dönüşüm), kiracı+yıl bazında numara sayacı ve ticaret raporu.
/// Diğer modüllere yalnız <c>Sales.Contracts</c> (<c>IRecordLookup</c>, <c>IRecordRelationLookup</c>) ve <c>Identity.Contracts</c> ile konuşur.
/// <c>crm.products/quotes/orders.read|write</c> izinlerini kataloğa katar.
/// </summary>
public sealed class CommerceModule : IModule
{
    public string Name => CommerceDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(CommerceModule).Assembly,
        typeof(OwnerResolver).Assembly,
        typeof(CommercePermissions).Assembly,
        typeof(CommerceDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => CommercePermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CommerceDbContext>(configuration, CommerceDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox: QuoteAccepted, SalesOrderCreated) için.
        services.AddModuleHandlers(
            CommerceDbContext.SchemaName,
            typeof(OwnerResolver).Assembly,
            typeof(CommerceDbContext).Assembly,
            typeof(IProductRepository).Assembly,
            typeof(CommercePermissions).Assembly);

        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
        services.AddScoped<IProductReadStore, ProductReadStore>();
        services.AddScoped<IQuoteReadStore, QuoteReadStore>();
        services.AddScoped<IOrderReadStore, OrderReadStore>();
        services.AddScoped<ICommerceReportStore, CommerceReportStore>();
        services.AddScoped<IProductLookup, ProductLookup>();
        services.AddScoped<IDocumentNumberAllocator, DocumentNumberAllocator>();
        services.AddScoped<ICommerceTransaction, CommerceTransaction>();

        services.AddScoped<OwnerResolver>();
        services.AddScoped<RelatedRecordVerifier>();
        services.AddScoped<LineProductVerifier>();
        services.AddScoped<DocumentNumbers>();
        services.AddScoped<CommerceClock>();
        services.AddScoped<QuoteTransitions>();
        services.AddScoped<OrderTransitions>();
        services.AddSingleton<IAuditEntityPermissions, CommerceAuditEntityPermissions>();
    }
}

/// <summary>Commerce varlık türlerinin kayıt bazlı denetim okuma izinleri (<c>GET /audit?entityType&amp;entityId</c>).</summary>
public sealed class CommerceAuditEntityPermissions : IAuditEntityPermissions
{
    public IReadOnlyDictionary<string, string> ReadPermissionsByEntityType => CommerceAuditEntities.ReadPermissions;
}
