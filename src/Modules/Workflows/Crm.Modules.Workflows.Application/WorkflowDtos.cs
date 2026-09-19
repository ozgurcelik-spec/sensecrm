using System.Text.Json;
using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Modules.Workflows.Domain.Rules;

namespace Crm.Modules.Workflows.Application;

/// <summary>Kural (yanıt). <see cref="Params"/> türe göre şemalı nesnedir (camelCase).</summary>
public sealed record RuleDto(
    Guid Id,
    string Name,
    WorkflowRuleKind Kind,
    bool IsEnabled,
    JsonElement Params,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Yürütme özeti (liste ve detay tabanı).</summary>
public sealed record ExecutionDto(
    Guid Id,
    Guid RuleId,
    string RuleName,
    WorkflowRuleKind Kind,
    ExecutionStatus Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    ExecutionSubjectType SubjectType,
    Guid SubjectId,
    string? SubjectName,
    string? Error);

/// <summary>Yürütme detayında onay özeti.</summary>
public sealed record ExecutionApprovalDto(Guid Id, string ApproverName, ApprovalStatus Status, DateTime? DecidedAt);

/// <summary>Yürütme detayı: özet + motordan adımlar + (varsa) onaylar. Motora ulaşılamazsa <see cref="Steps"/> boştur.</summary>
public sealed record ExecutionDetailDto(
    Guid Id,
    Guid RuleId,
    string RuleName,
    WorkflowRuleKind Kind,
    ExecutionStatus Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    ExecutionSubjectType SubjectType,
    Guid SubjectId,
    string? SubjectName,
    string? Error,
    IReadOnlyList<WorkflowStepDto> Steps,
    IReadOnlyList<ExecutionApprovalDto>? Approvals);

/// <summary>Detaydaki adım (motorun ham durum metniyle).</summary>
public sealed record WorkflowStepDto(string Name, string Status, DateTime? StartedAt, DateTime? EndedAt, JsonElement? Output);

/// <summary>Onay (yanıt).</summary>
public sealed record ApprovalDto(
    Guid Id,
    Guid ExecutionId,
    string Title,
    ExecutionSubjectType SubjectType,
    Guid SubjectId,
    string? SubjectName,
    decimal? Amount,
    string? Currency,
    DateTime RequestedAt,
    ApprovalStatus Status,
    Guid ApproverUserId,
    string ApproverName,
    DateTime? DecidedAt,
    string? Comment);

/// <summary>Üst çubuk rozeti: çağıranın bekleyen onay sayısı.</summary>
public sealed record ApprovalSummaryDto(int PendingCount);
