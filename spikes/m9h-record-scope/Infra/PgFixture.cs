using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Model;
using Testcontainers.PostgreSql;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Infra;

[CollectionDefinition(Name)]
public sealed class PgCollection : ICollectionFixture<PgFixture>
{
    public const string Name = "h0-pg";
}

/// <summary>
/// Tek atılabilir PostgreSQL container'ı: adı SABİT <c>crm-h0-pg</c> (postgres:17-alpine). Başka container'lara dokunulmaz;
/// artık bir <c>crm-h0-pg</c> kaldıysa (yalnız bizim adımız) elle silinmelidir: <c>docker rm -f crm-h0-pg</c>.
/// </summary>
public sealed class PgFixture : IAsyncLifetime
{
    public const string ContainerName = "crm-h0-pg";

    /// <summary>Geliştirme hızı için: <c>H0_KEEP=1</c> ise container test sonunda silinmez ve sonraki koşuda yeniden kullanılır (tohumlanmış 300k satır 1-3 dk sürer). Varsayılan: silinir.</summary>
    private static readonly bool Keep = Environment.GetEnvironmentVariable("H0_KEEP") == "1";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine")
        .WithName(ContainerName)
        .WithReuse(Keep)
        .WithDatabase("h0")
        .WithUsername("crm")
        .WithPassword(Keep ? "h0-throwaway-local" : Guid.NewGuid().ToString("N"))
        // Ölçüm/tohumlama hızı: dayanıklılık kapalı (okuma planlarını etkilemez); shared_buffers üretim küçük-kiracı boyutuna yakın.
        .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off", "-c", "shared_buffers=256MB", "-c", "max_connections=300")
        .Build();

    public string ConnectionString => _pg.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _pg.StartAsync();
        await using var ctx = CreateStatic();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!Keep)
        {
            await _pg.DisposeAsync();
        }
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public SpikeStaticDbContext CreateStatic(SqlCapture? capture = null) => new(Options<SpikeStaticDbContext>(capture), new TenantContext());

    public SpikeMemberDbContext CreateMember(SqlCapture? capture = null) => new(Options<SpikeMemberDbContext>(capture), new TenantContext());

    public SpikeCtxArgDbContext CreateCtxArg(SqlCapture? capture = null) => new(Options<SpikeCtxArgDbContext>(capture), new TenantContext());

    public SpikeDbContextBase Create(FilterStyle style, SqlCapture? capture = null) => style switch
    {
        FilterStyle.StaticCalls => CreateStatic(capture),
        FilterStyle.ContextMember => CreateMember(capture),
        _ => CreateCtxArg(capture),
    };

    public IDbContextFactory<SpikeDbContextBase> Pooled(FilterStyle style, SqlCapture? capture = null) => style switch
    {
        FilterStyle.StaticCalls => new Wrap<SpikeStaticDbContext>(PooledFactory<SpikeStaticDbContext>(capture)),
        FilterStyle.ContextMember => new Wrap<SpikeMemberDbContext>(PooledFactory<SpikeMemberDbContext>(capture)),
        _ => new Wrap<SpikeCtxArgDbContext>(PooledFactory<SpikeCtxArgDbContext>(capture)),
    };

    private sealed class Wrap<T>(IDbContextFactory<T> inner) : IDbContextFactory<SpikeDbContextBase>
        where T : SpikeDbContextBase
    {
        public SpikeDbContextBase CreateDbContext() => inner.CreateDbContext();
    }

    public DbContextOptions<T> Options<T>(SqlCapture? capture = null)
        where T : DbContext
    {
        var b = new DbContextOptionsBuilder<T>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention();
        if (capture is not null)
        {
            b.AddInterceptors(capture);
        }

        return b.Options;
    }

    /// <summary>Havuzlu fabrika (AddPooledDbContextFactory): havuz + AsyncLocal kapsam etkileşimini test etmek için.</summary>
    public IDbContextFactory<T> PooledFactory<T>(SqlCapture? capture = null)
        where T : DbContext
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext, TenantContext>(); // TenantContext statik AsyncLocal okur: singleton olabilir
        services.AddPooledDbContextFactory<T>(o =>
        {
            o.UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention();
            if (capture is not null)
            {
                o.AddInterceptors(capture);
            }
        });
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<T>>();
    }

    public async Task<int> ExecAsyncOn(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 300 };
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> ExecAsync(string sql, params object[] args)
    {
        await using var c = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        for (var i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", args[i]);
        }

        return await cmd.ExecuteNonQueryAsync();
    }
}

public sealed record CapturedCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

/// <summary>Yürütülen SQL + parametreleri yakalar (EXPLAIN ve "tek SQL metni" doğrulamaları için).</summary>
public sealed class SqlCapture : DbCommandInterceptor
{
    private readonly List<CapturedCommand> _commands = [];

    public IReadOnlyList<CapturedCommand> Commands
    {
        get
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }
    }

    public CapturedCommand Last => Commands[^1];

    public void Clear()
    {
        lock (_commands)
        {
            _commands.Clear();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        var ps = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToList();
        lock (_commands)
        {
            _commands.Add(new CapturedCommand(command.CommandText, ps));
        }
    }
}
