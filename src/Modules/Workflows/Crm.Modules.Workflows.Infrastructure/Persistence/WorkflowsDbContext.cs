using Crm.Modules.Workflows.Application;
using Crm.Modules.Workflows.Domain;
using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Modules.Workflows.Domain.Rules;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Workflows.Infrastructure.Persistence;

/// <summary>Workflows modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması).</summary>
public sealed class WorkflowsDbContext(DbContextOptions<WorkflowsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext), IWorkflowsUnitOfWork
{
    public const string SchemaName = "workflows";

    public override string Schema => SchemaName;

    public DbSet<WorkflowRule> Rules => Set<WorkflowRule>();

    public DbSet<WorkflowExecution> Executions => Set<WorkflowExecution>();

    public DbSet<Approval> Approvals => Set<Approval>();
}

internal static class WorkflowsTables
{
    public const string Rules = "workflow_rules";
    public const string Executions = "workflow_executions";
    public const string Approvals = "approvals";
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3).

public sealed class WorkflowRuleConfiguration : IEntityTypeConfiguration<WorkflowRule>
{
    public void Configure(EntityTypeBuilder<WorkflowRule> b)
    {
        b.ToTable(WorkflowsTables.Rules);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(WorkflowLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(WorkflowLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.ParamsJson).HasColumnType(PersistenceDefaults.JsonbColumnType).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Kind, x.IsEnabled });
        b.Ignore(x => x.LeadAssignment);
        b.Ignore(x => x.DealApproval);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class WorkflowExecutionConfiguration : IEntityTypeConfiguration<WorkflowExecution>
{
    public void Configure(EntityTypeBuilder<WorkflowExecution> b)
    {
        b.ToTable(WorkflowsTables.Executions);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RuleName).HasMaxLength(WorkflowLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(WorkflowLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(WorkflowLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(WorkflowLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.SubjectName).HasMaxLength(WorkflowLimits.SubjectNameMaxLength);
        b.Property(x => x.Error).HasMaxLength(WorkflowLimits.ErrorMaxLength);
        b.Property(x => x.EngineWorkflowId).HasMaxLength(WorkflowLimits.EngineIdMaxLength);
        b.Property(x => x.InputJson).HasColumnType(PersistenceDefaults.JsonbColumnType).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Status, x.StartedAt });
        b.HasIndex(x => new { x.TenantId, x.RuleId, x.TriggerEventId, x.Attempt }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.SubjectType, x.SubjectId });
        // Durum senkronu (Worker, kiracılar arası, dar projeksiyon) yalnız çalışan yürütmeleri tarar.
        b.HasIndex(x => x.StartedAt).HasDatabaseName("ix_workflow_executions_running").HasFilter("status = 'Running'");
        b.Ignore(x => x.IsRunning);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class ApprovalConfiguration : IEntityTypeConfiguration<Approval>
{
    public void Configure(EntityTypeBuilder<Approval> b)
    {
        b.ToTable(WorkflowsTables.Approvals);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Title).HasMaxLength(WorkflowLimits.TitleMaxLength).IsRequired();
        b.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(WorkflowLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.SubjectName).HasMaxLength(WorkflowLimits.SubjectNameMaxLength);
        b.Property(x => x.Currency).HasMaxLength(WorkflowLimits.CurrencyMaxLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(WorkflowLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Comment).HasMaxLength(WorkflowLimits.CommentMaxLength);
        b.HasIndex(x => new { x.TenantId, x.ApproverUserId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.ExecutionId, x.ApproverUserId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.RequestedAt });
        b.Ignore(x => x.IsPending);
        b.Ignore(x => x.DomainEvents);
    }
}
