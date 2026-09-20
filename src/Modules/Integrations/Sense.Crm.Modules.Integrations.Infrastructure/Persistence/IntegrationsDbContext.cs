using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.ApiKeys;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Persistence;

/// <summary>Integrations modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması).</summary>
public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext), IIntegrationsUnitOfWork
{
    public const string SchemaName = "integrations";

    public override string Schema => SchemaName;

    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();

    public DbSet<DeliveryQueueItem> DeliveryQueue => Set<DeliveryQueueItem>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<ApiKeyUsageDay> ApiKeyUsageDays => Set<ApiKeyUsageDay>();
}

internal static class IntegrationsTables
{
    public const string WebhookSubscriptions = "webhook_subscriptions";
    public const string WebhookDeliveries = "webhook_deliveries";
    public const string WebhookDeliveryAttempts = "webhook_delivery_attempts";
    public const string DeliveryQueue = "delivery_queue";
    public const string ApiKeys = "api_keys";
    public const string ApiKeyUsageDaily = "api_key_usage_daily";
}

// Tüm kiracı sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3); delivery_queue küreseldir (dispatcher taraması due_at).

public sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> b)
    {
        b.ToTable(IntegrationsTables.WebhookSubscriptions);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(IntegrationsLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Url).HasMaxLength(IntegrationsLimits.UrlMaxLength).IsRequired();
        b.Property(x => x.Host).HasMaxLength(IntegrationsLimits.HostMaxLength).IsRequired();
        b.Property(x => x.EventTypes).HasColumnType("text[]").IsRequired();
        b.Property(x => x.DisabledReason).HasMaxLength(IntegrationsLimits.DisabledReasonMaxLength);
        b.Property(x => x.Description).HasMaxLength(IntegrationsLimits.DescriptionMaxLength);
        b.Property(x => x.SecretEnc).IsRequired();
        b.Property(x => x.SecretKeyId).HasMaxLength(IntegrationsLimits.SecretKeyIdMaxLength).IsRequired();
        b.Property(x => x.SecretLast4).HasColumnType("char(4)").IsRequired();
        b.Property(x => x.PreviousSecretKeyId).HasMaxLength(IntegrationsLimits.SecretKeyIdMaxLength);
        b.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Enabled });
        b.Ignore(x => x.DomainEvents);
        b.Ignore(x => x.NextSecretVersion);
        b.Ignore(x => x.PreviousSecretVersion);
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable(IntegrationsTables.WebhookDeliveries);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).HasMaxLength(IntegrationsLimits.EventTypeMaxLength).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(IntegrationsLimits.KindMaxLength).IsRequired();
        b.Property(x => x.Status).HasMaxLength(IntegrationsLimits.StatusMaxLength).IsRequired();

        // İmzalanan tam bayt dizisi: text (jsonb DEĞİL: anahtar sırası bozulup imza geçersiz olurdu).
        b.Property(x => x.Payload).HasColumnType("text").IsRequired();
        b.Property(x => x.Host).HasMaxLength(IntegrationsLimits.HostMaxLength).IsRequired();
        b.Property(x => x.FailureReason).HasMaxLength(IntegrationsLimits.FailureReasonMaxLength);
        b.HasOne<WebhookSubscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.CreatedAt }).IsDescending(false, false, true);
        b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt }).IsDescending(false, false, true);

        // En az bir kez teslimat, idempotent fan-out: (abonelik, olay) yalnız kind = event için benzersiz.
        b.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.EventId }).IsUnique().HasFilter("kind = 'event'");
        b.Ignore(x => x.IsRedeliverable);
    }
}

public sealed class WebhookDeliveryAttemptConfiguration : IEntityTypeConfiguration<WebhookDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryAttempt> b)
    {
        b.ToTable(IntegrationsTables.WebhookDeliveryAttempts);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FailureReason).HasMaxLength(IntegrationsLimits.FailureReasonMaxLength);
        b.Property(x => x.ErrorDetail).HasMaxLength(IntegrationsLimits.ErrorDetailMaxLength);
        b.Property(x => x.ResponseSnippet).HasMaxLength(IntegrationsLimits.ResponseSnippetMaxLength);
        b.HasOne<WebhookDelivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.DeliveryId, x.AttemptNo }).IsUnique();
    }
}

/// <summary>Küresel teknik kuyruk (kiracı filtresiz, <c>ITenantEntity</c> değil); dispatcher taraması <c>due_at</c>, imha <c>tenant_id</c> indeksiyle.</summary>
public sealed class DeliveryQueueItemConfiguration : IEntityTypeConfiguration<DeliveryQueueItem>
{
    public void Configure(EntityTypeBuilder<DeliveryQueueItem> b)
    {
        b.ToTable(IntegrationsTables.DeliveryQueue);
        b.HasKey(x => x.DeliveryId);
        b.Property(x => x.DeliveryId).ValueGeneratedNever();
        b.HasIndex(x => x.DueAt).HasFilter("locked_until IS NULL");
        b.HasIndex(x => x.TenantId);
    }
}

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable(IntegrationsTables.ApiKeys);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(IntegrationsLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(IntegrationsLimits.DescriptionMaxLength);
        b.Property(x => x.Prefix).HasColumnType("char(8)").IsRequired();
        b.Property(x => x.SecretHash).HasColumnType("bytea").IsRequired();
        b.Property(x => x.Scopes).HasColumnType("text[]").IsRequired();
        b.Property(x => x.AllowedCidrs).HasColumnType("text[]").IsRequired();
        b.Property(x => x.LastUsedIp).HasMaxLength(IntegrationsLimits.IpMaxLength);
        b.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.Prefix }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("revoked_at IS NULL");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);
        b.Ignore(x => x.DomainEvents);
        b.Ignore(x => x.IsRevoked);
    }
}

public sealed class ApiKeyUsageDayConfiguration : IEntityTypeConfiguration<ApiKeyUsageDay>
{
    public void Configure(EntityTypeBuilder<ApiKeyUsageDay> b)
    {
        b.ToTable(IntegrationsTables.ApiKeyUsageDaily);
        b.HasKey(x => new { x.TenantId, x.ApiKeyId, x.Day });
    }
}
