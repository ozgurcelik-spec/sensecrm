using Asp.Versioning;
using Crm.Modules.Activities.Application;
using Crm.Modules.Activities.Application.Activities;
using Crm.Modules.Activities.Application.Reports;
using Crm.Modules.Activities.Domain.Activities;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Modules.Activities.Api.Controllers;

/// <summary>Activities uç noktaları (docs/plan/m3-aktivite-rapor.md). Taban: /api/v1. Yetki her sorgu/komutun [RequiresPermission]'ındadır.</summary>
public static class ActivitiesRoutes
{
    public const string Activities = ApiRoutes.VersionedBase + "/activities";
    public const string ActivityReports = ApiRoutes.VersionedBase + "/reports/activities";
}

/// <summary>
/// Aktivite gövdesi (POST/PUT). <c>type</c> ve <c>subject</c> zorunlu; <c>status</c> verilmezse oluştururken <c>open</c> (not: <c>completed</c>),
/// güncellerken mevcut durum korunur; <c>assignedUserId</c> verilmezse çağıran (güncellemede mevcut atanan); <c>relatedType</c> ve
/// <c>relatedId</c> birlikte verilir. Tarih-saatler ISO 8601 (UTC).
/// </summary>
public sealed record ActivityRequest(
    ActivityType? Type,
    string Subject,
    string? Description,
    ActivityStatus? Status,
    ActivityPriority? Priority,
    DateTime? DueAt,
    DateTime? StartAt,
    DateTime? EndAt,
    ActivityRelatedType? RelatedType,
    Guid? RelatedId,
    Guid? AssignedUserId);

/// <summary>Aktiviteler: görev/arama/toplantı/not. Silme yumuşaktır; bağlı kayıt silinse de aktivite kalır (<c>relatedName</c> boş döner).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(ActivitiesRoutes.Activities)]
[Authorize]
public sealed class ActivitiesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ActivityDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] Guid? assignedUserId,
        [FromQuery] ActivityType? type,
        [FromQuery] ActivityStatus? status,
        [FromQuery] ActivityRelatedType? relatedType,
        [FromQuery] Guid? relatedId,
        [FromQuery] DateTime? dueFrom,
        [FromQuery] DateTime? dueTo,
        [FromQuery] bool? overdue,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(
            new ListActivitiesQuery(paging, new ActivityFilter(assignedUserId, type, status, relatedType, relatedId, dueFrom, dueTo, overdue)),
            ct));

    /// <summary>Kişisel iş özeti (<c>assignedUserId</c> yoksa çağıran): kiracı saat dilimine göre bugün ve hafta başı (pazartesi).</summary>
    [HttpGet("summary")]
    [ProducesResponseType<ActivitySummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] Guid? assignedUserId, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetActivitySummaryQuery(assignedUserId), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<ActivityDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetActivityQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<ActivityDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] ActivityRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateActivityCommand(
                request.Type,
                request.Subject,
                request.Description,
                request.Status,
                request.Priority,
                request.DueAt,
                request.StartAt,
                request.EndAt,
                request.RelatedType,
                request.RelatedId,
                request.AssignedUserId),
            ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetActivityQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ActivityRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateActivityCommand(
                id,
                request.Type,
                request.Subject,
                request.Description,
                request.Status,
                request.Priority,
                request.DueAt,
                request.StartAt,
                request.EndAt,
                request.RelatedType,
                request.RelatedId,
                request.AssignedUserId),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteActivityCommand(id), ct));

    /// <summary>Tamamlar (idempotent). Not için <c>activity.note_status_fixed</c> (409).</summary>
    [HttpPost(ApiRoutes.IdParam + "/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new CompleteActivityCommand(id), ct));

    /// <summary>Yeniden açar (idempotent). Not için <c>activity.note_status_fixed</c> (409).</summary>
    [HttpPost(ApiRoutes.IdParam + "/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new ReopenActivityCommand(id), ct));
}

/// <summary>Aktivite raporu (<c>crm.reports.read</c>): kullanıcı bazında tamamlanan/açık/geciken.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(ActivitiesRoutes.ActivityReports)]
[Authorize]
public sealed class ActivityReportsController : ApiControllerBase
{
    [HttpGet("by-user")]
    [ProducesResponseType<IReadOnlyList<ActivityUserReportRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByUser([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetActivitiesByUserReportQuery(from, to), ct));
}
