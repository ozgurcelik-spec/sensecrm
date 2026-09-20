using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Infrastructure.Storage;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Tests.Shared.Fixtures;
using Serilog.Core;
using Serilog.Events;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>Sahte tarayıcı: sonucu testler belirler; çağrı sayısını tutar.</summary>
internal sealed class FakeScanner : IFileScanner
{
    private int _calls;

    public Func<ScanResult> Result { get; set; } = () => ScanResult.Clean;

    public int Calls => Volatile.Read(ref _calls);

    public bool IsEnabled => true;

    public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Result());
    }
}

/// <summary>Depo davranışını yönlendiren ve çağrıları sayan paylaşılan durum (arızalanabilen sahte depo).</summary>
internal sealed class StorageFaults
{
    private int _puts;
    private int _deletes;

    private volatile bool _failPut;
    private volatile bool _failDelete;
    private volatile bool _failGet;
    private volatile bool _failList;

    public bool FailPut
    {
        get => _failPut;
        set => _failPut = value;
    }

    public bool FailDelete
    {
        get => _failDelete;
        set => _failDelete = value;
    }

    public bool FailGet
    {
        get => _failGet;
        set => _failGet = value;
    }

    public bool FailList
    {
        get => _failList;
        set => _failList = value;
    }

    /// <summary>Doluysa <c>PutAsync</c> bu görev tamamlanana kadar bekler (slotu/işlemi tutan yavaş yükleme senaryoları).</summary>
    public Task? PutGate { get; set; }

    /// <summary>İlk <c>PutAsync</c> çağrısı gelince tamamlanır.</summary>
    public TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Puts => Volatile.Read(ref _puts);

    public int Deletes => Volatile.Read(ref _deletes);

    internal void CountPut() => Interlocked.Increment(ref _puts);

    internal void CountDelete() => Interlocked.Increment(ref _deletes);
}

/// <summary>Gerçek (bellek içi) depoyu saran arızalanabilen sahte depo.</summary>
internal sealed class FaultyStorage(IFileStorage inner, StorageFaults faults) : IFileStorage
{
    public async Task PutAsync(ObjectKey key, Stream content, long length, CancellationToken ct)
    {
        faults.CountPut();
        faults.PutEntered.TrySetResult();
        if (faults.PutGate is { } gate)
        {
            await gate.WaitAsync(ct);
        }

        if (faults.FailPut)
        {
            throw new IOException("Simulated storage outage (put)");
        }

        await inner.PutAsync(key, content, length, ct);
    }

    public Task<StoredObject?> GetAsync(ObjectKey key, ByteRange? range, CancellationToken ct) =>
        faults.FailGet ? throw new IOException("Simulated storage outage (get)") : inner.GetAsync(key, range, ct);

    public Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken ct) => inner.StatAsync(key, ct);

    public Task<bool> DeleteAsync(ObjectKey key, CancellationToken ct)
    {
        faults.CountDelete();
        return faults.FailDelete ? throw new IOException("Simulated storage outage (delete)") : inner.DeleteAsync(key, ct);
    }

    public Task<long> DeletePrefixAsync(Guid tenantId, CancellationToken ct) => inner.DeletePrefixAsync(tenantId, ct);

    public IAsyncEnumerable<ObjectInfo> ListAsync(Guid tenantId, CancellationToken ct) =>
        faults.FailList ? throw new IOException("Simulated storage outage (list)") : inner.ListAsync(tenantId, ct);

    public Task<StorageHealth> CheckAsync(CancellationToken ct) => inner.CheckAsync(ct);
}

/// <summary>Günlük havuzu: Serilog olaylarını toplar (dosya adı sızıntısı testi).</summary>
internal sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public void Emit(LogEvent logEvent)
    {
        var properties = string.Join(" ", logEvent.Properties.Select(p => p.Key + "=" + p.Value));
        _lines.Enqueue(logEvent.RenderMessage() + " | " + properties + " | " + logEvent.Exception);
    }
}

