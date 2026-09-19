using System.Security.Claims;
using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Crm.Shared.Infrastructure.Tests.Observability;

/// <summary>
/// Enricher'lar TenantContext/CorrelationIdContext'in statik AsyncLocal'ini (DI kapsamı olmadan) okur; bu yüzden
/// Serilog logger'ı kurup gerçek bir log olayı üretip yayılan property'leri doğrulamak en gerçekçi test yoludur.
/// </summary>
public sealed class EnricherTests
{
    [Fact]
    public void TenantEnricher_AddsTenantId_WhenTenantResolved()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().Enrich.With<TenantEnricher>().WriteTo.Sink(sink).CreateLogger();
        var tenant = new TenantContext();
        var tenantId = Guid.CreateVersion7();

        using (tenant.BeginScope(tenantId))
        {
            logger.Information("probe");
        }

        var evt = sink.Events.ShouldHaveSingleItem();
        evt.Properties.ShouldContainKey(TenantEnricher.PropertyName);
        evt.Properties[TenantEnricher.PropertyName].ToString().ShouldContain(tenantId.ToString());
    }

    [Fact]
    public async Task TenantEnricher_DoesNotAddProperty_WhenTenantNotResolved()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().Enrich.With<TenantEnricher>().WriteTo.Sink(sink).CreateLogger();

        // Not: TenantContext statik AsyncLocal'e dayandığından, aynı process içinde başka testler tarafından
        // bırakılmış bir scope olmadığını garanti etmek için yeni bir async akışta (Task.Run) çalıştırıyoruz.
        await Task.Run(() => logger.Information("probe"), TestContext.Current.CancellationToken);

        var evt = sink.Events.ShouldHaveSingleItem();
        evt.Properties.ShouldNotContainKey(TenantEnricher.PropertyName);
    }

    [Fact]
    public void CorrelationIdEnricher_AddsCorrelationId_WhenScopeIsActive()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().Enrich.With<CorrelationIdEnricher>().WriteTo.Sink(sink).CreateLogger();
        var context = new CorrelationIdContext();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        using (context.BeginScope(correlationId))
        {
            logger.Information("probe");
        }

        var evt = sink.Events.ShouldHaveSingleItem();
        evt.Properties.ShouldContainKey(CorrelationIdEnricher.PropertyName);
        evt.Properties[CorrelationIdEnricher.PropertyName].ToString().ShouldContain(correlationId);
    }

    [Fact]
    public void CorrelationIdContext_BeginScope_RestoresPreviousValueOnDispose()
    {
        var context = new CorrelationIdContext();

        using (context.BeginScope("outer"))
        {
            context.CorrelationId.ShouldBe("outer");

            using (context.BeginScope("inner"))
            {
                context.CorrelationId.ShouldBe("inner");
            }

            context.CorrelationId.ShouldBe("outer");
        }

        context.CorrelationId.ShouldBeNull();
    }

    [Fact]
    public void UserEnricher_AddsUserIdAndEmail_WhenAuthenticated()
    {
        var sink = new CapturingSink();
        var userId = Guid.CreateVersion7().ToString();
        const string Email = "user@example.com";

        var identity = new ClaimsIdentity(
        [
            new Claim("sub", userId),
            new Claim("email", Email),
        ], authenticationType: "Test");

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var logger = new LoggerConfiguration().Enrich.With(new UserEnricher(accessor)).WriteTo.Sink(sink).CreateLogger();
        logger.Information("probe");

        var evt = sink.Events.ShouldHaveSingleItem();
        evt.Properties[UserEnricher.UserIdProperty].ToString().ShouldContain(userId);
        evt.Properties[UserEnricher.UserEmailProperty].ToString().ShouldContain(Email);
    }

    [Fact]
    public void UserEnricher_DoesNothing_WhenNoHttpContext()
    {
        var sink = new CapturingSink();
        var accessor = new HttpContextAccessor();
        var logger = new LoggerConfiguration().Enrich.With(new UserEnricher(accessor)).WriteTo.Sink(sink).CreateLogger();

        logger.Information("probe");

        var evt = sink.Events.ShouldHaveSingleItem();
        evt.Properties.ShouldNotContainKey(UserEnricher.UserIdProperty);
        evt.Properties.ShouldNotContainKey(UserEnricher.UserEmailProperty);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
