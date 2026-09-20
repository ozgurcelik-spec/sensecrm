using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Application.Files;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Infrastructure.Storage;
using Sense.Crm.Shared.Contracts.Context;
using Shouldly;
using Testcontainers.Minio;
using Xunit;

namespace Sense.Crm.Modules.Files.Tests.Storage;

public sealed class InMemoryFileStorageTests : FileStorageContractTests
{
    private readonly InMemoryObjectStore _store = new();

    protected override IFileStorage Create(ITenantContext tenant) => new InMemoryFileStorage(_store, tenant, TimeProvider.System, Log<InMemoryFileStorage>());

    [Fact]
    public async Task ForeignKeysUnderTheTenantPrefix_AreListedWithoutAParsedKey()
    {
        var tenant = Guid.NewGuid();
        var (context, scope) = TenantScope(tenant);
        using var _ = scope;
        var storage = Create(context);
        _store.SeedRaw(ObjectKey.TenantPrefix(tenant) + "not-a-valid-key", [1, 2]);
        _store.SeedRaw(ObjectKey.For(tenant, 2026, Guid.CreateVersion7()).ToString(), [1]);

        var items = new List<ObjectInfo>();
        await foreach (var item in storage.ListAsync(tenant, Ct))
        {
            items.Add(item);
        }

        items.Count.ShouldBe(2);
        items.Count(i => i.Key is null).ShouldBe(1);
    }
}

