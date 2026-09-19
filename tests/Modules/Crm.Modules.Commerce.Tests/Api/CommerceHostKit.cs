using Crm.Modules.Commerce.Domain.Orders;
using Crm.Modules.Commerce.Domain.Quotes;
using Crm.Modules.Commerce.Infrastructure.Persistence;
using Crm.Tests.Shared.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crm.Modules.Commerce.Tests.Api;

/// <summary>
/// Testte saati sabitlemek için <see cref="TimeProvider"/>: <see cref="Override"/> doluyken o anı, boşken gerçek saati döner.
/// JWT doğrulaması gerçek saati kullanır; bu yüzden yalnız iş mantığının gördüğü saat değiştirilir.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset? Override { get; set; }

    public override DateTimeOffset GetUtcNow() => Override ?? base.GetUtcNow();
}

/// <summary>Hata enjeksiyonu kipleri: SaveChanges'i belirli varlık eklenirken patlatır (atomiklik testleri).</summary>
internal static class Faults
{
    public const string FailOnQuoteInsert = "quote";
    public const string FailOnOrderLineInsert = "orderLine";

    public static volatile string? Mode;
}

internal sealed class FaultInjectionInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Faults.Mode is { } mode && eventData.Context is { } context)
        {
            var added = context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
            var fail = (mode == Faults.FailOnQuoteInsert && added.OfType<Quote>().Any())
                || (mode == Faults.FailOnOrderLineInsert && added.OfType<SalesOrderLine>().Any());
            if (fail)
            {
                throw new InvalidOperationException("Injected commerce persistence failure.");
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

internal static class CommerceHostKit
{
    /// <summary>Paylaşılan veritabanında, saati/hata enjeksiyonunu kontrol edebildiğimiz ikinci bir API host'u.</summary>
    public static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntry> Derive<TEntry>(this Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntry> factory, TestClock clock)
        where TEntry : class =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.ConfigureDbContext<CommerceDbContext>(options => options.AddInterceptors(new FaultInjectionInterceptor()));
        }));
}
