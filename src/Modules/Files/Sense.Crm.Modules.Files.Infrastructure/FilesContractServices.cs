using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Infrastructure.Jobs;
using Sense.Crm.Modules.Files.Infrastructure.Persistence;
using Sense.Crm.Modules.Files.Infrastructure.Storage;
using Sense.Crm.Modules.Files.Infrastructure.Upload;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Files.Infrastructure;

/// <summary>
/// Files'ın çalışma zamanı kayıtları (handler taraması hariç): seçenekler (<c>Files</c> bölümü + ortama göre varsayılanlar + <c>ValidateOnStart</c>), depolama adaptörü seçimi,
/// yükleme hazırlama, tarayıcı, hız sınırlayıcı, erişim koruması, depolar, <c>IUsageReporter</c>, KVKK nesne imhası ve Worker işleri. API (<c>FilesModule</c>), Worker ve Migrator
/// aynı kaydı kullanır. Diğer modüllerin <c>IAttachmentTarget</c>'ları kendi <c>Add&lt;Modül&gt;ContractServices()</c>'inde kayıtlıdır.
/// </summary>
public static class FilesContractServices
{
    public static IServiceCollection AddFilesContractServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FilesOptions>().Bind(configuration.GetSection(FilesOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<FilesOptions>, FilesOptionsDefaults>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FilesOptions>, FilesOptionsValidation>());

        // Depolama: durum tek örnek, kiracı korumalı yüz istek kapsamlı.
        services.TryAddSingleton<InMemoryObjectStore>();
        services.TryAddSingleton<S3StorageState>();
        services.TryAddScoped<IFileStorage>(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<FilesOptions>>().Value.Storage;
            var tenant = sp.GetRequiredService<Sense.Crm.Shared.Contracts.Context.ITenantContext>();
            var clock = sp.GetRequiredService<TimeProvider>();
            return storage.Provider switch
            {
                StorageProviders.S3 => new S3FileStorage(sp.GetRequiredService<S3StorageState>(), tenant, clock, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<S3FileStorage>>()),
                StorageProviders.FileSystem => new FileSystemFileStorage(storage.RootPath, tenant, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileSystemFileStorage>>()),
                _ => new InMemoryFileStorage(sp.GetRequiredService<InMemoryObjectStore>(), tenant, clock, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InMemoryFileStorage>>()),
            };
        });

        services.TryAddSingleton<IFileScanner, NoOpFileScanner>();
        services.TryAddSingleton<FilesRateLimiter>();
        services.TryAddSingleton<IFilesRateLimiter>(sp => sp.GetRequiredService<FilesRateLimiter>());
        services.TryAddScoped<IUploadStager, TempFileUploadStager>();

        services.TryAddScoped<IFilesUnitOfWork>(sp => sp.GetRequiredService<FilesDbContext>());
        services.TryAddScoped<IFileAttachmentRepository, FileAttachmentRepository>();
        services.TryAddScoped<IFilesReadStore, FilesReadStore>();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageReporter, FilesUsageReporter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ITenantDataEraser, FilesObjectEraser>());

        services.TryAddScoped<FilesPurgeJob>();
        services.TryAddScoped<FilesReconciliationJob>();
        return services;
    }
}

/// <summary>Ortama göre varsayılanlar: Production → <c>s3</c> + şifreleme <c>required</c>; Testing → <c>memory</c>; diğerleri → <c>filesystem</c> + şifreleme <c>none</c>.</summary>
public sealed class FilesOptionsDefaults(IHostEnvironment environment) : IPostConfigureOptions<FilesOptions>
{
    public const string TestingEnvironment = "Testing";

    public void PostConfigure(string? name, FilesOptions options)
    {
        var storage = options.Storage;
        storage.Provider = (storage.Provider ?? string.Empty).Trim().ToLowerInvariant();
        storage.Encryption = (storage.Encryption ?? string.Empty).Trim().ToLowerInvariant();
        if (storage.Provider.Length == 0)
        {
            storage.Provider = environment.IsProduction()
                ? StorageProviders.S3
                : environment.IsEnvironment(TestingEnvironment) ? StorageProviders.Memory : StorageProviders.FileSystem;
        }

        if (storage.Encryption.Length == 0)
        {
            storage.Encryption = environment.IsProduction() ? StorageEncryptionModes.Required : StorageEncryptionModes.None;
        }

        if (storage.Provider == StorageProviders.FileSystem && string.IsNullOrWhiteSpace(storage.RootPath))
        {
            storage.RootPath = Path.Combine(environment.ContentRootPath, ".data", "files");
        }
    }
}

/// <summary><see cref="FilesOptionsValidator"/>'ı ortam bilgisiyle çalıştırır (Production'da <c>Provider != s3</c> ya da şifresiz kurulum başlatmaz).</summary>
public sealed class FilesOptionsValidation(IHostEnvironment environment) : IValidateOptions<FilesOptions>
{
    public ValidateOptionsResult Validate(string? name, FilesOptions options)
    {
        var errors = FilesOptionsValidator.Validate(options, environment.IsProduction());
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