public sealed class FileSystemFileStorageTests : FileStorageContractTests, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crm-fs-test-" + Guid.NewGuid().ToString("N"));

    protected override IFileStorage Create(ITenantContext tenant) => new FileSystemFileStorage(_root, tenant, Log<FileSystemFileStorage>());

    [Fact]
    public async Task Files_LiveOnlyUnderTheRootAtTheKeyLayout_AndNoTempFilesRemain()
    {
        var tenant = Guid.NewGuid();
        var (context, scope) = TenantScope(tenant);
        using var _ = scope;
        var storage = Create(context);
        var key = ObjectKey.For(tenant, 2026, Guid.CreateVersion7());
        await using (var stream = new MemoryStream([1, 2, 3]))
        {
            await storage.PutAsync(key, stream, 3, Ct);
        }

        var expected = Path.Combine(_root, tenant.ToString("D"), "2026", key.FileId.ToString("N"));
        File.Exists(expected).ShouldBeTrue();
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Count().ShouldBe(1);
        Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task PutWithAWrongDeclaredLength_FailsAndLeavesNothingBehind()
    {
        var tenant = Guid.NewGuid();
        var (context, scope) = TenantScope(tenant);
        using var _ = scope;
        var storage = Create(context);
        var key = ObjectKey.For(tenant, 2026, Guid.CreateVersion7());
        await using var stream = new MemoryStream([1, 2, 3]);

        await Should.ThrowAsync<InvalidOperationException>(() => storage.PutAsync(key, stream, 99, Ct));
        (await storage.GetAsync(key, null, Ct)).ShouldBeNull();
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Gerçek MinIO Testcontainer'ı (sabit imaj etiketi; <c>CRM_TEST_MINIO_IMAGE</c> ile ezilebilir). İsteğe bağlı statik KMS anahtarı (SSE-S3 doğrulaması için).</summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string DefaultImage = "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z";
    public const string KmsKey = "crm-files-key:MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private MinioContainer? _container;

    public string Endpoint { get; private set; } = string.Empty;

    public string AccessKey => "crmtestadmin";

    public string SecretKey => "crmtestadmin-secret-1";

    public async ValueTask InitializeAsync()
    {
        var image = Environment.GetEnvironmentVariable("CRM_TEST_MINIO_IMAGE") is { Length: > 0 } custom ? custom : DefaultImage;
        _container = new MinioBuilder(image)
            .WithUsername(AccessKey)
            .WithPassword(SecretKey)
            .WithEnvironment("MINIO_KMS_SECRET_KEY", KmsKey)
            .Build();
        await _container.StartAsync();
        Endpoint = _container.GetConnectionString();
        if (!Endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Endpoint = "http://" + Endpoint;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public AmazonS3Client AdminClient() =>
        new(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    /// <summary>Kova oluşturur; <paramref name="encrypted"/> ise varsayılan SSE-S3 (AES256) ayarlar.</summary>
    public async Task<string> NewBucketAsync(bool encrypted)
    {
        var name = "crm-test-" + Guid.NewGuid().ToString("N")[..12];
        using var admin = AdminClient();
        await admin.PutBucketAsync(new PutBucketRequest { BucketName = name });
        if (encrypted)
        {
            await admin.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
            {
                BucketName = name,
                ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
                {
                    ServerSideEncryptionRules =
                    [
                        new ServerSideEncryptionRule { ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault { ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AES256 } },
                    ],
                },
            });
        }

        return name;
    }

    public S3StorageState NewState(string bucket, bool requireEncryption, string? endpoint = null) =>
        new(Options.Create(new FilesOptions
        {
            Storage =
            {
                Provider = StorageProviders.S3,
                Endpoint = endpoint ?? Endpoint,
                Bucket = bucket,
                AccessKey = AccessKey,
                SecretKey = SecretKey,
                Encryption = requireEncryption ? StorageEncryptionModes.Required : StorageEncryptionModes.None,
            },
        }));
}

/// <summary>S3 adaptörü gerçek MinIO'ya karşı: aynı sözleşme paketi (şifreleme <c>none</c>).</summary>
public sealed class S3FileStorageContractTests(MinioFixture minio) : FileStorageContractTests, IClassFixture<MinioFixture>, IAsyncLifetime
{
    private S3StorageState? _state;

    public async ValueTask InitializeAsync() => _state = minio.NewState(await minio.NewBucketAsync(encrypted: false), requireEncryption: false);

    public ValueTask DisposeAsync()
    {
        _state?.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override IFileStorage Create(ITenantContext tenant) => new S3FileStorage(_state!, tenant, TimeProvider.System, Log<S3FileStorage>());

    [Fact]
    public async Task StoredObjects_AreWrittenAsOctetStream_WithoutFileNameMetadata()
    {
        var tenant = Guid.NewGuid();
        var (context, scope) = TenantScope(tenant);
        using var _ = scope;
        var storage = Create(context);
        var key = ObjectKey.For(tenant, 2026, Guid.CreateVersion7());
        await using (var stream = new MemoryStream([1, 2, 3, 4]))
        {
            await storage.PutAsync(key, stream, 4, Ct);
        }

        using var admin = minio.AdminClient();
        var meta = await admin.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _state!.Bucket, Key = key.ToString() }, Ct);
        meta.Headers.ContentType.ShouldBe("application/octet-stream");
        meta.Metadata.Keys.ShouldBeEmpty();
        meta.Headers.ContentDisposition.ShouldBeNullOrEmpty();
    }
}

/// <summary>Şifreleme doğrulaması (<c>Encryption=required</c>): kovada varsayılan SSE-S3 yoksa sağlık <c>Unhealthy</c> ve yüklemeler reddedilir.</summary>
public sealed class S3EncryptionAndHealthTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TestTenant(Guid id) : ITenantContext
    {
        public Guid TenantId => id;

        public bool IsResolved => true;

        public string? TenantSlug => null;
    }

    private static S3FileStorage Storage(S3StorageState state, Guid tenant) =>
        new(state, new TestTenant(tenant), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<S3FileStorage>.Instance);

    [Fact]
    public async Task EncryptedBucket_IsHealthy_AndObjectsAreStoredEncrypted()
    {
        using var state = minio.NewState(await minio.NewBucketAsync(encrypted: true), requireEncryption: true);
        var tenant = Guid.NewGuid();
        var storage = Storage(state, tenant);

        var health = await storage.CheckAsync(Ct);
        health.Healthy.ShouldBeTrue(health.Detail);

        var key = ObjectKey.For(tenant, 2026, Guid.CreateVersion7());
        await using (var stream = new MemoryStream([1, 2, 3]))
        {
            await storage.PutAsync(key, stream, 3, Ct);
        }

        using var admin = minio.AdminClient();
        var meta = await admin.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = state.Bucket, Key = key.ToString() }, Ct);
        meta.ServerSideEncryptionMethod.ShouldNotBe(ServerSideEncryptionMethod.None, "varsayılan SSE-S3 nesneye uygulanmalı");
    }

    [Fact]
    public async Task UnencryptedBucket_WithEncryptionRequired_IsUnhealthy_AndRefusesToWrite()
    {
        using var state = minio.NewState(await minio.NewBucketAsync(encrypted: false), requireEncryption: true);
        var tenant = Guid.NewGuid();
        var storage = Storage(state, tenant);

        var health = await storage.CheckAsync(Ct);
        health.Healthy.ShouldBeFalse();

        var key = ObjectKey.For(tenant, 2026, Guid.CreateVersion7());
        await using var stream = new MemoryStream([1, 2, 3]);
        await Should.ThrowAsync<StorageUnavailableException>(() => storage.PutAsync(key, stream, 3, Ct));

        using var admin = minio.AdminClient();
        var listed = await admin.ListObjectsV2Async(new ListObjectsV2Request { BucketName = state.Bucket }, Ct);
        (listed.S3Objects ?? []).ShouldBeEmpty("şifreleme doğrulanamazken nesne yazılmamalı");
    }

    [Fact]
    public async Task UnencryptedBucket_WithEncryptionNone_IsHealthy()
    {
        using var state = minio.NewState(await minio.NewBucketAsync(encrypted: false), requireEncryption: false);
        (await Storage(state, Guid.NewGuid()).CheckAsync(Ct)).Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task MissingBucket_IsUnhealthy()
    {
        using var state = minio.NewState("crm-bucket-that-does-not-exist", requireEncryption: false);
        (await Storage(state, Guid.NewGuid()).CheckAsync(Ct)).Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task ServerDown_IsUnhealthy()
    {
        using var state = minio.NewState("crm-files", requireEncryption: false, endpoint: "http://127.0.0.1:1");
        (await Storage(state, Guid.NewGuid()).CheckAsync(Ct)).Healthy.ShouldBeFalse();
    }
}
