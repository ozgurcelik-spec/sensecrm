using Asp.Versioning;
using Crm.Modules.Service.Application;
using Crm.Modules.Service.Application.Cases;
using Crm.Modules.Service.Application.Reports;
using Crm.Modules.Service.Application.Sla;
using Crm.Modules.Service.Domain.Cases;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Modules.Service.Api.Controllers;

/// <summary>Service uç noktaları (docs/plan/m6b-servis.md). Taban: /api/v1. Yetki her sorgu/komutun [RequiresPermission]'ındadır.</summary>
public static class ServiceRoutes
{
    public const string Cases = ApiRoutes.VersionedBase + "/cases";
    public const string SlaPolicies = ApiRoutes.VersionedBase + "/service/sla-policies";
    public const string ServiceReports = ApiRoutes.VersionedBase + "/reports/service";
}

/// <summary>Talep oluşturma gövdesi. <c>subject</c> zorunlu; <c>priority</c> varsayılan <c>normal</c>, <c>channel</c> varsayılan <c>other</c>; <c>assignedUserId</c> verilmezse atanmamış.</summary>
public sealed record CreateCaseRequest(
    string? Subject,
    string? Description,
    Guid? AccountId,
    Guid? ContactId,
    CasePriority? Priority,
    CaseChannel? Channel,
    Guid? AssignedUserId);

/// <summary>Talep güncelleme gövdesi (tam değiştirme; gönderilmeyen isteğe bağlı alan temizlenir, <c>channel</c> verilmezse korunur).</summary>
public sealed record UpdateCaseRequest(string? Subject, string? Description, Guid? AccountId, Guid? ContactId, CaseChannel? Channel);

public sealed record ChangeCaseStatusRequest(CaseStatus? Status, string? ResolutionNote);

public sealed record ChangeCasePriorityRequest(CasePriority? Priority);

public sealed record AssignCaseRequest(Guid? AssignedUserId);

public sealed record AddCaseCommentRequest(CommentVisibility? Visibility, string? Body);

public sealed record UpdateSlaPoliciesRequest(IReadOnlyList<SlaPolicyInput>? Policies);

/// <summary>Talepler: numaralı, durum makineli, yorumlu ve SLA süreli müşteri sorunları. Silme yumuşaktır; bağlı firma/kişi silinse de talep kalır.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(ServiceRoutes.Cases)]
[Authorize]
public sealed class CasesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<CaseListItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] CaseChannel? channel,
        [FromQuery] Guid? assignedUserId,
        [FromQuery] bool? unassigned,
        [FromQuery] Guid? accountId,
        [FromQuery] Guid? contactId,
        [FromQuery] string? slaState,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListCasesQuery(paging, status, priority, channel, assignedUserId, unassigned, accountId, contactId, slaState), ct));

    /// <summary>Ana sayfa sayaçları: açık (aktif), geciken, bana atanan, atanmamış.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<CaseSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetCaseSummaryQuery(), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<CaseDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetCaseQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<CaseDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateCaseRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateCaseCommand(
                request.Subject ?? string.Empty,
                request.Description,
                request.AccountId,
                request.ContactId,
                request.Priority,
                request.Channel,
                request.AssignedUserId),
            ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetCaseQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCaseRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateCaseCommand(id, request.Subject ?? string.Empty, request.Description, request.AccountId, request.ContactId, request.Channel),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteCaseCommand(id), ct));

    /// <summary>Durum geçişi. Tablo dışı → <c>case.invalid_transition</c>; çözme/çözülmeden kapatmada not zorunlu; aynı durum idempotent.</summary>
    [HttpPost(ApiRoutes.IdParam + "/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeCaseStatusRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new ChangeCaseStatusCommand(id, request.Status, request.ResolutionNote), ct));

    /// <summary>Öncelik değişimi; SLA hedefleri özgün başlangıçtan yeniden hesaplanır.</summary>
    [HttpPost(ApiRoutes.IdParam + "/priority")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangePriority(Guid id, [FromBody] ChangeCasePriorityRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new ChangeCasePriorityCommand(id, request.Priority), ct));

    /// <summary>Atama (<c>assignedUserId: null</c> = atamayı kaldır).</summary>
    [HttpPost(ApiRoutes.IdParam + "/assign")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignCaseRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new AssignCaseCommand(id, request.AssignedUserId), ct));

    /// <summary>Yorum ekler (<c>visibility</c> açıkça verilir). Herkese açık ilk yorum ilk yanıttır ve <c>new</c> talebi <c>open</c> yapar.</summary>
    [HttpPost(ApiRoutes.IdParam + "/comments")]
    [ProducesResponseType<CaseCommentDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddCaseCommentRequest request, CancellationToken ct) =>
        CreatedWithBody(await Dispatcher.Send(new AddCaseCommentCommand(id, request.Visibility, request.Body ?? string.Empty), ct));

    /// <summary>Yorumlar + olaylar birleşik zaman çizelgesi, en yeni önce.</summary>
    [HttpGet(ApiRoutes.IdParam + "/timeline")]
    [ProducesResponseType<PagedResult<TimelineItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Timeline(Guid id, [FromQuery] PagedQuery paging, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetCaseTimelineQuery(id, paging), ct));
}

/// <summary>SLA politikaları (<c>org.settings.manage</c>): öncelik başına ilk yanıt ve çözüm süresi (duvar saati dakikası).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(ServiceRoutes.SlaPolicies)]
[Authorize]
public sealed class SlaPoliciesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SlaPolicyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListSlaPoliciesQuery(), ct));

    /// <summary>Dört önceliğin tamamını değiştirir; yalnız yeni hesaplamaları (oluşturma, öncelik değişimi, yeniden açma) etkiler.</summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update([FromBody] UpdateSlaPoliciesRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateSlaPoliciesCommand(request.Policies), ct));
}

/// <summary>Servis raporları (<c>crm.reports.read</c>): özet ve temsilci bazlı.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(ServiceRoutes.ServiceReports)]
[Authorize]
public sealed class ServiceReportsController : ApiControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType<ServiceSummaryReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetServiceSummaryReportQuery(from, to), ct));

    [HttpGet("by-assignee")]
    [ProducesResponseType<IReadOnlyList<AssigneeReportRowDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByAssignee([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetServiceByAssigneeReportQuery(from, to), ct));
}
