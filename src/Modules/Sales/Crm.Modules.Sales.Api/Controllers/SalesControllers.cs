using Asp.Versioning;
using Crm.Modules.Sales.Application;
using Crm.Modules.Sales.Application.Accounts;
using Crm.Modules.Sales.Application.Contacts;
using Crm.Modules.Sales.Application.Deals;
using Crm.Modules.Sales.Application.Leads;
using Crm.Modules.Sales.Application.Pipelines;
using Crm.Modules.Sales.Domain.Leads;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Modules.Sales.Api.Controllers;

/// <summary>Sales uç noktaları (docs/plan/m2-api-kontrat.md). Taban: /api/v1. Yetki her sorgu/komutun [RequiresPermission]'ındadır.</summary>
public static class SalesRoutes
{
    public const string Accounts = ApiRoutes.VersionedBase + "/accounts";
    public const string Contacts = ApiRoutes.VersionedBase + "/contacts";
    public const string Leads = ApiRoutes.VersionedBase + "/leads";
    public const string Pipelines = ApiRoutes.VersionedBase + "/pipelines";
    public const string Deals = ApiRoutes.VersionedBase + "/deals";
}

public sealed record AccountRequest(
    string Name,
    string? Industry,
    string? Website,
    string? Phone,
    string? Email,
    AddressDto? BillingAddress,
    string? Description,
    Guid? OwnerUserId);

public sealed record ContactRequest(
    string? FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? Title,
    Guid? AccountId,
    AddressDto? MailingAddress,
    Guid? OwnerUserId);

public sealed record LeadRequest(
    string? FirstName,
    string LastName,
    string Company,
    string? Email,
    string? Phone,
    LeadSource? Source,
    LeadStatus? Status,
    LeadRating? Rating,
    Guid? OwnerUserId);

public sealed record ConvertLeadRequest(Guid? AccountId, bool CreateDeal, string? DealName, decimal? Amount, DateOnly? ClosingDate, Guid? PipelineId);

public sealed record CreatePipelineRequest(string Name);

public sealed record UpdatePipelineRequest(string Name, bool IsDefault);

public sealed record StagesRequest(IReadOnlyList<StageInput> Stages);

public sealed record DealRequest(
    string Name,
    Guid AccountId,
    Guid? ContactId,
    Guid? PipelineId,
    Guid? StageId,
    decimal? Amount,
    string? Currency,
    DateOnly? ClosingDate,
    Guid? OwnerUserId,
    string? LostReason);

public sealed record DealStageRequest(Guid StageId, string? LostReason);

/// <summary>Firmalar. Silme yumuşaktır; bağlı kişi/fırsat varsa <c>account.has_dependents</c> (409).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(SalesRoutes.Accounts)]
[Authorize]
public sealed class AccountsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<AccountDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] Guid? ownerUserId, [FromQuery] string? industry, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListAccountsQuery(paging, ownerUserId, industry), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetAccountQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<AccountDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] AccountRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateAccountCommand(request.Name, request.Industry, request.Website, request.Phone, request.Email, request.BillingAddress, request.Description, request.OwnerUserId),
            ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetAccountQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] AccountRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateAccountCommand(id, request.Name, request.Industry, request.Website, request.Phone, request.Email, request.BillingAddress, request.Description, request.OwnerUserId),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteAccountCommand(id), ct));

    [HttpGet(ApiRoutes.IdParam + "/contacts")]
    [ProducesResponseType<IReadOnlyList<ContactDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Contacts(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new ListAccountContactsQuery(id), ct));

    [HttpGet(ApiRoutes.IdParam + "/deals")]
    [ProducesResponseType<IReadOnlyList<DealDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deals(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new ListAccountDealsQuery(id), ct));
}

