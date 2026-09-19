using System.Text.Json;
using Asp.Versioning;
using Crm.Modules.Workflows.Application;
using Crm.Modules.Workflows.Application.Approvals;
using Crm.Modules.Workflows.Application.Executions;
using Crm.Modules.Workflows.Application.Rules;
using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Modules.Workflows.Domain.Rules;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Modules.Workflows.Api.Controllers;

/// <summary>Workflows uç noktaları (docs/plan/m4-workflow.md). Taban: /api/v1. Yetki her sorgu/komutun [RequiresPermission]'ındadır.</summary>
public static class WorkflowsRoutes
{
    public const string Rules = ApiRoutes.VersionedBase + "/workflows/rules";
    public const string Executions = ApiRoutes.VersionedBase + "/workflows/executions";
    public const string Approvals = ApiRoutes.VersionedBase + "/approvals";
}

/// <summary>
/// Kural oluşturma gövdesi: <c>{ name, kind, params, isEnabled? }</c> (<c>isEnabled</c> verilmezse etkin). <c>params</c> türe göre
/// şemalıdır; hatalar <c>params.&lt;alan&gt;</c> altında döner.
/// </summary>
public sealed record CreateRuleRequest(string Name, WorkflowRuleKind? Kind, JsonElement? Params, bool? IsEnabled);

/// <summary>Kural güncelleme gövdesi (tam değiştirme): <c>{ name, kind, params }</c>; etkin durumu enable/disable uçlarıyla değişir.</summary>
public sealed record UpdateRuleRequest(string Name, WorkflowRuleKind? Kind, JsonElement? Params);

/// <summary>Workflow kuralları (<c>org.workflows.manage</c>). Silme yumuşaktır; her değişiklik denetim kaydına yazılır.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(WorkflowsRoutes.Rules)]
[Authorize]
public sealed class WorkflowRulesController : ApiControllerBase
{
    /// <summary>Organizasyonun tüm kuralları (düz dizi).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RuleDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListRulesQuery(), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<RuleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetRuleQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<RuleDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateRuleRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(new CreateRuleCommand(request.Name, request.Kind, request.Params, request.IsEnabled), ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetRuleQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRuleRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateRuleCommand(id, request.Name, request.Kind, request.Params), ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteRuleCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/enable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new EnableRuleCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DisableRuleCommand(id), ct));
}

/// <summary>Workflow yürütmeleri (<c>org.workflows.manage</c>): liste, detay (adımlar Conductor'dan), sonlandır, yeniden dene.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(WorkflowsRoutes.Executions)]
[Authorize]
public sealed class WorkflowExecutionsController : ApiControllerBase
{
    /// <summary>Filtre: <c>status, ruleId, from, to</c> (UTC anlar, uçlar dahil; <c>startedAt</c>), <c>subjectType</c> (lead|deal), <c>subjectId</c>. En yeni önce.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<ExecutionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] ExecutionStatus? status,
        [FromQuery] Guid? ruleId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ExecutionSubjectType? subjectType,
        [FromQuery] Guid? subjectId,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListExecutionsQuery(paging, new ExecutionFilter(status, ruleId, from, to, subjectType, subjectId)), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<ExecutionDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetExecutionQuery(id), ct));

    /// <summary>Yalnız çalışan yürütme; aksi <c>workflow.not_running</c> (409).</summary>
    [HttpPost(ApiRoutes.IdParam + "/terminate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Terminate(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new TerminateExecutionCommand(id), ct));

    /// <summary>Yalnız başarısız yürütme; aynı olay için yeni bir yürütme açar. Aksi <c>workflow.not_failed</c> (409).</summary>
    [HttpPost(ApiRoutes.IdParam + "/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new RetryExecutionCommand(id), ct));
}

/// <summary>Onay kararı gövdesi: <c>{ decision: "approve"|"reject", comment? }</c>; reddetmede <c>comment</c> zorunlu.</summary>
public sealed record DecideApprovalRequest(ApprovalDecision? Decision, string? Comment);

/// <summary>Onaylar: listele (<c>mine</c>), oku, karar ver, bekleyen sayısı.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(WorkflowsRoutes.Approvals)]
[Authorize]
public sealed class ApprovalsController : ApiControllerBase
{
    /// <summary><c>mine=true</c> (varsayılan): çağıranın onayları (ek izin gerekmez); <c>mine=false</c>: tümü (<c>org.workflows.manage</c>).</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<ApprovalDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] ApprovalStatus? status, [FromQuery] bool? mine, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListApprovalsQuery(paging, status, mine ?? true), ct));

    /// <summary>Çağıranın bekleyen onay sayısı; kimliği doğrulanmış her üye için 200.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<ApprovalSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetApprovalSummaryQuery(), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetApprovalQuery(id), ct));

    /// <summary>
    /// Karar verir (<c>crm.approvals.decide</c> + onay çağırana ait olmalı). Zaten karar verilmiş/iptal → <c>approval.already_decided</c>
    /// (409); reddetmede yorum zorunlu (<c>validation</c>, alan <c>comment</c>). Kararla workflow ilerler.
    /// </summary>
    [HttpPost(ApiRoutes.IdParam + "/decision")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideApprovalRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new DecideApprovalCommand(id, request.Decision, request.Comment), ct));
}
