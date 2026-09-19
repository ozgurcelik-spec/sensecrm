using Asp.Versioning;
using Crm.Modules.Marketing.Application;
using Crm.Modules.Marketing.Application.Campaigns;
using Crm.Modules.Marketing.Application.Members;
using Crm.Modules.Marketing.Application.Reports;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Domain.Campaigns;
using Crm.Modules.Marketing.Domain.Members;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Modules.Marketing.Api.Controllers;

/// <summary>Marketing uç noktaları (docs/plan/m6c-pazarlama.md). Taban: /api/v1. Yetki her sorgu/komutun [RequiresPermission]'ındadır.</summary>
public static class MarketingRoutes
{
    public const string Campaigns = ApiRoutes.VersionedBase + "/campaigns";
    public const string MarketingReports = ApiRoutes.VersionedBase + "/reports/marketing";
}

/// <summary>
/// Kampanya oluşturma gövdesi. <c>name</c> ve <c>type</c> zorunlu; <c>status</c> verilmezse <c>planned</c> (yalnız <c>planned</c>/<c>active</c>);
/// <c>currency</c> verilmezse <c>TRY</c>; <c>ownerUserId</c> verilmezse çağıran. Tarihler <c>YYYY-MM-DD</c>.
/// </summary>
public sealed record CreateCampaignRequest(
    string Name,
    CampaignType? Type,
    CampaignStatus? Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Currency,
    decimal? Budget,
    decimal? ExpectedRevenue,
    decimal? ActualCost,
    string? Description,
    Guid? OwnerUserId);

/// <summary>Kampanya güncelleme gövdesi (PUT, tam değiştirme; durum bu uçtan değişmez: <c>POST /campaigns/{id}/status</c>).</summary>
public sealed record UpdateCampaignRequest(
    string Name,
    CampaignType? Type,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Currency,
    decimal? Budget,
    decimal? ExpectedRevenue,
    decimal? ActualCost,
    string? Description,
    Guid? OwnerUserId);

public sealed record CampaignStatusRequest(CampaignStatus? Status);

/// <summary>Toplu üye ekleme gövdesi: 1–500 lead/kişi kimliği (tek üye = tek elemanlı dizi).</summary>
public sealed record AddMembersRequest(CampaignMemberType? MemberType, IReadOnlyList<Guid>? MemberIds);

/// <summary>Toplu üye durumu gövdesi: <c>memberIds</c> üyelik <b>satır</b> kimlikleridir.</summary>
public sealed record SetMemberStatusRequest(IReadOnlyList<Guid>? MemberIds, CampaignMemberStatus? Status);

public sealed record RemoveMembersRequest(IReadOnlyList<Guid>? MemberIds);

/// <summary>Kampanyalar ve kampanya üyeleri. Silme yumuşaktır; üyelikler kalır ama hiçbir uçtan görünmez.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(MarketingRoutes.Campaigns)]
[Authorize]
public sealed class CampaignsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<CampaignDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] DateOnly? startFrom,
        [FromQuery] DateOnly? startTo,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListCampaignsQuery(paging, type, status, ownerUserId, startFrom, startTo), ct));

    /// <summary>Bir lead/kişinin kampanya üyelikleri (en çok 200, <c>addedAt</c> azalan; yalnız silinmemiş kampanyalar). Kayıt yoksa <c>not_found</c>.</summary>
    [HttpGet("by-member")]
    [ProducesResponseType<IReadOnlyList<RecordCampaignDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByMember([FromQuery] CampaignMemberType? memberType, [FromQuery] Guid? memberId, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetRecordCampaignsQuery(memberType, memberId), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetCampaignQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<CampaignDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateCampaignRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateCampaignCommand(
                request.Name,
                request.Type,
                request.Status,
                request.StartDate,
                request.EndDate,
                request.Currency,
                request.Budget,
                request.ExpectedRevenue,
                request.ActualCost,
                request.Description,
                request.OwnerUserId),
            ct);
        return created.IsFailure ? CampaignProblem(created.Error) : Created(await Dispatcher.Query(new GetCampaignQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCampaignRequest request, CancellationToken ct)
    {
        var updated = await Dispatcher.Send(
            new UpdateCampaignCommand(
                id,
                request.Name,
                request.Type,
                request.StartDate,
                request.EndDate,
                request.Currency,
                request.Budget,
                request.ExpectedRevenue,
                request.ActualCost,
                request.Description,
                request.OwnerUserId),
            ct);
        return updated.IsFailure ? CampaignProblem(updated.Error) : NoContent();
    }

    /// <summary>Durum geçişi (planned → active|cancelled, active → completed|cancelled, completed → active, cancelled → planned); aynı durum no-op.</summary>
    [HttpPost(ApiRoutes.IdParam + "/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] CampaignStatusRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new ChangeCampaignStatusCommand(id, request.Status), ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteCampaignCommand(id), ct));

    /// <summary>Kampanya metrikleri (hesaplanır, saklanmaz): üye sayıları, yanıt/dönüşüm oranı, potansiyel başına maliyet.</summary>
    [HttpGet(ApiRoutes.IdParam + "/metrics")]
    [ProducesResponseType<CampaignMetricsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Metrics(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetCampaignMetricsQuery(id), ct));

    [HttpGet(ApiRoutes.IdParam + "/members")]
    [ProducesResponseType<PagedResult<CampaignMemberDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMembers(
        Guid id,
        [FromQuery] PagedQuery paging,
        [FromQuery] CampaignMemberType? memberType,
        [FromQuery] string? status,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListCampaignMembersQuery(id, paging, memberType, status), ct));

    /// <summary>Toplu üye ekleme (idempotent; 1–500 kimlik). Yanıt 200: <c>addedCount</c>, <c>alreadyMemberCount</c>, <c>skipped</c>.</summary>
    [HttpPost(ApiRoutes.IdParam + "/members")]
    [ProducesResponseType<AddMembersResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddMembers(Guid id, [FromBody] AddMembersRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new AddCampaignMembersCommand(id, request.MemberType, request.MemberIds), ct));

    [HttpPost(ApiRoutes.IdParam + "/members/status")]
    [ProducesResponseType<SetMemberStatusResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetMemberStatus(Guid id, [FromBody] SetMemberStatusRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new SetCampaignMemberStatusCommand(id, request.MemberIds, request.Status), ct));

    [HttpPost(ApiRoutes.IdParam + "/members/remove")]
    [ProducesResponseType<RemoveMembersResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveMembers(Guid id, [FromBody] RemoveMembersRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RemoveCampaignMembersCommand(id, request.MemberIds), ct));

    /// <summary>
    /// Hata eşlemesi: <c>campaign.invalid_date_range</c> (400) ayrıca <c>errors.endDate</c> taşır (sözleşme); diğer hatalar ortak eşlemedir.
    /// </summary>
    private IActionResult CampaignProblem(Error error)
    {
        var result = Problem(error);
        if (error.Code == MarketingErrors.InvalidDateRange && result is ObjectResult { Value: ProblemDetails problem })
        {
            problem.Extensions["errors"] = new Dictionary<string, string[]> { ["endDate"] = [problem.Detail ?? error.Code] };
        }

        return result;
    }
}

/// <summary>Pazarlama raporu (<c>crm.reports.read</c>): durum/tür dağılımı, toplamlar ve en iyi kampanyalar.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(MarketingRoutes.MarketingReports)]
[Authorize]
public sealed class MarketingReportsController : ApiControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType<MarketingSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetMarketingSummaryQuery(from, to), ct));
}