/// <summary>Files kullanım sayacı çağrılarını sayan sarmalayıcı (sınırsız kotada <b>sıfır</b> sayım sorgusu testi).</summary>
internal sealed class CountingFilesReporter(Sense.Crm.Modules.Files.Infrastructure.FilesUsageReporter inner, UsageCounter counter) : Sense.Crm.Shared.Contracts.Usage.IUsageReporter
{
    public string Module => inner.Module;

    public Task<IReadOnlyList<Sense.Crm.Shared.Contracts.Usage.UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        counter.Increment();
        return inner.ReportAsync(ct);
    }
}

internal sealed class UsageCounter
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Increment() => Interlocked.Increment(ref _calls);
}

/// <summary>Bir test için yapılandırılmış API host'u (Files ayarları + sahte tarayıcı/depo/günlük).</summary>
internal sealed class FilesHost(WebApplicationFactory<Program> app, StorageFaults faults, FakeScanner scanner, CapturingSink logs, string tempDirectory, UsageCounter usage) : IAsyncDisposable
{
    public WebApplicationFactory<Program> App => app;

    /// <summary><c>files.storage_bytes</c>/<c>files.files</c> sayımının çağrı sayısı.</summary>
    public int UsageQueries => usage.Calls;

    public StorageFaults Faults => faults;

    public FakeScanner Scanner => scanner;

    public CapturingSink Logs => logs;

    public string TempDirectory => tempDirectory;

    public int TempFileCount => Directory.Exists(tempDirectory) ? Directory.GetFiles(tempDirectory).Length : 0;

    /// <summary>Paylaşılan test factory'sinden, verilen <c>Files</c> ayarlarıyla yeni bir host türetir.</summary>
    public static FilesHost Create(CrmApiFactory factory, params (string Key, string Value)[] settings) => Create(factory, null, settings);

    /// <summary>Elle yönetilen saatli host (temizlik/uzlaştırma/imha zamanlaması testleri): <see cref="TimeProvider"/> = <paramref name="clock"/>.</summary>
    public static FilesHost Create(CrmApiFactory factory, TestClock? clock, params (string Key, string Value)[] settings)
    {
        var faults = new StorageFaults();
        var scanner = new FakeScanner();
        var logs = new CapturingSink();
        var usage = new UsageCounter();
        var temp = Path.Combine(Path.GetTempPath(), "crm-files-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Files:Upload:TempDirectory", temp);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                }

                services.RemoveAll<IFileScanner>();
                services.AddSingleton(scanner);
                services.AddSingleton<IFileScanner>(sp => sp.GetRequiredService<FakeScanner>());
                services.AddSingleton(faults);
                services.RemoveAll<IFileStorage>();
                services.AddScoped<IFileStorage>(sp => new FaultyStorage(
                    new InMemoryFileStorage(
                        sp.GetRequiredService<InMemoryObjectStore>(),
                        sp.GetRequiredService<ITenantContext>(),
                        sp.GetRequiredService<TimeProvider>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InMemoryFileStorage>>()),
                    faults));
                services.AddSingleton<ILogEventSink>(logs);

                // Files kullanım sayacını çağrı sayan sarmalayıcıyla değiştir.
                var reporter = services.Single(d => d.ServiceType == typeof(Sense.Crm.Shared.Contracts.Usage.IUsageReporter)
                    && d.ImplementationType == typeof(Sense.Crm.Modules.Files.Infrastructure.FilesUsageReporter));
                services.Remove(reporter);
                services.AddScoped<Sense.Crm.Shared.Contracts.Usage.IUsageReporter>(sp =>
                    new CountingFilesReporter(ActivatorUtilities.CreateInstance<Sense.Crm.Modules.Files.Infrastructure.FilesUsageReporter>(sp), usage));
            });
        });
        return new FilesHost(app, faults, scanner, logs, temp, usage);
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
