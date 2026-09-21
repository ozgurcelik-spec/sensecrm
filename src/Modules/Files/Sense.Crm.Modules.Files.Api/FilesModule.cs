using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Contracts;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Infrastructure;
using Sense.Crm.Modules.Files.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Modules.Files.Api;

/// <summary>
/// Files modülü kompozisyon kökü (M8C): kayıt ekleri (nesne depolama), kota, yaşam döngüsü işleri için kayıtlar. <b>Yeni izin yoktur</b> (D3): erişim kararı kaydın kendi
/// okuma/yazma iznine göre çalışma anında verilir. Hiçbir iş modülü Files'a bağlanmaz, Files hiçbir iş modülüne bağlanmaz (yalnız <c>Shared.Contracts.Files.IAttachmentTarget</c>).
/// </summary>
public sealed class FilesModule : IModule
{
    public string Name => FilesDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(FilesModule).Assembly,
        typeof(AttachmentAccess).Assembly,
        typeof(FileAttachment).Assembly,
        typeof(FileAttached).Assembly,
        typeof(FilesDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => [];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<FilesDbContext>(configuration, FilesDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox) ve UnitOfWork çözümlemesi için.
        services.AddModuleHandlers(
            FilesDbContext.SchemaName,
            typeof(AttachmentAccess).Assembly,
            typeof(FilesDbContext).Assembly,
            typeof(FileAttachment).Assembly,
            typeof(FileAttached).Assembly);

        services.AddFilesContractServices(configuration);

        // Erişim kararı yalnız API isteklerinde verilir (IPermissionService/ICurrentUser istek bağlamı ister); Worker ve Migrator bu koruma sınıfını kullanmaz.
        services.AddScoped<IAttachmentAccess, AttachmentAccess>();

        // /health tam raporunda "storage" denetimi; ready etiketi YOK (depo arızası CRM'nin geri kalanını hazır-değil yapmaz).
        services.AddHealthChecks().AddCheck<FilesStorageHealthCheck>("storage", failureStatus: HealthStatus.Degraded, tags: ["storage"]);
    }
}
