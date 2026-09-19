using System.Text.Json;
using System.Text.Json.Nodes;
using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Persistence;

namespace Sense.Crm.Modules.Workflows.Application;

/// <summary>Workflows modülünün UnitOfWork'ü (modül DbContext'i uygular); event/worker yollarında SaveChanges için.</summary>
public interface IWorkflowsUnitOfWork : IUnitOfWork
{
}

/// <summary>Workflow motoruna başlatma isteği. <see cref="Input"/> Conductor workflow girdisidir (kiracı kimliği dahil).</summary>
public sealed record StartWorkflowRequest(string Name, int Version, string CorrelationId, JsonObject Input);

/// <summary>Motorun bildirdiği bir adım (Conductor görevi). <see cref="Status"/> motorun ham durumudur (<c>COMPLETED</c>, <c>IN_PROGRESS</c> …).</summary>
public sealed record WorkflowStep(string Name, string Status, DateTime? StartedAt, DateTime? EndedAt, JsonElement? Output);

/// <summary>Motordaki bir workflow'un anlık durumu: CRM durumuna eşlenmiş <see cref="Status"/>, varsa <see cref="Error"/> ve adımlar.</summary>
public sealed record WorkflowState(ExecutionStatus Status, string? Error, IReadOnlyList<WorkflowStep> Steps);

/// <summary>
/// Workflow motoru portu (Conductor OSS arkasında). CRM iş mantığı yalnız bu arayüzü bilir; testlerde sahte motor,
/// canlıda <c>ConductorWorkflowEngine</c> (REST) kullanılır.
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>Workflow'u başlatır; motorun workflow kimliğini döner. Ulaşılamazsa/reddedilirse istisna fırlatır.</summary>
    Task<string> StartAsync(StartWorkflowRequest request, CancellationToken cancellationToken);

    /// <summary>Workflow durumu ve adımları; motor böyle bir workflow bilmiyorsa null.</summary>
    Task<WorkflowState?> GetAsync(string workflowId, CancellationToken cancellationToken);

    /// <summary>Çalışan workflow'u sonlandırır (idempotent: zaten sonlanmışsa hata vermez).</summary>
    Task TerminateAsync(string workflowId, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Workflow'un beklediği insan/bekleme (HUMAN/WAIT) görevini <paramref name="output"/> ile tamamlar (onay kararı). Görev
    /// zaten tamamlanmışsa hata vermez (idempotent).
    /// </summary>
    Task CompleteWaitTaskAsync(string workflowId, string taskReferenceName, IReadOnlyDictionary<string, object?> output, CancellationToken cancellationToken);
}

/// <summary>Sürümlü workflow ve görev tanımlarını motora idempotent kaydeder (API ve Worker başlangıcında).</summary>
public interface IWorkflowDefinitionRegistrar
{
    Task EnsureRegisteredAsync(CancellationToken cancellationToken);
}

/// <summary>Çalışmakta olan (sistem genelinde, kiracıdan bağımsız) bir yürütmeye işaret eder; durum senkronu için.</summary>
public sealed record RunningExecutionRef(Guid TenantId, Guid ExecutionId);

/// <summary>
/// Yalnız durum senkronu için kiracı filtresi dışında, dar bir projeksiyonla çalışan kaynak (Worker). Diğer her okuma/yazma
/// kiracı kapsamı altında yapılır.
/// </summary>
public interface IRunningExecutionSource
{
    Task<IReadOnlyList<RunningExecutionRef>> ListRunningAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>Yürütme listesi filtreleri (<c>from</c>/<c>to</c> <c>startedAt</c> anları, uçlar dahil; <c>subjectType</c>/<c>subjectId</c> lead/fırsat detayındaki durum şeridi için).</summary>
public sealed record ExecutionFilter(ExecutionStatus? Status, Guid? RuleId, DateTime? From, DateTime? To, ExecutionSubjectType? SubjectType = null, Guid? SubjectId = null);

/// <summary>Onay listesi filtreleri: <paramref name="ApproverUserId"/> doluysa yalnız o kullanıcının onayları.</summary>
public sealed record ApprovalFilter(Domain.Approvals.ApprovalStatus? Status, Guid? ApproverUserId);

/// <summary>
/// Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı ve yumuşak silme filtresi altında
/// üretilir. Onaylayıcı adları toplu çözülür.
/// </summary>
public interface IWorkflowReadStore
{
    Task<IReadOnlyList<RuleDto>> ListRulesAsync(CancellationToken ct);

    Task<RuleDto?> GetRuleAsync(Guid id, CancellationToken ct);

    /// <summary>En yeni başlayan önce.</summary>
    Task<PagedResult<ExecutionDto>> ListExecutionsAsync(PagedQuery paging, ExecutionFilter filter, CancellationToken ct);

    /// <summary>Yürütme + onayları (adımlar motordan handler'da eklenir); yoksa null.</summary>
    Task<(ExecutionDto Execution, string? EngineWorkflowId, IReadOnlyList<ExecutionApprovalDto> Approvals)?> GetExecutionAsync(Guid id, CancellationToken ct);

    /// <summary>En yeni talep önce.</summary>
    Task<PagedResult<ApprovalDto>> ListApprovalsAsync(PagedQuery paging, ApprovalFilter filter, CancellationToken ct);

    Task<ApprovalDto?> GetApprovalAsync(Guid id, CancellationToken ct);

    Task<int> CountPendingAsync(Guid approverUserId, CancellationToken ct);
}
