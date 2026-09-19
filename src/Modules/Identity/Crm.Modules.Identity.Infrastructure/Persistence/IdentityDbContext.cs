using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Memberships;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Tokens;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Identity.Infrastructure.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext), IIdentityUnitOfWork
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
}

internal static class IdentityTables
{
    public const string Tenants = "tenants";
    public const string Users = "users";
    public const string Memberships = "memberships";
    public const string Roles = "roles";
    public const string RefreshTokens = "refresh_tokens";

    public const int HashLength = 128;
    public const int StampLength = 64;
    public const int IpLength = 64;
}

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable(IdentityTables.Tenants);
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(IdentityLimits.OrganizationNameMaxLength).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(IdentityLimits.SlugMaxLength).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.DefaultLocale).HasMaxLength(IdentityLimits.LocaleMaxLength).IsRequired();
        b.Property(x => x.TimeZone).HasMaxLength(IdentityLimits.TimeZoneMaxLength).IsRequired();
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable(IdentityTables.Users);
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(IdentityLimits.EmailMaxLength).IsRequired();
        b.Property(x => x.NormalizedEmail).HasMaxLength(IdentityLimits.EmailMaxLength).IsRequired();
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Property(x => x.DisplayName).HasMaxLength(IdentityLimits.DisplayNameMaxLength).IsRequired();
        b.Property(x => x.PasswordHash).IsRequired();
        b.Property(x => x.Locale).HasMaxLength(IdentityLimits.LocaleMaxLength).IsRequired();
        b.Property(x => x.SecurityStamp).HasMaxLength(IdentityTables.StampLength).IsRequired();
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable(IdentityTables.Memberships);
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.RoleId });
        b.HasIndex(x => x.UserId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable(IdentityTables.Roles);
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(IdentityLimits.RoleNameMaxLength).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(IdentityLimits.RoleCodeMaxLength);
        b.Property(x => x.Permissions).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable(IdentityTables.RefreshTokens);
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(IdentityTables.HashLength).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.FamilyId);
        b.HasIndex(x => x.UserId);
        b.Property(x => x.ReplacedByTokenHash).HasMaxLength(IdentityTables.HashLength);
        b.Property(x => x.DeviceInfo).HasMaxLength(IdentityLimits.DeviceInfoMaxLength);
        b.Property(x => x.IpAddress).HasMaxLength(IdentityTables.IpLength);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>dotnet-ef migration üretimi için design-time context (bağlantı: CRM_DATABASE ortam değişkeni veya yerel varsayılan).</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, IdentityDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new IdentityDbContext(options, new TenantContext());
    }
}