[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(SalesRoutes.Contacts)]
[Authorize]
public sealed class ContactsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ContactDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] Guid? accountId, [FromQuery] Guid? ownerUserId, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListContactsQuery(paging, accountId, ownerUserId), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<ContactDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetContactQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<ContactDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] ContactRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateContactCommand(request.FirstName, request.LastName, request.Email, request.Phone, request.Mobile, request.Title, request.AccountId, request.MailingAddress, request.OwnerUserId),
            ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetContactQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ContactRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateContactCommand(id, request.FirstName, request.LastName, request.Email, request.Phone, request.Mobile, request.Title, request.AccountId, request.MailingAddress, request.OwnerUserId),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteContactCommand(id), ct));
}

/// <summary>Potansiyel müşteriler ve dönüştürme. Dönüşmüş lead güncellenemez (<c>lead.already_converted</c>, 409).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(SalesRoutes.Leads)]
[Authorize]
public sealed class LeadsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<LeadDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] LeadStatus? status,
        [FromQuery] LeadSource? source,
        [FromQuery] Guid? ownerUserId,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListLeadsQuery(paging, status, source, ownerUserId), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<LeadDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetLeadQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<LeadDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] LeadRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateLeadCommand(request.FirstName, request.LastName, request.Company, request.Email, request.Phone, request.Source, request.Rating, request.OwnerUserId),
            ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetLeadQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] LeadRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateLeadCommand(id, request.FirstName, request.LastName, request.Company, request.Email, request.Phone, request.Source, request.Status, request.Rating, request.OwnerUserId),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteLeadCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/convert")]
    [ProducesResponseType<ConvertLeadResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Convert(Guid id, [FromBody] ConvertLeadRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new ConvertLeadCommand(id, request.AccountId, request.CreateDeal, request.DealName, request.Amount, request.ClosingDate, request.PipelineId),
            ct));
}

/// <summary>Satış hunileri. Okuma <c>crm.deals.read</c>; değiştirme <c>org.settings.manage</c>.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(SalesRoutes.Pipelines)]
[Authorize]
public sealed class PipelinesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PipelineDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListPipelinesQuery(), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetPipelineQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<PipelineDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreatePipelineRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(new CreatePipelineCommand(request.Name), ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetPipelineQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePipelineRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdatePipelineCommand(id, request.Name, request.IsDefault), ct));

    [HttpPut(ApiRoutes.IdParam + "/stages")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ReplaceStages(Guid id, [FromBody] StagesRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new ReplaceStagesCommand(id, request.Stages ?? []), ct));
}

/// <summary>Fırsatlar, kanban panosu ve aşama değişimi.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(SalesRoutes.Deals)]
[Authorize]
public sealed class DealsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<DealDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] Guid? pipelineId,
        [FromQuery] Guid? stageId,
        [FromQuery] StageKind? stageKind,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] Guid? accountId,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListDealsQuery(paging, new DealFilter(pipelineId, stageId, stageKind, ownerUserId, accountId)), ct));

    /// <summary>Kanban: her aşama için toplam tutar, adet ve en çok 100 fırsat özeti.</summary>
    [HttpGet("board")]
    [ProducesResponseType<DealBoardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Board([FromQuery] Guid? pipelineId, [FromQuery] Guid? ownerUserId, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetDealBoardQuery(pipelineId, ownerUserId), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<DealDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetDealQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<DealDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] DealRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateDealCommand(
                request.Name,
                request.AccountId,
                request.ContactId,
                request.PipelineId,
                request.StageId,
                request.Amount,
                request.Currency,
                request.ClosingDate,
                request.OwnerUserId,
                request.LostReason),
            ct);
        return created.IsFailure ? Problem(created.Error) : Created(await Dispatcher.Query(new GetDealQuery(created.Value), ct), nameof(Get), new { id = created.Value });
    }

    /// <summary>Aşama dışındaki alanlar; aşama <c>POST /deals/{id}/stage</c> ile değişir.</summary>
    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] DealRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateDealCommand(id, request.Name, request.AccountId, request.ContactId, request.Amount, request.Currency, request.ClosingDate, request.OwnerUserId, request.LostReason),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteDealCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/stage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MoveStage(Guid id, [FromBody] DealStageRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new MoveDealStageCommand(id, request.StageId, request.LostReason), ct));
}
