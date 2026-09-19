using Asp.Versioning;
using Crm.Modules.Sales.Application.Reports;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Modules.Sales.Api.Controllers;

/// <summary>
/// Satış raporları (docs/plan/m3-aktivite-rapor.md), izin <c>crm.reports.read</c>. <c>from</c>/<c>to</c> <c>YYYY-MM-DD</c> (uçlar dahil,
/// kiracı saat diliminde takvim günü); verilmezse son 12 ay.
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(SalesRoutes.SalesReports)]
[Authorize]
public sealed class SalesReportsController : ApiControllerBase
{
    /// <summary>Huni: her aşamada güncel fırsat sayısı ve toplam tutar (aralık yok, güncel durum).</summary>
    [HttpGet("funnel")]
    [ProducesResponseType<FunnelDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Funnel([FromQuery] Guid? pipelineId, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetFunnelReportQuery(pipelineId), ct));

    /// <summary>Kazanılan/kaybedilen fırsatlar: <c>closedAt</c>'e göre ay veya ISO hafta, boş dönemler 0 ile doldurulur.</summary>
    [HttpGet("won-lost")]
    [ProducesResponseType<IReadOnlyList<WonLostPeriodDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> WonLost([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] WonLostGroupBy? groupBy, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetWonLostReportQuery(from, to, groupBy ?? WonLostGroupBy.Month), ct));

    /// <summary>Potansiyel kaynakları: <c>createdAt</c>'e göre kaynak başına toplam ve dönüşen.</summary>
    [HttpGet("leads-by-source")]
    [ProducesResponseType<IReadOnlyList<LeadSourceReportRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LeadsBySource([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetLeadsBySourceReportQuery(from, to), ct));

    /// <summary>Satış temsilcisi: açık fırsat (güncel), kazanılan ve potansiyel sayıları.</summary>
    [HttpGet("by-owner")]
    [ProducesResponseType<IReadOnlyList<OwnerReportRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByOwner([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetByOwnerReportQuery(from, to), ct));
}
